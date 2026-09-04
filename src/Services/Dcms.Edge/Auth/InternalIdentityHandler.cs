namespace Dcms.Edge.Auth;

/// <summary>
/// Sends the OIDC handler's back-channel calls to identity's internal address while leaving
/// every URL the browser sees pointing at the public one.
///
/// <para>Identity's discovery document advertises public endpoints, because the issuer it
/// stamps into tokens is the public origin and the two have to agree. So the authorize URL a
/// browser is redirected to is correct as published — but the calls this process makes for
/// itself (discovery, JWKS, the code-for-token exchange, userinfo) would go out to the public
/// name and come straight back in through this proxy. That works only while the host hairpins
/// NAT on its own published port, and when it stops working, sign-in stops with it while
/// everything else keeps serving.</para>
///
/// <para>Rewriting the authority here rather than overriding endpoints on the options keeps
/// issuer validation untouched: the metadata is still the public document, and the tokens still
/// have to be signed by the issuer they claim.</para>
/// </summary>
public sealed class InternalIdentityHandler : DelegatingHandler
{
    private readonly Uri _public;
    private readonly Uri _internal;

    public InternalIdentityHandler(string publicAuthority, string internalAuthority)
        : base(new HttpClientHandler())
    {
        _public = new Uri(publicAuthority, UriKind.Absolute);
        _internal = new Uri(internalAuthority, UriKind.Absolute);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri
            && string.Equals(uri.Host, _public.Host, StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new UriBuilder(uri)
            {
                Scheme = _internal.Scheme,
                Host = _internal.Host,
                Port = _internal.Port,
            }.Uri;

            // Identity resolves its own issuer from the request when it is behind a proxy, and
            // some of its endpoints vary on the Host. Keeping the public name on the header
            // means the internal call is answered exactly as the public one would have been.
            request.Headers.Host = _public.IsDefaultPort
                ? _public.Host
                : $"{_public.Host}:{_public.Port}";
        }

        return base.SendAsync(request, cancellationToken);
    }
}
