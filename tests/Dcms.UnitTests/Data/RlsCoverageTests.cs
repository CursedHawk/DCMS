using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Dcms.UnitTests.Data;

/// <summary>
/// Guards the hand-maintained RLS table list against the schema it is supposed to cover.
///
/// <para>This is the check that would have caught <c>ai.user_ai_settings</c>: added a release
/// after its sibling, never added to <c>TenantTables</c>, and therefore the one table in the
/// granted schemas holding encrypted provider keys with a <c>SELECT</c> grant and no policy
/// behind it. Nothing failed — that is the whole problem with a list you have to remember.</para>
///
/// <para>Runs here rather than only at startup because it needs no database: a DbContext's
/// model is built from the mappings, so every entity's schema and table name is known without
/// a connection. Startup keeps its own copy of the assertion for deployments that add tables
/// out of band.</para>
/// </summary>
public class RlsCoverageTests
{
    /// <summary>Never connected to; EF needs a syntactically valid string to build the model.</summary>
    private const string Unused = "Host=localhost;Database=unused;Username=unused;Password=unused";

    private static readonly ITenantContext Tenant = Substitute.For<ITenantContext>();
    private static readonly ISandboxContext Sandbox = Substitute.For<ISandboxContext>();

    private static DbContextOptions<T> Options<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseNpgsql(Unused).Options;

    private static DbContext[] AllContexts() =>
    [
        new TenancyDbContext(Options<TenancyDbContext>(), Tenant),
        new CmsDbContext(Options<CmsDbContext>(), Tenant),
        new MediaDbContext(Options<MediaDbContext>(), Tenant),
        new SitesDbContext(Options<SitesDbContext>(), Tenant),
        new AiDbContext(Options<AiDbContext>()),
        new SearchDbContext(Options<SearchDbContext>(), Tenant),
        new AnalyticsDbContext(Options<AnalyticsDbContext>()),
        new VisitorsDbContext(Options<VisitorsDbContext>(), Tenant, Sandbox),
        new ChatDbContext(Options<ChatDbContext>(), Tenant, Sandbox),
        new FormsDbContext(Options<FormsDbContext>(), Tenant, Sandbox),
        new SocialDbContext(Options<SocialDbContext>(), Tenant),
        new AuditDbContext(Options<AuditDbContext>()),
    ];

    [Fact]
    public void Every_tenant_scoped_table_is_covered_or_deliberately_exempt()
    {
        var contexts = AllContexts();
        try
        {
            // Throws naming the offenders; the assertion message is the documentation.
            var act = () => RlsConfigurator.AssertCoverage(contexts, NullLogger.Instance);
            act.Should().NotThrow();
        }
        finally
        {
            foreach (var context in contexts) context.Dispose();
        }
    }

    [Fact]
    public void The_table_holding_meta_oauth_tokens_is_covered()
    {
        // Same shape as the user_ai_settings miss below, and the same silence: the tokens stay
        // encrypted whether or not a policy exists, so nothing looks wrong from the outside.
        // meta_connections holds a tenant's live Facebook/Instagram credentials.
        using var social = new SocialDbContext(Options<SocialDbContext>(), Tenant);

        var entity = social.Model.GetEntityTypes()
            .Single(e => e.ClrType == typeof(MetaConnection));

        var schema = entity.GetSchema() ?? social.Model.GetDefaultSchema();
        (schema, entity.GetTableName()).Should().Be(("social", "meta_connections"));

        var act = () => RlsConfigurator.AssertCoverage([social], NullLogger.Instance);
        act.Should().NotThrow();
    }

    [Fact]
    public void The_table_holding_user_provider_keys_is_covered()
    {
        // Named on its own because it is the one that was missing, and a regression here is
        // silent: the key column stays encrypted either way, so nothing looks wrong.
        using var ai = new AiDbContext(Options<AiDbContext>());

        var entity = ai.Model.GetEntityTypes()
            .Single(e => e.ClrType == typeof(UserAiSettings));

        var schema = entity.GetSchema() ?? ai.Model.GetDefaultSchema();
        var table = entity.GetTableName();

        (schema, table).Should().Be(("ai", "user_ai_settings"),
            "the coverage assertion matches on the mapped name, so a rename must move the entry too");

        var act = () => RlsConfigurator.AssertCoverage([ai], NullLogger.Instance);
        act.Should().NotThrow();
    }
}
