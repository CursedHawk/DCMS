using System.Text;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The platform storage and secrets contracts against real Postgres. The property that matters
/// is that tenant and plugin come from the caller's context and nowhere else: two plugins, or
/// two tenants, writing the same collection and key never see each other's documents.
///
/// <para>Runs with no HTTP request and no ambient tenant, as a job or event handler would, which
/// is also what proves the stores scope themselves rather than leaning on the request's query
/// filter.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class PluginDataTests(AdminApiFixture fixture)
{
    private sealed record Ctx(Guid TenantId, string PluginId, PluginInstanceContext? Instance) : IPluginContext
    {
        public PluginActor Actor => PluginActor.System;
        public IPluginContracts Contracts => throw new NotSupportedException();
    }

    private static Ctx For(Guid tenant, string plugin, Guid? instance = null) =>
        new(tenant, plugin, instance is { } id
            ? new PluginInstanceContext(id, tenant, plugin, "slug", "name", "", JsonDocument.Parse("{}"))
            : null);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<T> WithStorage<T>(Ctx ctx, Func<IPluginStorage, Task<T>> act)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await act(new PluginStorage(ctx, scope.ServiceProvider.GetRequiredService<CmsDbContext>()));
    }

    [DockerFact]
    public async Task Documents_round_trip_with_optimistic_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = For(Guid.NewGuid(), "forms", Guid.NewGuid());

        var first = await WithStorage(ctx, s => s.PutAsync(new PutDocument("drafts", "a", Json(new { n = 1 }), ExpectedVersion: 0), ct));
        first.Version.Should().Be(1);

        var second = await WithStorage(ctx, s => s.PutAsync(new PutDocument("drafts", "a", Json(new { n = 2 }), ExpectedVersion: 1), ct));
        second.Version.Should().Be(2);

        var stale = () => WithStorage(ctx, s => s.PutAsync(new PutDocument("drafts", "a", Json(new { n = 3 }), ExpectedVersion: 1), ct));
        await stale.Should().ThrowAsync<ContractConflictException>().WithMessage("*version 2, not 1*");

        var mustNotExist = () => WithStorage(ctx, s => s.PutAsync(new PutDocument("drafts", "a", Json(new { }), ExpectedVersion: 0), ct));
        await mustNotExist.Should().ThrowAsync<ContractConflictException>();

        var read = await WithStorage(ctx, s => s.GetAsync(new DocumentAddress("drafts", "a"), ct));
        read!.Data.GetProperty("n").GetInt32().Should().Be(2);

        (await WithStorage(ctx, s => s.DeleteAsync(new DocumentAddress("drafts", "a"), ct))).Found.Should().BeTrue();
        (await WithStorage(ctx, s => s.GetAsync(new DocumentAddress("drafts", "a"), ct))).Should().BeNull();
    }

    [DockerFact]
    public async Task Same_key_is_private_per_plugin_per_tenant_and_per_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var instance = Guid.NewGuid();

        var formsA = For(tenantA, "forms", instance);
        var eventsA = For(tenantA, "events", instance);
        var formsB = For(tenantB, "forms", instance);

        await WithStorage(formsA, s => s.PutAsync(new PutDocument("c", "k", Json(new { owner = "formsA" })), ct));
        await WithStorage(formsA, s => s.PutAsync(new PutDocument("c", "k", Json(new { owner = "formsA-wide" }), InstanceScoped: false), ct));

        (await WithStorage(eventsA, s => s.GetAsync(new DocumentAddress("c", "k"), ct))).Should().BeNull("another plugin, same tenant");
        (await WithStorage(formsB, s => s.GetAsync(new DocumentAddress("c", "k"), ct))).Should().BeNull("same plugin, another tenant");

        (await WithStorage(formsA, s => s.GetAsync(new DocumentAddress("c", "k"), ct)))!
            .Data.GetProperty("owner").GetString().Should().Be("formsA");
        (await WithStorage(formsA, s => s.GetAsync(new DocumentAddress("c", "k", InstanceScoped: false), ct)))!
            .Data.GetProperty("owner").GetString().Should().Be("formsA-wide");
    }

    [DockerFact]
    public async Task Query_filters_by_containment_and_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = For(Guid.NewGuid(), "forms", Guid.NewGuid());
        for (var i = 0; i < 5; i++)
        {
            var i1 = i;
            await WithStorage(ctx, s => s.PutAsync(new PutDocument("subs", $"k{i1}", Json(new { status = i1 % 2 == 0 ? "open" : "closed", i = i1 })), ct));
        }

        var open = await WithStorage(ctx, s => s.QueryAsync(
            new QueryDocuments("subs", new Dictionary<string, JsonElement> { ["status"] = Json("open") }, PageSize: 2), ct));

        open.TotalCount.Should().Be(3);
        open.Items.Select(d => d.Key).Should().Equal("k0", "k2");
    }

    [DockerFact]
    public async Task Tenant_wide_context_cannot_address_instance_data_implicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = For(Guid.NewGuid(), "forms");

        var act = () => WithStorage(job, s => s.GetAsync(new DocumentAddress("c", "k"), ct));

        await act.Should().ThrowAsync<ContractValidationException>().WithMessage("*InstanceScoped = false*");
    }

    [DockerFact]
    public async Task Secrets_are_stored_encrypted_and_never_listed_with_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = For(Guid.NewGuid(), "instagram", Guid.NewGuid());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var secrets = new PluginSecrets(ctx, db, new ReversingTransit());

        await secrets.SetAsync(new SetSecret("token", "s3cret"), ct);

        (await secrets.GetAsync(new SecretName("token"), ct)).Value.Should().Be("s3cret");
        (await secrets.ListAsync(new SecretNames(), ct)).Names.Should().Equal("token");

        using var rls = RlsScope.Platform();
        var stored = await db.PluginSecrets.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.TenantId == ctx.TenantId, ct);
        stored.Ciphertext.Should().NotContain("s3cret");

        var other = new PluginSecrets(For(ctx.TenantId, "facebook", ctx.Instance!.InstanceId), db, new ReversingTransit());
        (await other.GetAsync(new SecretName("token"), ct)).Value.Should().BeNull("another plugin's secret");
    }

    /// <summary>Stands in for Vault Transit: reversible, and visibly not the plaintext.</summary>
    private sealed class ReversingTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
        {
            keyName.Should().Be("dcms-plugin-secrets");
            return Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.ToArray().Reverse().ToArray()));
        }

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default) =>
            Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]).Reverse().ToArray());
    }
}
