extern alias AiGatewayApp;
using Dcms.IntegrationTests.Social;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using AiProviderResolver = AiGatewayApp::Dcms.AiGateway.Providers.AiProviderResolver;

namespace Dcms.IntegrationTests.Ai;

/// <summary>
/// The single provider a workspace had becomes its default provider, key and all; a workspace that
/// configured nothing gets none, and keeps the platform default.
/// </summary>
public sealed class AiProvidersMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [DockerFact]
    public async Task The_configured_provider_moves_into_the_providers_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiDbContext(
            new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261113150253_AddMessageKind", cancellationToken: ct);

        var configured = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai.tenant_ai_settings ("TenantId", "Provider", "Model", "ApiKeyCiphertext", "BaseUrl", "AgentMaxTurns", "UpdatedAt")
            VALUES ({configured}, 'Ollama', ' llama3 ', 'vault:v1:abc', 'http://10.0.0.5:11434/v1', 40, now()),
                   ({untouched}, 'Inherit', NULL, NULL, '', NULL, now())
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);

        var providers = await db.Providers.AsNoTracking().ToListAsync(ct);
        providers.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            TenantId = configured, Name = "Ollama", Provider = AiProvider.Ollama, Model = "llama3",
            ApiKeyCiphertext = "vault:v1:abc", BaseUrl = "http://10.0.0.5:11434/v1", IsDefault = true,
        });
        (await db.Settings.AsNoTracking().SingleAsync(s => s.TenantId == configured, ct)).AgentMaxTurns
            .Should().Be(40, "the agent policy stays where it was");
    }
}

/// <summary>
/// ADR 0015 phase 4: ai-gateway on <c>dcms_app</c>. The gateway is called service to service
/// with the tenant in the request body, so nothing ambient tells the database whose settings
/// are being read. The failure this guards is quiet: the rows vanish and resolution falls
/// through to the platform's provider and key, so the call still works, on the wrong bill.
///
/// <para>The resolver runs as the gateway runs it after the move: <c>dcms_app</c>,
/// <c>NOBYPASSRLS</c>, the GUC interceptor attached, with no tenant but what the resolver
/// declares.</para>
/// </summary>
public sealed class AiProviderResolverRlsTests : IAsyncLifetime
{
    private const string AppRolePassword = "dcms-app-test";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly FakeTransitEncryptor _transit = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var owner = new AiDbContext(
            new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await owner.Database.MigrateAsync();
        foreach (var table in new[] { "tenant_ai_settings", "user_ai_settings", "providers", "usage" })
        {
            await RlsConfigurator.ProtectAsync(owner, "ai", table, NullLogger.Instance);
        }
        await owner.Database.ExecuteSqlRawAsync($"""
            CREATE ROLE dcms_app LOGIN PASSWORD '{AppRolePassword}' NOSUPERUSER NOBYPASSRLS;
            GRANT USAGE ON SCHEMA ai TO dcms_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ai TO dcms_app;
            """);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [DockerFact]
    public async Task A_tenants_own_key_and_model_resolve_under_the_app_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();

        await using (var owner = new AiDbContext(
            new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            owner.Providers.Add(await ProviderAsync(tenant, "tenant-model", "sk-tenant"));
            owner.Providers.Add(await ProviderAsync(other, "other-model", "sk-other"));
            await owner.SaveChangesAsync(ct);
        }

        await using var app = AppRoleContext();
        var resolver = new AiProviderResolver(app, _transit, PlatformDefaults(), new NullRecorder());

        var resolved = await resolver.ResolveCredentialsAsync(tenant, userId: null, providerId: null, ct);

        resolved.ApiKey.Should().Be("sk-tenant", "the tenant's own key, not the platform's");
        resolved.Model.Should().Be("tenant-model");
        resolved.UsageSource.Should().Be(AiUsageSources.Workspace);
    }

    [DockerFact]
    public async Task A_picked_provider_is_used_whole_and_only_within_its_own_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var user = Guid.NewGuid();
        var main = await ProviderAsync(tenant, "main-model", "sk-main", isDefault: true);
        var second = await ProviderAsync(tenant, "second-model", "sk-second", isDefault: false, name: "Second");
        var foreign = await ProviderAsync(other, "other-model", "sk-other");
        await using (var owner = OwnerContext())
        {
            owner.Providers.AddRange(main, second, foreign);
            owner.UserSettings.Add(new UserAiSettings
            {
                TenantId = tenant,
                UserId = user,
                ApiKeyCiphertext = await _transit.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.TenantSecretsKey, System.Text.Encoding.UTF8.GetBytes("sk-mine")),
            });
            await owner.SaveChangesAsync(ct);
        }

        await using var app = AppRoleContext();
        var resolver = new AiProviderResolver(app, _transit, PlatformDefaults(), new NullRecorder());

        var unpicked = await resolver.ResolveCredentialsAsync(tenant, user, providerId: null, ct);
        unpicked.ApiKey.Should().Be("sk-mine", "with nothing picked, the member's own key still wins");
        unpicked.Model.Should().Be("main-model", "on the default provider");
        (unpicked.UsageSource, unpicked.PayingUserId).Should().Be((AiUsageSources.User, user));

        var picked = await resolver.ResolveCredentialsAsync(tenant, user, second.Id, ct);
        (picked.ApiKey, picked.Model).Should().Be(("sk-second", "second-model"), "a picked provider is used whole");
        (picked.UsageSource, picked.ProviderId, picked.ProviderName).Should().Be((AiUsageSources.Workspace, second.Id, "Second"));

