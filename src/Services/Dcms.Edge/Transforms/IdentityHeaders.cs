using System.Security.Claims;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Dcms.Edge.Transforms;

/// <summary>
/// Tells the service behind the edge who the caller is, in the header format that service reads.
///
/// <para>This is what removes a login implementation from Grafana and from Forgejo. It is also,
/// unavoidably, a bearer credential in header form: whoever can set <c>X-WEBAUTH-USER</c> on a
/// request that reaches Grafana <i>is</i> that user. Two things make that safe, and both are
/// load-bearing rather than tidy:</para>
///
/// <list type="number">
///   <item><c>UseUntrustedHeaderScrubbing</c> removes every <c>X-WEBAUTH-*</c> header from every
///   inbound request before anything reads one, so a client cannot supply its own. There is a
///   test for exactly that, and it is the test to keep green.</item>
///   <item>Neither service publishes a host port, so the compose network is the only other way
///   to reach them.</item>
/// </list>
/// </summary>
public static class IdentityHeaders
{
    /// <summary>
    /// Route metadata: which service's header dialect to speak. Absent means none — the default
    /// is to assert nothing, so a route added later has to opt in rather than remember to opt
    /// out.
    /// </summary>
    public const string MetadataKey = "dcms.identity-headers";

    /// <summary>Grafana's auth.proxy: the login is an email address.</summary>
    public const string Grafana = "grafana";

    /// <summary>Forgejo's reverse-proxy auth: the login is the Forgejo username.</summary>
    public const string Forgejo = "forgejo";

    /// <summary>
    /// The claim identity emits carrying the user's Forgejo account name.
    ///
    /// <para><b>Not derivable here, and that is the whole reason it is a claim.</b> Forgejo
    /// usernames are allocated by <c>ForgejoUserSync</c> from the email's local part, with a
    /// numeric suffix on collision — two people called <c>rgolias@</c> at different domains
    /// become <c>rgolias</c> and <c>rgolias-2</c>. An edge that recomputed the name from the
    /// email would, on any collision, sign one of them in as the other, in a git server holding
    /// every tenant's site repositories. So the edge asserts a name only when identity has told
    /// it one.</para>
    /// </summary>
    public const string ForgejoUsernameClaim = "forgejo_username";

    /// <summary>Grafana org role. Server-admin is not settable over auth.proxy; the break-glass
    /// local <c>admin</c> account is what holds it.</summary>
    private const string GrafanaRole = "Admin";

    public static void AddIdentityHeaders(this TransformBuilderContext context)
    {
        if (context.Route.Metadata is null
            || !context.Route.Metadata.TryGetValue(MetadataKey, out var dialect))
        {
            return;
        }

        context.AddRequestTransform(transform =>
        {
            foreach (var (name, value) in Resolve(dialect, transform.HttpContext))
            {
                transform.ProxyRequest.Headers.TryAddWithoutValidation(name, value);
            }
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Which headers to assert for this caller, if any. A pure function on purpose: this is the
    /// decision the whole arrangement rests on, and it should be assertable without standing up
    /// a proxy pipeline to ask it.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Resolve(string dialect, HttpContext http)
    {
        // The caller authenticated themselves, so let the service check that instead. This is
        // the second guard on the git-over-HTTP paths: those requests carry the Basic credential
        // ForgejoUserSync provisioned, and overwriting the identity they prove with one from a
        // browser session would hand a push to the wrong account. The route split is the first
        // guard; this one holds even if a git URL shape is missed.
        if (http.Request.Headers.ContainsKey("Authorization"))
        {
            yield break;
        }

        if (http.User.Identity?.IsAuthenticated != true)
        {
            yield break;
        }

        switch (dialect)
        {
            case Grafana:
                var email = http.User.FindFirstValue("email");
                if (string.IsNullOrEmpty(email))
                {
                    // Grafana keys its accounts on this. Asserting a blank one would create an
                    // account nobody owns rather than fail a sign-in.
                    yield break;
                }
                yield return new("X-WEBAUTH-USER", email);
                yield return new("X-WEBAUTH-EMAIL", email);

                var name = http.User.FindFirstValue("name");
                if (!string.IsNullOrEmpty(name))
                {
                    yield return new("X-WEBAUTH-NAME", name);
                }

                // Everyone who gets past the route's policy is a SuperAdmin, so there is one
                // role to send. Sending it explicitly rather than relying on Grafana's
                // auto_assign_org_role, which is Viewer -- and a Viewer on these dashboards
                // reads the whole platform anyway, so the difference is friction, not a safety
                // margin.
                yield return new("X-WEBAUTH-ROLE", GrafanaRole);
                break;

            case Forgejo:
                var login = http.User.FindFirstValue(ForgejoUsernameClaim);
                if (string.IsNullOrEmpty(login))
                {
                    // No mirrored account yet. Forgejo's own sign-in page is the right answer;
                    // auto-registration is off there precisely so that a name this edge guessed
                    // cannot collide with the one the sync will allocate.
                    yield break;
                }
                yield return new("X-WEBAUTH-USER", login);
                break;
        }
    }
}
