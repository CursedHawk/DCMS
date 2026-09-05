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

            // THE SCHEME MUST TRAVEL WITH THE HOST, and forgetting it broke sign-in outright.
            //
            // Identity builds the discovery document's endpoint URLs from the incoming request:
            // its scheme and the Host header, promoted through UseForwardedHeaders. Rewriting
            // only the Host meant the internal call arrived as plain HTTP and came back with
            //   "jwks_uri": "http://auth.highgeek.eu/.well-known/jwks"
            // -- the right host on the wrong scheme. Microsoft.IdentityModel's document
            // retriever then refuses its own metadata with IDX20108 ("not valid as per HTTPS
            // scheme"), the OIDC challenge throws, and every gated route answers 500. Not a
            // sign-in page, not a redirect loop: an unhandled exception on the public ingress,
            // and the address it names in the message is the https one that was asked for,
            // which is exactly the wrong place to look.
            //
            // The public request that this stands in for carries X-Forwarded-Proto: https, set
            // by this same edge. Asserting it here makes the internal call answered identically
            // -- which is the whole premise of the rewrite.
            if (_public.Scheme == Uri.UriSchemeHttps && _internal.Scheme != Uri.UriSchemeHttps)
            {
                request.Headers.Remove("X-Forwarded-Proto");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", Uri.UriSchemeHttps);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
