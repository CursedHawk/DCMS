using System.Xml.Linq;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Data;

/// <summary>
/// The Data Protection key ring is wrapped with Vault Transit, and there is now more than one
/// key doing the wrapping: the platform ring uses <c>dcms-dataprotection</c> and the edge's own
/// ring uses <c>dcms-edge-dataprotection</c>, so that a read of the public ingress's table does
/// not yield the key protecting identity's cookies.
///
/// <para>Two keys means the decryptor can no longer assume which one wrapped a row. It reads
/// the name the encryptor recorded — and for rows written before that name existed, it has to
/// fall back to the key that wrote them, or turning this on retroactively makes every existing
/// session unreadable.</para>
/// </summary>
public class TransitKeyRingWrappingTests
{
    [Fact]
    public void The_key_that_wrapped_a_row_is_the_key_asked_to_unwrap_it()
    {
        var transit = new RecordingTransit();
        var encrypted = new TransitXmlEncryptor(transit, TransitXmlEncryptor.EdgeKeyName)
            .Encrypt(new XElement("key", "secret"));

        transit.EncryptedWith.Should().Be(TransitXmlEncryptor.EdgeKeyName);

        var plaintext = Decryptor(transit).Decrypt(encrypted.EncryptedElement);

        transit.DecryptedWith.Should().Be(TransitXmlEncryptor.EdgeKeyName);
        plaintext.Value.Should().Be("secret");
    }

    [Fact]
    public void A_row_written_before_the_key_name_was_recorded_is_read_against_the_shared_key()
    {
        var transit = new RecordingTransit();
        // The shape the previous release wrote: no name, because there was only one key.
        var legacy = new XElement("encryptedKey", new XElement("value", "ciphertext"));

        Decryptor(transit).Decrypt(legacy);

        transit.DecryptedWith.Should().Be(TransitXmlEncryptor.KeyName);
    }

    [Fact]
    public void The_edge_does_not_share_the_platform_key()
    {
        TransitXmlEncryptor.EdgeKeyName.Should().NotBe(TransitXmlEncryptor.KeyName);
    }

    private static TransitXmlDecryptor Decryptor(ITransitEncryptor transit)
        => new(new ServiceCollection().AddSingleton(transit).BuildServiceProvider());

    private sealed class RecordingTransit : ITransitEncryptor
    {
        public string? EncryptedWith { get; private set; }
        public string? DecryptedWith { get; private set; }

        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
        {
            EncryptedWith = keyName;
            return Task.FromResult(Convert.ToBase64String(plaintext.Span));
        }

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
        {
            DecryptedWith = keyName;
            // The legacy row carries a payload this fake never wrote, so answer with something
            // that parses either way.
            return Task.FromResult(IsBase64(ciphertext)
                ? Convert.FromBase64String(ciphertext)
                : "<key>legacy</key>"u8.ToArray());
        }

        private static bool IsBase64(string value) => Convert.TryFromBase64String(value, new byte[value.Length], out _);
    }
}
