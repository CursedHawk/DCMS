using System.Security.Cryptography;

namespace Dcms.Plugins.Functions.Packages;

/// <summary>Subresource-integrity strings, as npm's lockfile carries them (<c>sha512-&lt;base64&gt;</c>).</summary>
public static class Integrity
{
    /// <summary>Whether it names a hash worth trusting: sha256 or stronger. npm's old sha1 is not.</summary>
    public static bool IsStrong(string integrity) => Parse(integrity) is not null;

    /// <summary>Whether the bytes are what any of the integrity's hashes says.</summary>
    public static bool Matches(string integrity, ReadOnlySpan<byte> bytes)
    {
        foreach (var part in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(part) is not ({ } algorithm, { } expected)) continue;
            var actual = algorithm switch
            {
                "sha512" => SHA512.HashData(bytes),
                "sha384" => SHA384.HashData(bytes),
                _ => SHA256.HashData(bytes),
            };
            if (CryptographicOperations.FixedTimeEquals(actual, expected)) return true;
        }
        return false;
    }

    /// <summary>A storage key for the content: the same bytes are the same key, whoever asked.</summary>
    public static string Key(string integrity)
    {
        var (algorithm, hash) = integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).First(p => p is not null)!.Value;
        return $"{algorithm}/{Convert.ToHexStringLower(hash)}";
    }

    private static (string Algorithm, byte[] Hash)? Parse(string integrity)
    {
        foreach (var part in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash <= 0) continue;
            var algorithm = part[..dash];
            if (algorithm is not ("sha512" or "sha384" or "sha256")) continue;
            try
            {
                return (algorithm, Convert.FromBase64String(part[(dash + 1)..]));
            }
            catch (FormatException)
            {
            }
        }
        return null;
    }
}
