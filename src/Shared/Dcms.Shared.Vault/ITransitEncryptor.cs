namespace Dcms.Shared.Vault;

/// <summary>
/// Envelope encryption via Vault Transit. Used for tenant AI API keys and
/// per-tenant visitor-JWT key derivation. Implemented in Phase 10.
/// </summary>
public interface ITransitEncryptor
{
    Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);
    Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default);
}
