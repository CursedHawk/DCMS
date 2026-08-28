using System.Security.Cryptography.X509Certificates;

namespace Dcms.Identity;

/// <summary>
/// Loads the OpenIddict signing and encryption certificates from configuration (Vault, in a
/// deployed environment) instead of minting them per process.
///
/// <para><b>Why this exists.</b> <c>AddDevelopmentSigningCertificate()</c> mints a self-signed
/// certificate into the current user's X.509 store, which inside a container is a directory in
/// the image with no volume behind it. Two consequences, both of which look like other bugs:</para>
///
/// <list type="bullet">
///   <item>Every replica publishes a <b>different JWKS</b>, so an access token minted by one is
///   rejected by every resource server that cached another's. Identity cannot be scaled past one
///   replica at all.</item>
///   <item>Recreating the container mints new keys, so <b>every live token is invalidated by a
///   deploy</b> — including refresh tokens, which is a forced re-login for every user on every
///   release.</item>
/// </list>
///
/// <para>Generate a pair (2048-bit RSA, five years) with:</para>
/// <code>
/// openssl req -x509 -newkey rsa:2048 -sha256 -days 1825 -nodes \
///   -keyout signing.key -out signing.crt -subj "/CN=DCMS Identity Signing"
/// openssl pkcs12 -export -out signing.pfx -inkey signing.key -in signing.crt -passout pass:
/// base64 -w0 signing.pfx
/// </code>
/// <para>Store the base64 under <c>secret/dcms/identity</c> as <c>Identity__SigningCertificate</c>
/// and <c>Identity__EncryptionCertificate</c>. Rotating the signing certificate invalidates every
/// token signed with the old one, so rotate during a maintenance window — or add the new one
/// alongside the old, which OpenIddict supports, before removing the old.</para>
/// </summary>
internal static class OpenIddictCertificates
{
    /// <summary>
    /// Returns the certificate configured under <c>Identity:{key}</c> as base64 PKCS#12, or null
    /// when the key is absent.
    /// </summary>
    public static X509Certificate2? Load(IConfiguration configuration, string key)
    {
        var base64 = configuration[$"Identity:{key}"];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Identity:{key} is set but is not valid base64. It must be a base64-encoded PKCS#12 (.pfx) blob.", ex);
        }

        var password = configuration["Identity:CertificatePassword"];

        try
        {
            // EphemeralKeySet: the private key stays in memory rather than being written into a
            // key store under the container's home directory. Nothing here needs it to persist --
            // the durable copy is in Vault -- and a container filesystem is the wrong place for it.
            return X509CertificateLoader.LoadPkcs12(
                bytes, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Identity:{key} could not be loaded as a PKCS#12 certificate. If the .pfx has an "
                + "export password, set Identity:CertificatePassword.", ex);
        }
    }
}
