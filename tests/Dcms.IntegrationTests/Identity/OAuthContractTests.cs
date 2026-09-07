extern alias IdentityApp;

using IdentityApp::Dcms.Identity;
using System.Reflection;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// The three-place contract every scope has to satisfy, and which is silent when broken.
///
/// <para>A scope must exist as a constant, be seeded as a row by <c>IdentitySeeder</c>, and be
/// passed to <c>options.RegisterScopes(...)</c> in <c>Program.cs</c>. Missing the middle one
/// leaves it out of the database; missing the last leaves it out of discovery. Either way the
/// failure appears at the moment somebody is trying to sign in, and the seeder logs success.
/// <c>dcms.platform</c> shipped in the seeder and not in Program.cs once already.</para>
/// </summary>
public sealed class OAuthContractTests
{
    private static IReadOnlyList<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }

    [Fact]
    public void Every_scope_constant_is_registered_for_discovery()
    {
        var program = ReadSource("src/Services/Dcms.Identity/Program.cs");
        var registerCall = program[program.IndexOf("options.RegisterScopes(", StringComparison.Ordinal)..];
        registerCall = registerCall[..registerCall.IndexOf(");", StringComparison.Ordinal)];

        foreach (var name in typeof(DcmsOAuth.Scopes)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral)
                     .Select(f => f.Name))
        {
            registerCall.Should().Contain(
                $"Scopes.{name}",
                $"'{name}' is a declared scope, so it must be in RegisterScopes or it is absent "
                + "from /.well-known/openid-configuration and sign-in fails for whoever asks for it");
        }
    }

    [Fact]
    public void Every_scope_constant_is_seeded_as_a_row()
    {
        var seeder = ReadSource("src/Services/Dcms.Identity/Seeding/IdentitySeeder.cs");
        foreach (var name in typeof(DcmsOAuth.Scopes)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral)
                     .Select(f => f.Name))
        {
            seeder.Should().Contain(
                $"Scopes.{name}",
                $"'{name}' has no scope row, so a token request naming it is refused");
        }
    }

    [Fact]
    public void Scope_names_are_namespaced_and_unique()
    {
        var scopes = ConstantsOf(typeof(DcmsOAuth.Scopes));
        scopes.Should().OnlyHaveUniqueItems();
        scopes.Should().AllSatisfy(s => s.Should().StartWith("dcms."));
    }

    [Fact]
    public void Client_ids_are_unique()
    {
        // Two constants resolving to one id means one seeder call silently overwrites the
        // other's permissions, and which wins depends on call order.
        ConstantsOf(typeof(DcmsOAuth.Clients)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_console_service_scope_is_not_the_admin_SPA_scope()
    {
        // The whole point of dcms.console: ServicePrincipalGuard confines it to the endpoints
        // that named it, where dcms.admin would be indistinguishable from the SPA's own token.
        DcmsOAuth.Scopes.Console.Should().NotBe(DcmsOAuth.Scopes.Admin);
    }

    [Fact]
    public void Platform_api_has_its_own_service_client()
    {
        // Not a reuse of admin-api's: whoever holds that secret also holds dcms.ai and
        // dcms.social, and infra/vault/policies/dcms-platform-api.hcl argues the opposite
        // direction for this service.
        DcmsOAuth.Clients.PlatformApiService.Should().NotBe(DcmsOAuth.Clients.AdminApiService);
    }

    [Fact]
    public void The_seeder_converges_scope_permissions_on_an_existing_client()
    {
        /*
         * The bug this guards is a silent no-op, and it is the reason the whole API split has a
         * six-commit ordering: EnsurePublicSpaClientAsync used to return as soon as the redirect
         * URIs matched, so editing the scopes of a client that already existed changed nothing
         * at all. Removing a scope from a shipped client — which is how this split ends — would
         * have appeared to work and done nothing.
         *
         * A source scan rather than a behavioural test because the alternative is standing up
         * OpenIddict against a real database for an assertion about one early return.
         */
        var seeder = ReadSource("src/Services/Dcms.Identity/Seeding/IdentitySeeder.cs");
        seeder.Should().Contain("wantedScopes",
            "EnsurePublicSpaClientAsync must reconcile scope permissions, not only URIs");
        seeder.Should().Contain("EnsureServiceClientAsync",
            "confidential clients must converge too, or a service can never lose a scope");
    }
}
