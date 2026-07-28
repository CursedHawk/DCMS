using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Applies the Row-Level Security backstop to the tenant-scoped tables after the
/// EF migrations have created them. Defense-in-depth on top of the EF per-tenant
/// query filters (the primary isolation guarantee). Idempotent — safe to run on
/// every startup. See <c>infra/postgres/init/01-rls.sql</c> and ADR 0003.
///
/// Each table gets a single policy keyed on the <c>app.tenant_id</c> GUC:
/// a row is visible/insertable only when its <c>TenantId</c> equals the GUC. The
/// app connects as the table owner and bypasses RLS, so this changes nothing for
/// the running services; it constrains any non-owner (e.g. the <c>dcms_rls</c>
/// role used by the isolation test) reading the data with a raw connection.
/// </summary>
public static class RlsConfigurator
{
    // (schema, table) pairs whose rows carry a "TenantId" column and are
    // tenant-filtered in EF. Cross-tenant scan tables (content_outbox,
    // scheduled_publishes) are deliberately excluded.
    private static readonly (string Schema, string Table)[] TenantTables =
    [
        ("tenancy", "domains"),
        ("tenancy", "tenant_memberships"),
        ("tenancy", "tenant_roles"),
        ("tenancy", "tenant_role_permissions"),
        ("tenancy", "member_roles"),
        ("tenancy", "invitations"),
        ("plugins", "plugin_instances"),
        ("cms", "content_items"),
        ("cms", "content_versions"),
        ("media", "media_assets"),
        ("media", "media_variants"),
        ("sites", "sites"),
        ("sites", "site_builds"),
        ("sites", "site_drafts"),
        ("ai", "tenant_ai_settings"),
        ("search", "search_documents"),
        ("analytics", "events"),
        ("analytics", "daily_rollups"),
        ("visitors", "visitor_accounts"),
        ("visitors", "visitor_refresh_tokens"),
        ("chat", "conversations"),
        ("chat", "messages"),
        ("forms", "form_submissions"),
    ];

    public static async Task ApplyAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        foreach (var (schema, table) in TenantTables)
        {
            var qualified = $"\"{schema}\".\"{table}\"";
            // The policy is the important part — apply it unconditionally.
            // CREATE POLICY has no IF NOT EXISTS; drop-then-create keeps it idempotent.
            var policySql = $"""
                ALTER TABLE {qualified} ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON {qualified};
                CREATE POLICY tenant_isolation ON {qualified}
                    USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """;
            try
            {
                await context.Database.ExecuteSqlRawAsync(policySql, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RLS policy apply failed for {Table}.", qualified);
                continue;
            }

            // Grants to the least-privilege test/raw-access role are best-effort:
            // the role only exists where infra/postgres/init/01-rls.sql ran.
            var grantSql = $"GRANT USAGE ON SCHEMA \"{schema}\" TO dcms_rls; GRANT SELECT ON {qualified} TO dcms_rls;";
            try
            {
                await context.Database.ExecuteSqlRawAsync(grantSql, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RLS grant to dcms_rls skipped for {Table} (role absent).", qualified);
            }
        }
        logger.LogInformation("Row-Level Security policies applied to {Count} tenant tables.", TenantTables.Length);
    }
}
