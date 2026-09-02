using System.Text;
using Dcms.Shared.Vault;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// Stands in for Vault Transit so the connect flow can be tested without a Vault.
///
/// <para>It is deliberately reversible and deliberately obvious. Tests assert that a plaintext
/// token never appears in an API response, and a fake that returned the plaintext unchanged
/// would make those assertions pass for the wrong reason — so the "ciphertext" is prefixed and
/// encoded, and a leak of it would be visible as such.</para>
/// </summary>
public sealed class FakeTransitEncryptor : ITransitEncryptor
{
    public const string Prefix = "vault:fake:";

    public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        Task.FromResult(Prefix + keyName + ":" + Convert.ToBase64String(plaintext.Span));

    public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
    {
        var expected = Prefix + keyName + ":";
        if (!ciphertext.StartsWith(expected, StringComparison.Ordinal))
        {
            // Mirrors Transit's real behaviour: a key cannot decrypt another key's ciphertext.
            // Without this the tests could not catch social tokens being written under the AI key.
            throw new InvalidOperationException(
                $"Ciphertext was not encrypted with key '{keyName}'.");
        }
        return Task.FromResult(Convert.FromBase64String(ciphertext[expected.Length..]));
    }

    /// <summary>Test-side helper: what plaintext is hiding inside this ciphertext?</summary>
    public static string Reveal(string ciphertext) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[(ciphertext.IndexOf(':', Prefix.Length) + 1)..]));
}
