namespace Dcms.Edge.Certificates;

public static class AcmeChallengeEndpoints
{
    /// <summary>
    /// Serves the HTTP-01 challenge the certificate authority comes back to fetch.
    ///
    /// <para>Must be mapped <b>before</b> the reverse proxy, and matched before any host-based
    /// route: the CA requests <c>http://&lt;domain&gt;/.well-known/acme-challenge/&lt;token&gt;</c>
    /// on the domain being validated, which is by definition a hostname the proxy would otherwise
    /// hand to site-host — and site-host, correctly, has never heard of it.</para>
    ///
    /// <para>Anonymous and unauthenticated, which is the protocol. The token is a high-entropy
    /// value the CA chose and only we were told; knowing one proves nothing and grants nothing.
    /// Exempt from the audit log for the same reason health probes are — it is a machine
    /// fetching a file it was told to fetch.</para>
    /// </summary>
    public static IEndpointRouteBuilder MapAcmeChallenge(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/acme-challenge/{token}", async (
            string token, AcmeChallengeStore store, CancellationToken ct) =>
        {
            var keyAuthorization = await store.GetAsync(token, ct);
            return keyAuthorization is null
                ? Results.NotFound()
                // text/plain, exactly as RFC 8555 specifies. A JSON body or a redirect fails
                // validation with an error that describes none of that.
                : Results.Text(keyAuthorization, "text/plain");
        });

        return app;
    }
}
