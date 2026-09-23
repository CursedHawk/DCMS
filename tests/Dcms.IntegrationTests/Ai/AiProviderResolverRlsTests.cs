extern alias AiGatewayApp;
using Dcms.IntegrationTests.Social;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using AiProviderResolver = AiGatewayApp::Dcms.AiGateway.Providers.AiProviderResolver;

namespace Dcms.IntegrationTests.Ai;

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
        foreach (var table in new[] { "tenant_ai_settings", "user_ai_settings" })
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
            owner.Settings.Add(await SettingsAsync(tenant, "tenant-model", "sk-tenant"));
            owner.Settings.Add(await SettingsAsync(other, "other-model", "sk-other"));
            await owner.SaveChangesAsync(ct);
        }

        await using var app = AppRoleContext();
        var resolver = new AiProviderResolver(app, _transit, PlatformDefaults(), new NullRecorder());

        var resolved = await resolver.ResolveCredentialsAsync(tenant, userId: null, ct);

        resolved.ApiKey.Should().Be("sk-tenant", "the tenant's own key, not the platform's");
        resolved.Model.Should().Be("tenant-model");
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
            .AddInterceptors(new TenantGucInterceptor(new NoTenant(), NullLogger<TenantGucInterceptor>.Instance))
            .Options);
    }

    private async Task<TenantAiSettings> SettingsAsync(Guid tenantId, string model, string key) => new()
    {
        TenantId = tenantId,
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

    /// <summary>What ai-gateway has: audit's tenantless context, since no request carries a tenant.</summary>
    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }

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
