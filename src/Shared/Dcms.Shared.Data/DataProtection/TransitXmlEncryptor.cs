using System.Xml.Linq;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.DataProtection;

/// <summary>
/// Encrypts the Data Protection key ring at rest with Vault Transit, so the keys stored in
/// Postgres are ciphertext rather than the plaintext XML the framework writes by default.
///
/// <para>Without this, anyone who can read <c>dataprotection.data_protection_keys</c> — a
/// database dump, a backup, a misconfigured reporting role — holds the master keys for every
/// authentication cookie the platform issues. Wrapping them means that read yields nothing
/// usable without Vault's <c>dcms-dataprotection</c> Transit key as well.</para>
///
/// <para><b>Availability cost, stated because it is real.</b> Enabling this makes minting or
/// reading a key depend on Vault being reachable and unsealed. That is why it is off until
/// Transit auto-unseal is configured: with a Shamir-sealed Vault, a reboot would leave
/// identity unable to read its own key ring, so nobody could log in — the exact failure the
/// unsealing is meant to prevent, arriving through a different door. Turn it on with
/// <c>DataProtection:ProtectWithTransit=true</c> once auto-unseal is in place.</para>
///
/// <para><b>One-way, in practice.</b> Keys written wrapped cannot be read back without Vault.
/// Turning the flag off again leaves the existing wrapped keys unreadable, which logs everyone
/// out. The decryptor stays registered either way so a ring containing both shapes still
/// resolves.</para>
/// </summary>
public sealed class TransitXmlEncryptor(ITransitEncryptor transit, string? transitKeyName = null) : IXmlEncryptor
{
    /// <summary>
    /// The shared key ring's Transit key. Recreating it invalidates every wrapped key ring
    /// entry, which logs everyone out and strands the Forgejo outbox.
    /// </summary>
    public const string KeyName = "dcms-dataprotection";

    /// <summary>
    /// The edge's own key, for the separate ring in <c>edge.data_protection_keys</c>.
    ///
    /// <para>A second key rather than the shared one, for the same reason the edge holds
    /// <c>dcms-tls-keys</c> alone: it is the process an anonymous request from the internet
    /// reaches first, and pointing it at <see cref="KeyName"/> would hand the public ingress
    /// the key that also protects identity's cookies and every user's queued git credential —
    /// which is the thing the edge's separate ring exists to avoid.</para>
    /// </summary>
    public const string EdgeKeyName = "dcms-edge-dataprotection";

    /// <summary>
    /// Recorded in every wrapped element so the decryptor asks Vault for the key that actually
    /// wrapped it, rather than assuming the shared one. Rows written before this existed carry
    /// no name and are read against <see cref="KeyName"/>, which is what wrote them.
    /// </summary>
    internal const string KeyNameElementName = "transitKey";

    private readonly string keyName = transitKeyName ?? KeyName;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // Synchronous by interface. The key ring is read once at startup and written only when
        // a key rolls (every 90 days by default), so this blocks approximately never.
        var ciphertext = transit
            .EncryptAsync(keyName, System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting)))
            .GetAwaiter()
            .GetResult();

        var element = new XElement("encryptedKey",
            new XComment(" This key is encrypted with Vault Transit. "),
            new XElement(KeyNameElementName, keyName),
            new XElement("value", ciphertext));

        return new EncryptedXmlInfo(element, typeof(TransitXmlDecryptor));
    }
}

/// <summary>
/// Counterpart to <see cref="TransitXmlEncryptor"/>. Resolved by name from the
/// <c>decryptorType</c> attribute the framework stores alongside each wrapped key, and
/// activated through DI.
///
/// <para><b>Takes the provider, not <see cref="ITransitEncryptor"/> itself, on purpose.</b>
/// This type is registered unconditionally so a key ring holding both wrapped and plain keys
/// still resolves — but <see cref="ITransitEncryptor"/> is only registered when wrapping is
/// enabled. Depending on it directly made the container unbuildable in every deployment with
/// the flag off: a constructor dependency that cannot be satisfied fails
/// <c>ValidateOnBuild</c> whether or not anything ever calls it.</para>
///
/// <para>Resolving on demand keeps both properties: the container always validates, and a
/// wrapped key encountered without Transit configured produces a precise error instead of a
/// startup failure that names the wrong thing.</para>
/// </summary>
public sealed class TransitXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var transit = services.GetService<ITransitEncryptor>()
            ?? throw new InvalidOperationException(
                "A Data Protection key is encrypted with Vault Transit, but Transit is not configured "
                + "in this process. The key ring was written by a deployment with "
                + "DataProtection:ProtectWithTransit enabled; turning it off does not make those keys "
                + "readable again. Re-enable it (and supply Vault credentials), or delete the wrapped "
                + "rows from dataprotection.data_protection_keys and accept that existing sessions and "
                + "queued Forgejo passwords are lost.");

        var ciphertext = encryptedElement.Element("value")?.Value
            ?? throw new InvalidOperationException(
                "A Data Protection key is marked as Transit-encrypted but carries no <value>. "
                + "The key ring row is corrupt; delete it and let a new key be minted (this logs "
                + "existing sessions out, but they are unreadable either way).");

        // Absent on rows written before the name was recorded, and those were all wrapped with
        // the shared key -- there was no other one.
        var keyName = encryptedElement.Element(TransitXmlEncryptor.KeyNameElementName)?.Value is { Length: > 0 } named
            ? named
            : TransitXmlEncryptor.KeyName;

        var plaintext = transit.DecryptAsync(keyName, ciphertext)
            .GetAwaiter()
            .GetResult();

        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
    }
}
