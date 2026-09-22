namespace Dcms.IntegrationTests.Security;

/// <summary>
/// content-api is the internet-facing service, and its cross-origin surface is meant to be
/// three anonymous policies and nothing else: the analytics beacon, form submit, and the
/// branding read, each named at the endpoint that wants it.
///
/// <para>It used to also carry a default policy with <c>AllowCredentials()</c> and an origin
/// list, left over from when the admin console reached the chat hub cross-origin. Nothing needs
/// it now — every browser that talks to content-api does so same-origin, through Vite's proxy
/// in dev, the edge in production, or site-host for tenant sites — and a credentialed default
/// policy is precisely what turns a future cookie here into a cross-origin capability, silently,
/// for whoever adds it.</para>
///
/// <para>The compiler has no opinion about any of this, so the guard reads the source. A
/// default policy is what must not come back; the any-origin policies are deliberate and each
/// is anonymous.</para>
/// </summary>
public sealed class ContentApiCorsSurfaceTests
{
    private static string Program()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "Services", "Dcms.ContentApi", "Program.cs"));
    }

    [Fact]
    public void Content_api_declares_no_default_cors_policy()
        => Program().Should().NotContain(
            "AddDefaultPolicy",
            "a default policy applies to every endpoint that does not name one, including any "
            + "added later; content-api's cross-origin callers are anonymous and name their own");

    [Fact]
    public void No_cors_policy_on_content_api_allows_credentials()
        => Program().Should().NotContain(
            "AllowCredentials",
            "content-api is reached same-origin by every browser that has a credential for it, "
            + "so a credentialed cross-origin policy grants only what an attacker's page wants");
}
