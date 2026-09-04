using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Edge.Certificates;

/// <summary>
/// The certificate a TLS handshake is served from: an in-memory cache over
/// <c>edge.certificates</c>, with the private key decrypted through Vault Transit on load.
///
/// <para>Cached because this is on the handshake path. A database round trip and a Vault decrypt
/// per connection would put both on the critical path of every new visitor, and make Vault being
/// briefly unreachable indistinguishable from the platform being down. Entries are held until
/// invalidated — a certificate does not change except when this process or a sibling replaces
/// it, and both paths invalidate.</para>
/// </summary>
public sealed class CertificateStore(
    IServiceProvider services,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<CertificateStore> logger) : ICertificateStore
{
    /// <summary>
    /// Resolved per call rather than injected, so a Vault that is unconfigured or unreachable
    /// cannot stop this process starting. The edge is the public ingress: it must come up and
    /// route traffic even when the thing that decrypts certificates is having a bad day. What
    /// fails then is issuance and cold certificate loads — not the admin host someone would use
    /// to find out why.
    /// </summary>
    private ITransitEncryptor Transit => services.GetRequiredService<ITransitEncryptor>();

    /// <summary>
    /// A miss is cached too, briefly. Without this an unknown hostname — a scanner walking IP
    /// space, which the public edge sees constantly — costs a database query per connection.
    /// </summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The handshake credential for a hostname, or null if there is none to serve.
    ///
    /// <para>An <see cref="SslStreamCertificateContext"/> rather than a bare certificate, because
    /// the context is what carries the issuing chain. A client that does not already hold the
    /// intermediate cannot build a path to the root without it, and the symptom is a site that
    /// works in every browser the developer tried and fails on some mobile client — a miserable
    /// thing to debug. Built once at load rather than per handshake: constructing it walks and
    /// validates the chain, which is not work to repeat per connection.</para>
    /// </summary>
    public async Task<SslStreamCertificateContext?> GetAsync(string hostname, CancellationToken ct)
    {
        var key = CacheKey(Normalize(hostname));
        if (cache.TryGetValue<CachedCertificate?>(key, out var cached))
        {
            return cached?.Context;
        }

        var loaded = await LoadAsync(hostname, ct);
        if (loaded is null)
        {
            cache.Set<CachedCertificate?>(key, null, NegativeTtl);
            return null;
        }

        // Expires when the certificate does, so a renewal performed by a sibling replica is
        // picked up without waiting for an invalidation message.
        cache.Set(key, loaded, loaded.NotAfter);
        return loaded.Context;
    }

    private sealed record CachedCertificate(SslStreamCertificateContext Context, DateTimeOffset NotAfter);

    /// <summary>
    /// Stores a newly issued or uploaded certificate. The private key is encrypted before it
    /// touches the database; it is never written in plaintext, and this is the only place that
    /// decides so.
    /// </summary>
    public async Task SaveAsync(
        string hostname, string pemChain, string pemPrivateKey, CertificateSource source, CancellationToken ct)
    {
        var normalized = Normalize(hostname);
        var leaf = ParseChain(pemChain).leaf
                   ?? throw new InvalidOperationException($"No certificate found in the PEM chain for {normalized}.");

        var encryptedKey = await Transit.EncryptAsync(
            VaultTransitServiceCollectionExtensions.TlsKeysKey,
            System.Text.Encoding.UTF8.GetBytes(pemPrivateKey),
            ct);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var row = await db.Certificates.FirstOrDefaultAsync(c => c.Hostname == normalized, ct);
        if (row is null)
        {
            row = new EdgeCertificate { Hostname = normalized };
            db.Certificates.Add(row);
        }

        row.PemChain = pemChain;
        row.EncryptedPrivateKey = encryptedKey;
        row.NotBefore = leaf.NotBefore;
        row.NotAfter = leaf.NotAfter;
        row.Issuer = leaf.Issuer;
        row.Source = source;
        row.RenewedAt = clock.GetUtcNow();
        row.LastAttemptAt = clock.GetUtcNow();
        row.LastError = null;
        row.ConsecutiveFailures = 0;
        // Whatever asked for this is now satisfied, whichever path got here -- the operator's
        // button, the event it published, or the sweep that would have caught it within the hour.
        row.ReissueRequestedAt = null;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        Invalidate(normalized);
        logger.LogInformation(
            "Stored {Source} certificate for {Hostname}, valid until {NotAfter:u}.",
            source, normalized, row.NotAfter);
    }

    /// <summary>
    /// Records that an attempt failed, so the admin UI can say why and the backoff has something
    /// to count. A row is created for a hostname that has never had a certificate: "tried and
    /// failed with this error" is exactly the state the old edge could not express.
    /// </summary>
    public async Task RecordFailureAsync(string hostname, string error, CancellationToken ct)
    {
        var normalized = Normalize(hostname);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var row = await db.Certificates.FirstOrDefaultAsync(c => c.Hostname == normalized, ct);
        if (row is null)
        {
            row = new EdgeCertificate { Hostname = normalized };
            db.Certificates.Add(row);
        }

        row.LastAttemptAt = clock.GetUtcNow();
        // Truncated to the column width. The full exception is in the log with the trace id;
        // this is the sentence an operator reads next to the domain.
        row.LastError = error.Length > 2000 ? error[..2000] : error;
        row.ConsecutiveFailures++;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The backoff a hostname is currently serving, or null if it may be attempted now.</summary>
    public async Task<DateTimeOffset?> RetryNotBeforeAsync(string hostname, CertificateOptions options, CancellationToken ct)
    {
        var normalized = Normalize(hostname);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var row = await db.Certificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Hostname == normalized, ct);

        if (row?.LastAttemptAt is not { } lastAttempt || row.ConsecutiveFailures == 0)
        {
            return null;
        }

        // Doubling, capped at a day. Capped rather than unbounded so a domain that was broken
        // for a week and then fixed does not wait a month for its next attempt.
        var seconds = Math.Min(
            options.FailureBackoffSeconds * Math.Pow(2, Math.Min(row.ConsecutiveFailures - 1, 10)),
            TimeSpan.FromDays(1).TotalSeconds);
        var next = lastAttempt.AddSeconds(seconds);
        return next > clock.GetUtcNow() ? next : null;
    }

    public void Invalidate(string hostname) => cache.Remove(CacheKey(Normalize(hostname)));

    /// <summary>
    /// Whether an operator has asked for this hostname to be reissued before it is due.
    ///
    /// <para>Read straight from the row rather than cached: it is checked once per issuance
    /// attempt, not per handshake, and a cached "no" would make the reissue button do nothing
    /// for as long as the cache held — which is exactly the kind of button nobody trusts again.
    /// </para>
    /// </summary>
    public async Task<bool> IsReissueRequestedAsync(string hostname, CancellationToken ct)
    {
        var normalized = Normalize(hostname);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        return await db.Certificates.AsNoTracking()
            .AnyAsync(c => c.Hostname == normalized && c.ReissueRequestedAt != null, ct);
    }

    public async Task<int> ClearBackoffForNeverIssuedAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        // Loaded and tracked rather than an ExecuteUpdate, deliberately. A set-based statement
        // leaves no before-image -- the audit interceptor can say a table changed and not what
        // it held -- and the edge carries no audit recorder to make up the difference. The set
        // is bounded by the number of hostnames the platform serves and this runs once per
        // process start, so there is nothing to buy by going around the change tracker.
        var rows = await db.Certificates
            .Where(c => c.EncryptedPrivateKey == "" && c.ConsecutiveFailures > 0)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.ConsecutiveFailures = 0;
            row.LastError = null;
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<CachedCertificate?> LoadAsync(string hostname, CancellationToken ct)
    {
        var normalized = Normalize(hostname);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var row = await db.Certificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Hostname == normalized, ct);

        if (row is null || !row.IsUsable(clock.GetUtcNow()))
        {
            return null;
        }

        try
        {
            var keyPem = System.Text.Encoding.UTF8.GetString(
                await Transit.DecryptAsync(VaultTransitServiceCollectionExtensions.TlsKeysKey, row.EncryptedPrivateKey, ct));

            var (leaf, intermediates) = ParseChain(row.PemChain);
            if (leaf is null)
            {
                return null;
            }

            var withKey = X509Certificate2.CreateFromPem(leaf.ExportCertificatePem(), keyPem);

            // Round-tripped through PKCS#12. CreateFromPem hands back an ephemeral key, which
            // SslStream cannot always use as a server credential; exporting and re-loading
            // produces one it can. The alternative failure is a handshake that fails only
            // sometimes, which is the worst possible place to find this out.
            var serverCertificate = X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pkcs12), password: null);

            var context = SslStreamCertificateContext.Create(
                serverCertificate, [.. intermediates]);
            return new CachedCertificate(context, row.NotAfter);
        }
        catch (Exception ex)
        {
            // A certificate we cannot decrypt or parse is not a reason to fail the whole edge.
            // The handshake for this one hostname is refused; every other host keeps serving.
            logger.LogError(ex, "Certificate for {Hostname} could not be loaded.", normalized);
            return null;
        }
    }

    /// <summary>Splits a PEM bundle into the leaf and the intermediates that follow it.</summary>
    public static (X509Certificate2? leaf, X509Certificate2[] intermediates) ParseChain(string pemChain)
    {
        if (string.IsNullOrWhiteSpace(pemChain))
        {
            return (null, []);
        }

        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(pemChain);
        if (collection.Count == 0)
        {
            return (null, []);
        }

        return (collection[0], [.. collection.Cast<X509Certificate2>().Skip(1)]);
    }

    public static string Normalize(string host)
        => host.Split(':')[0].Trim().TrimEnd('.').ToLowerInvariant();

    private static string CacheKey(string hostname) => $"edgecert:{hostname}";
}
