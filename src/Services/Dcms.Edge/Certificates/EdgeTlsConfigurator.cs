using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Opens the TLS listener and answers each handshake with the right certificate for the name the
/// client asked for.
///
/// <para>This is the piece that replaces Caddy's automatic HTTPS. Kestrel's
/// <see cref="TlsHandshakeCallbackOptions"/> hands us the SNI name before the handshake
/// completes, which is what makes one listener able to serve every tenant domain from a store
/// rather than from a fixed list of bindings.</para>
///
/// <para>Registered as an <see cref="IConfigureOptions{T}"/> rather than configured inline on
/// the host builder, because the callback needs services from the container and the container
/// does not exist yet when <c>ConfigureKestrel</c> runs.</para>
/// </summary>
public sealed class EdgeTlsConfigurator(
    IServiceProvider services,
    IOptions<CertificateOptions> options,
    ILogger<EdgeTlsConfigurator> logger) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions kestrel)
    {
        var config = options.Value;
        if (!config.TlsEnabled)
        {
            logger.LogInformation(
                "Edge TLS listener is off (Edge:Certificates:TlsEnabled). Serving plain HTTP only.");
            return;
        }

        kestrel.ListenAnyIP(config.HttpsPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = SelectCertificateAsync,
                // Cached by Kestrel per SNI name for this long, on top of our own store cache.
                // Both matter: this one avoids re-entering the callback at all.
                HandshakeTimeout = TimeSpan.FromSeconds(config.OnDemandTimeoutSeconds + 10),
            });
        });

        logger.LogInformation("Edge TLS listener on :{Port}.", config.HttpsPort);
    }

    private async ValueTask<SslServerAuthenticationOptions> SelectCertificateAsync(TlsHandshakeCallbackContext context)
    {
        var serverName = context.ClientHelloInfo.ServerName;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            // No SNI means an IP-address connection or a very old client. There is no hostname to
            // resolve a certificate from, so there is nothing honest to serve.
            throw new AuthenticationException("The TLS client sent no server name (SNI).");
        }

        var hostname = CertificateStore.Normalize(serverName);
        var config = options.Value;

        // A scope per handshake: the store and provisioner are singletons but reach EF contexts,
        // which are scoped.
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICertificateStore>();

        var credential = await store.GetAsync(hostname, context.CancellationToken);
        if (credential is null && config.AllowOnDemand)
        {
            // Bounded, because this is blocking a browser's handshake. Past the deadline the
            // connection is refused: an error page a visitor can read beats a tab that spins
            // until the browser's own timeout, and the issuance carries on in the background for
            // whoever arrives next.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.OnDemandTimeoutSeconds));

            var provisioner = scope.ServiceProvider.GetRequiredService<CertificateProvisioner>();
            credential = await provisioner.EnsureAsync(hostname, timeout.Token);
        }

        if (credential is null)
        {
            // Aborting is the correct answer, not a fallback certificate. A self-signed stand-in
            // would put a browser warning in front of a visitor and teach them to click through
            // it, on the tenant's own domain.
            throw new AuthenticationException(
                $"No certificate is available for '{hostname}'.");
        }

        return new SslServerAuthenticationOptions { ServerCertificateContext = credential };
    }
}