        var stolen = () => resolver.ResolveCredentialsAsync(tenant, user, foreign.Id, ct);
        await stolen.Should().ThrowAsync<AiGatewayApp::Dcms.AiGateway.Providers.AiProviderNotFoundException>(
            "another workspace's provider, key and all, is not one this tenant can pick");
    }

    [DockerFact]
    public async Task A_local_provider_without_a_key_is_sent_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        await using (var owner = OwnerContext())
        {
            owner.Providers.Add(new AiProviderConfig
            {
                TenantId = tenant, Name = "Ollama", Provider = AiProvider.Ollama, BaseUrl = "http://10.0.0.5:11434/v1", IsDefault = true,
            });
            await owner.SaveChangesAsync(ct);
        }

        await using var app = AppRoleContext();
        var resolved = await new AiProviderResolver(app, _transit, PlatformDefaults(), new NullRecorder())
            .ResolveCredentialsAsync(tenant, userId: null, providerId: null, ct);

        resolved.ApiKey.Should().BeEmpty("the platform's key must never go to a URL the workspace chose");
        resolved.UsageSource.Should().Be(AiUsageSources.Workspace);
    }

    [DockerFact]
    public async Task Usage_adds_up_per_day_payer_provider_and_model_under_the_app_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var user = Guid.NewGuid();
        var workspace = new AiGatewayApp::Dcms.AiGateway.Providers.ResolvedCredentials(AiProvider.Anthropic, "m", null, "k")
        {
            UsageSource = AiUsageSources.Workspace, ProviderId = provider, ProviderName = "Main",
        };
        var own = workspace with { UsageSource = AiUsageSources.User, ProviderId = null, PayingUserId = user, ProviderName = "Own key" };

        await using (var app = AppRoleContext())
        {
            var ledger = new AiGatewayApp::Dcms.AiGateway.AiUsageLedger(
                app, NullLogger<AiGatewayApp::Dcms.AiGateway.AiUsageLedger>.Instance);
            await ledger.RecordAsync(tenant, workspace, "claude-x", 100, 10, 5, 50);
            await ledger.RecordAsync(tenant, workspace, "claude-x", 200, 20, 0, 70);
            await ledger.RecordAsync(tenant, own, "claude-x", 1, 1, 0, 0);
            await ledger.RecordAsync(tenant, own, "claude-x", 1, 1, 0, 0);
        }

        await using var owner = OwnerContext();
        var rows = await owner.Usage.Where(u => u.TenantId == tenant).OrderBy(u => u.Source).ToListAsync(ct);
        rows.Should().HaveCount(2, "the same day, payer, provider and model is one row, nulls included");
        rows[0].Should().BeEquivalentTo(new
        {
            Source = AiUsageSources.User, UserId = (Guid?)user, ProviderId = (Guid?)null, Calls = 2L, InputTokens = 2L,
        });
        rows[1].Should().BeEquivalentTo(new
        {
            Source = AiUsageSources.Workspace, ProviderId = (Guid?)provider, ProviderName = "Main", Calls = 2L,
            InputTokens = 300L, OutputTokens = 30L, CacheWriteTokens = 5L, CacheReadTokens = 120L,
        });
    }

    [DockerFact]
    public async Task The_chatbot_gets_only_its_own_tenants_server_prompt_overrides()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var owner = new AiDbContext(
            new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            owner.Settings.Add(new TenantAiSettings
            {
                TenantId = tenant,
                PromptOverridesJson = """{"chatbot":"You are {{botName}}, the HR desk.","site.react":"browser only"}""",
            });
            owner.Settings.Add(new TenantAiSettings { TenantId = other, PromptOverridesJson = """{"chatbot":"other"}""" });
            await owner.SaveChangesAsync(ct);
        }

        await using var app = AppRoleContext();
        var overrides = await AiGatewayApp::Dcms.AiGateway.ChatEndpoints.ServerPromptOverridesAsync(app, tenant, ct);

        // Its own row under the app role, and only what the server builds.
        overrides.Should().Equal(new Dictionary<string, string> { ["chatbot"] = "You are {{botName}}, the HR desk." });
    }

    private AiDbContext AppRoleContext()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = "dcms_app",
            Password = AppRolePassword,
        }.ConnectionString;

        return new AiDbContext(new DbContextOptionsBuilder<AiDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(new TenantGucInterceptor(NullLogger<TenantGucInterceptor>.Instance))
            .Options);
    }

    private AiDbContext OwnerContext() => new(
        new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    private async Task<AiProviderConfig> ProviderAsync(
        Guid tenantId, string model, string key, bool isDefault = true, string name = "Main") => new()
    {
        TenantId = tenantId,
        Name = name,
        IsDefault = isDefault,
        Provider = AiProvider.OpenAi,
        Model = model,
        ApiKeyCiphertext = await _transit.EncryptAsync(
            VaultTransitServiceCollectionExtensions.TenantSecretsKey, System.Text.Encoding.UTF8.GetBytes(key)),
    };

    private static IConfiguration PlatformDefaults() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Defaults:Provider"] = "anthropic",
            ["Ai:Defaults:Model"] = "platform-model",
            ["Ai:Defaults:ApiKey"] = "sk-platform",
        })
        .Build();

    private sealed class NullRecorder : IAuditRecorder
    {
        public AuditEntry? Declared => null;
        public IReadOnlyList<AuditEntry> Pending => [];
        public AuditEntry Record(string action) => new() { Action = action };
        public void Record(AuditEntry entry) { }
        public AuditEntry Declare(string action) => new() { Action = action };
        public void Discard(AuditEntry entry) { }
        public ValueTask RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public IReadOnlyList<AuditEvent> Drain() => [];
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
