using System.Text.Json;
using Dcms.AdminApi.Connections;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// External API connections by slug (ADR 0026): where they point and which operations a site may
/// read. Never the key — each environment holds its own (a development environment usually talks
/// to a sandbox account) — so a connection arriving with a new key requirement waits for one, and
/// the snapshots are refetched by the scheduler rather than copied.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Tenant(tenantId), the tenant each
// query names; IgnoreQueryFilters only drops the request's own, which is another environment.
public sealed class ApiConnectionsConfigKind(CmsDbContext db, IConfiguration config) : IEnvironmentConfigKind
{
    public string Kind => "api-connections";

    public string? Permission => PlatformPermissions.PluginsManage;

    public int Order => 30;

    private sealed record Value(string Name, string BaseUrl, string AuthKind, string? AuthName, string[] Operations, int RefreshMinutes);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct) =>
        (await db.ApiConnections.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct))
        .Select(c => new EnvironmentConfigItem(c.Slug, JsonSerializer.SerializeToElement(
            new Value(c.Name, c.BaseUrl, c.AuthKind, c.AuthName, c.Operations.ToArray(), c.RefreshMinutes))) { Label = c.Name })
        .ToList();

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        var row = await db.ApiConnections.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Slug == key, ct);
        if (value is null)
        {
            if (row is not null) db.ApiConnections.Remove(row);
        }
        else
        {
            var wanted = value.Value.Deserialize<Value>()!;
            // The rules the connection screen holds a person to, held here too.
            if (ApiConnectionRules.Problem(key, wanted.Name, wanted.BaseUrl, wanted.AuthKind, wanted.AuthName,
                    wanted.Operations, wanted.RefreshMinutes, config.GetValue("Connections:AllowLocal", false)) is { } problem)
            {
                throw new EnvironmentConfigException(problem);
            }
            if (row is null)
            {
                row = new ApiConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, Slug = key };
                db.ApiConnections.Add(row);
            }
            // The key this environment had stays only for the same origin and the same way of
            // sending it: a deployment re-pointing the connection must never carry the key along.
            if (wanted.AuthKind == "none" || row.AuthKind != wanted.AuthKind || row.AuthName != wanted.AuthName
                || Origin(row.BaseUrl) != Origin(wanted.BaseUrl))
            {
                row.SecretCiphertext = null;
            }
            (row.Name, row.BaseUrl, row.AuthKind, row.AuthName, row.Operations, row.RefreshMinutes) =
                (wanted.Name, wanted.BaseUrl, wanted.AuthKind, wanted.AuthName, wanted.Operations.ToList(), wanted.RefreshMinutes);
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.RefreshedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? Origin(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null;
}
