extern alias AdminApiApp;
extern alias IdentityApp;
extern alias PlatformApiApp;
using Dcms.Shared.Audit.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// The guard that keeps audit coverage from decaying.
///
/// <para>Every state-changing endpoint must either declare what it does
/// (<c>.WithAudit(...)</c>) or declare that it deliberately does not need to
/// (<c>.AuditExempt("reason")</c>). Adding a mutating endpoint and thinking about neither
/// fails the build — which is the point: coverage that depends on everyone remembering is
/// coverage that quietly rots.</para>
///
/// <para>Deliberately container-free. It needs the endpoint table, not a working database, so
/// it runs on every machine rather than only where Docker happens to be up. The migrator is
/// switched off and the background services find nothing to talk to, which they already
/// tolerate — none of that touches routing.</para>
/// </summary>
public abstract class AuditCoverageTestsBase<TEntryPoint> : IDisposable
    where TEntryPoint : class
{
    private readonly WebApplicationFactory<TEntryPoint> _factory =
        new WebApplicationFactory<TEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:Migrate", "false");
            builder.UseSetting("Tenancy:ApplyRls", "false");
            builder.UseSetting("Identity:Migrate", "false");
            builder.UseSetting("Identity:Seed", "false");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Every_mutating_endpoint_declares_its_audit_action()
    {
        var undeclared = MutatingEndpoints()
            .Where(e => e.Metadata.GetMetadata<AuditMetadata>() is null
                     && e.Metadata.GetMetadata<AuditExemptMetadata>() is null)
            .Select(Describe)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        undeclared.Should().BeEmpty(
            "every state-changing endpoint needs .WithAudit(AuditActions.X) — or .AuditExempt(\"reason\") "
            + "if a record would genuinely be noise. Undeclared:\n{0}",
            string.Join("\n", undeclared));
    }

    [Fact]
    public void Declared_actions_are_not_accidentally_shared()
    {
        // Two endpoints claiming one action makes the log ambiguous exactly when someone is
        // trying to work out which of them ran. Reusing a route across HTTP methods is fine;
        // the same action key on two different routes is not.
        var duplicates = MutatingEndpoints()
            .Select(e => (Action: e.Metadata.GetMetadata<AuditMetadata>()?.Action, Route: Describe(e)))
            .Where(x => x.Action is not null)
            .GroupBy(x => x.Action!, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Route).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Route))}")
            .ToList();

        duplicates.Should().BeEmpty(
            "each audit action should identify one operation. Shared:\n{0}", string.Join("\n", duplicates));
    }

    private IEnumerable<RouteEndpoint> MutatingEndpoints()
    {
        var source = _factory.Services.GetRequiredService<EndpointDataSource>();

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                // No declared methods means "any", which includes the mutating ones.
                return methods is null || methods.Any(IsMutating);
            });
    }

    private static bool IsMutating(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var verb = methods is null ? "ANY" : string.Join("/", methods.Where(IsMutating));
        return $"{verb} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
    }
}

/// <summary>The control plane: almost every action a tenant administrator can take.</summary>
public sealed class AdminApiAuditCoverageTests : AuditCoverageTestsBase<AdminApiApp::Program>;

/// <summary>
/// The delivery plane. Most of it is anonymous reads, which are out of scope by design — but
/// visitors do write here (form submissions, sign-ups, chat) and those are tenant data.
/// </summary>
public sealed class ContentApiAuditCoverageTests : AuditCoverageTestsBase<Program>;

/// <summary>Account lifecycle: the records that belong to a person rather than a tenant.</summary>
public sealed class IdentityAuditCoverageTests : AuditCoverageTestsBase<IdentityApp::Program>;

/// <summary>
/// The platform console's API — added because it was the obvious omission the moment it
/// existed. These are the endpoints that suspend a tenant, hand out platform permissions and
/// delete from a telemetry store: the smallest surface on the platform and the one where an
/// unrecorded action matters most.
/// </summary>
public sealed class PlatformApiAuditCoverageTests : AuditCoverageTestsBase<PlatformApiApp::Program>;
