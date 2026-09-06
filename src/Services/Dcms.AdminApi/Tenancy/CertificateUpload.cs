using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// The rules an uploaded certificate has to satisfy before the edge is allowed to serve it.
///
/// <para>Its own class rather than private helpers on the endpoint, because these are the rules
/// worth testing directly: every one of them exists to turn a failure that would otherwise
/// happen inside a TLS handshake on the tenant's live domain — where the only symptom is a
/// browser warning and the only explanation is a connection that already closed — into a
/// sentence somebody can act on at upload time.</para>
/// </summary>
public static class CertificateUpload
{
    /// <summary>A PEM chain (leaf first) and the matching private key.</summary>
    public sealed record Request(string PemChain, string PrivateKeyPem);

    /// <summary>
    /// Every way an uploaded pair can be wrong, checked here rather than at handshake time.
    ///
    /// <para>The alternative is finding out at 2am on the tenant's live domain, where the only
    /// symptom is a browser warning and the only place the reason exists is a TLS handshake that
    /// already failed. Each of these produces a sentence somebody can act on instead.</para>
    /// </summary>
    public static string? Validate(Request body, string hostname, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body.PemChain) || string.IsNullOrWhiteSpace(body.PrivateKeyPem))
        {
            return "Both the certificate chain and the private key are required.";
        }

        X509Certificate2? leaf;
        try
        {
            leaf = ParseLeaf(body.PemChain);
        }
        catch (CryptographicException)
        {
            return "The certificate chain is not valid PEM.";
        }
        if (leaf is null)
        {
            return "No certificate was found in the chain.";
        }

        if (leaf.NotAfter.ToUniversalTime() <= now)
        {
            return $"The certificate expired on {leaf.NotAfter:u}.";
        }
        if (leaf.NotBefore.ToUniversalTime() > now)
        {
            return $"The certificate is not valid until {leaf.NotBefore:u}.";
        }

        if (!CoversHostname(leaf, hostname))
        {
            // The common name has not been authoritative for over a decade, and neither has any
            // browser's opinion of it. Matching what a client actually matches means the upload
            // fails here rather than in front of a visitor.
            return $"The certificate does not cover {hostname}.";
        }

        try
        {
            using var withKey = X509Certificate2.CreateFromPem(
                leaf.ExportCertificatePem(), body.PrivateKeyPem);
            if (!withKey.HasPrivateKey)
            {
                return "The private key does not match the certificate.";
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // The single most common upload mistake, and the one whose runtime symptom is the
            // least legible: a handshake that fails with no server-side explanation at all.
            return "The private key does not match the certificate, or is not valid PEM.";
        }

        return null;
    }

    /// <summary>
    /// Every DNS name in the leaf's SAN extension, normalised.
    ///
    /// <para>Stored on the certificate row so an uploaded certificate describes what it covers
    /// the same way an issued one does — the edge's handshake lookup falls back to this list,
    /// and the admin UI shows it. Without it an upload covering several names would be found
    /// only under the one domain it was attached to.</para>
    /// </summary>
    public static string[] DnsNames(X509Certificate2 leaf)
    {
        var names = new List<string>();
        foreach (var extension in leaf.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                names.AddRange(san.EnumerateDnsNames().Select(n => n.Trim().TrimEnd('.').ToLowerInvariant()));
            }
        }

        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Matches the way a client does: the SAN DNS names, including a single leading wildcard
    /// label. Never the common name.
    /// </summary>
    public static bool CoversHostname(X509Certificate2 leaf, string hostname)
    {
        foreach (var extension in leaf.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san)
            {
                continue;
            }

            foreach (var name in san.EnumerateDnsNames())
            {
                if (string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // "*.example.com" covers "a.example.com" and NOT "a.b.example.com" or
                // "example.com" itself -- one label, and only the leftmost.
                if (name.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = name[1..];
                    var label = hostname.Length - suffix.Length;
                    if (label > 0
                        && hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && !hostname.AsSpan(0, label).Contains('.'))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public static X509Certificate2? ParseLeaf(string pemChain)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(pemChain);
        return collection.Count == 0 ? null : collection[0];
    }
}
