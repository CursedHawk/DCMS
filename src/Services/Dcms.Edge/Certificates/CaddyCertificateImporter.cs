using System.Security.Cryptography.X509Certificates;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Copies the certificates Caddy already holds into <c>edge.certificates</c>, once, before the
/// edge takes the public ports.
///
/// <para><b>Without this the cutover is an outage.</b> The moment the edge owns :443, every live
/// tenant domain would have no certificate — so every one of them would have to be issued on
/// demand, inside a visitor's TLS handshake, serially, all at the same minute. Some would time
/// out; the rest would arrive at Let's Encrypt as a burst that trips the weekly limit for the
/// whole registered domain and blocks the ones that were still queued. Importing turns the
/// cutover into a port swap.</para>
///
/// <para>Caddy's layout is
/// <c>/data/caddy/certificates/&lt;ca-directory&gt;/&lt;name&gt;/&lt;name&gt;.crt</c> with a
/// sibling <c>.key</c>. The hostname is taken from the certificate's own SAN rather than from
/// the directory name: the directory is a filename-safe encoding (a wildcard becomes
/// <c>wildcard_.example.com</c>), and reading it back is guesswork where the certificate itself
/// is authoritative.</para>
///
/// <para>Idempotent, and non-fatal in every failure mode. A hostname that already has a row is
/// left alone — a certificate this platform issued is never replaced by one found on disk. An
/// unreadable file, an absent directory or an unparseable certificate is logged and skipped: the
/// edge starting is more important than the import completing, and anything missed is issued the
/// ordinary way.</para>
/// </summary>
public sealed class CaddyCertificateImporter(
    IServiceProvider services,
    ICertificateStore store,
    ILogger<CaddyCertificateImporter> logger)
{
    public async Task ImportAsync(string caddyDataPath, CancellationToken ct)
    {
        var root = Path.Combine(caddyDataPath, "caddy", "certificates");
        if (!Directory.Exists(root))
        {
            // Not a warning. The path is only mounted for the one deploy that performs the
            // cutover; every deploy after it correctly finds nothing.
            logger.LogInformation(
                "No Caddy certificate store at {Path}; nothing to import.", root);
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var existing = await db.Certificates.AsNoTracking()
            .Select(c => c.Hostname)
            .ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        var skipped = 0;

        foreach (var crtPath in Directory.EnumerateFiles(root, "*.crt", SearchOption.AllDirectories))
        {
            var keyPath = Path.ChangeExtension(crtPath, ".key");
            if (!File.Exists(keyPath))
            {
                logger.LogWarning("Skipping {Path}: no matching private key.", crtPath);
                skipped++;
                continue;
            }

            try
            {
                var chainPem = await File.ReadAllTextAsync(crtPath, ct);
                var (leaf, _) = CertificateStore.ParseChain(chainPem);
                if (leaf is null)
                {
                    logger.LogWarning("Skipping {Path}: no certificate in the file.", crtPath);
                    skipped++;
                    continue;
                }

                var names = SubjectAlternativeNames(leaf);
                if (names.Count != 1)
                {
                    // The store is keyed by one hostname, so a multi-SAN or wildcard certificate
                    // has no single row to become. Left for the ordinary issuance path, and for
                    // the managed-zone wildcard work, which needs a different shape anyway.
                    logger.LogInformation(
                        "Skipping {Path}: {Count} subject names, and the store is keyed by one.",
                        crtPath, names.Count);
                    skipped++;
                    continue;
                }

                var hostname = CertificateStore.Normalize(names[0]);
                if (known.Contains(hostname))
                {
                    skipped++;
                    continue;
                }

                if (leaf.NotAfter <= DateTime.UtcNow)
                {
                    logger.LogInformation("Skipping {Hostname}: the certificate has expired.", hostname);
                    skipped++;
                    continue;
                }

                var keyPem = await File.ReadAllTextAsync(keyPath, ct);
                await store.SaveAsync(hostname, chainPem, keyPem, CertificateSource.DcmsManaged, ct);
                known.Add(hostname);
                imported++;
            }
            catch (Exception ex)
            {
                // Never fatal. One bad file must not stop the edge from starting, and whatever it
                // held is reissued the ordinary way.
                logger.LogError(ex, "Could not import the Caddy certificate at {Path}.", crtPath);
                skipped++;
            }
        }

        logger.LogInformation(
            "Caddy certificate import: {Imported} imported, {Skipped} skipped.", imported, skipped);
    }

    /// <summary>
    /// The DNS names in the leaf's SAN extension, which is what a browser matches against — the
    /// common name has not been authoritative for well over a decade.
    /// </summary>
    private static List<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                return [.. san.EnumerateDnsNames()];
            }
        }
        return [];
    }
}
