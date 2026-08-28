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
        ("media", "media_folders"),
        ("sites", "sites"),
        ("sites", "site_builds"),
        ("sites", "site_drafts"),
        ("ai", "tenant_ai_settings"),
        // Holds every user's Vault-Transit-encrypted provider key. Added a release after its
        // sibling above and missed here, which mattered because 01-rls.sql grants SELECT on
        // every *future* table in these schemas by default while the policies are opt-in per
        // table — so the omission was a grant with nothing enforcing the tenant predicate.
        ("ai", "user_ai_settings"),
        ("search", "search_documents"),
        ("analytics", "events"),
        ("analytics", "daily_rollups"),
        ("visitors", "visitor_accounts"),
        ("visitors", "visitor_refresh_tokens"),
        ("chat", "conversations"),
        ("chat", "messages"),
        ("forms", "form_submissions"),
        // Audit rows carry a TenantId and must be covered like any other tenant table.
        // Guid.Empty marks platform-scope records (logins, tenant provisioning): the policy
        // compares equality against the GUC, so those rows are invisible to tenant readers,
        // which is the intended behaviour. The sibling audit tables (chain_heads,
        // chain_anchors, audit_outbox) are deliberately excluded — cross-tenant scan tables,
        // same rationale as content_outbox above.
        ("audit", "audit_events"),
    ];

    /// <summary>
    /// Tables that carry a <c>TenantId</c> and are deliberately left without a policy, because
    /// the code that reads them is a cross-tenant scan and a per-tenant predicate would break
    /// it. Kept as data rather than as a comment so <see cref="AssertCoverage"/> can tell
    /// "decided against" apart from "not noticed".
    /// </summary>
    private static readonly (string Schema, string Table)[] ExemptTables =
    [
        ("cms", "content_outbox"),        // drained by a cross-tenant dispatcher
        ("cms", "scheduled_publishes"),   // scanned across tenants by the publish worker
        ("audit", "audit_outbox"),        // same, and mapped into every business context
        ("audit", "chain_heads"),         // one row per chain; the verifier walks all of them
        ("audit", "chain_anchors"),
    ];

    /// <summary>
    /// Fails startup when a tenant-scoped table is in neither list.
    ///
    /// <para><b>Why this is worth a startup check.</b> <c>TenantTables</c> is hand-maintained,
    /// and the failure mode when somebody forgets an entry is silent: the table still works,
    /// the tests still pass, and the only thing that changed is that the backstop no longer
    /// covers it. <c>ai.user_ai_settings</c> sat that way — the table holding every user's
    /// encrypted provider key was the one table in the granted schemas with a grant and no
    /// policy. A new tenant table is exactly when nobody is thinking about RLS, so the check
    /// runs then rather than relying on it being remembered.</para>
    ///
    /// <para>Pass every business <see cref="DbContext"/>; each contributes its own model.</para>
    /// </summary>
    public static void AssertCoverage(IEnumerable<DbContext> contexts, ILogger logger)
    {
        var known = new HashSet<(string, string)>(TenantTables);
        known.UnionWith(ExemptTables);

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var context in contexts)
        {
            var model = context.Model;
            foreach (var entity in model.GetEntityTypes())
            {
                // Only real tables, and only the ones carrying the tenant discriminator. An
                // owned type or a keyless projection has no table of its own to protect.
                if (entity.FindProperty(nameof(TenantEntity.TenantId)) is null)
                {
                    continue;
                }
                if (entity.GetTableName() is not { } table)
                {
                    continue;
                }
                var schema = entity.GetSchema() ?? model.GetDefaultSchema();
                if (schema is null)
                {
                    continue;
                }
                if (!known.Contains((schema, table)))
                {
                    missing.Add($"{schema}.{table}");
                }
            }
        }

        if (missing.Count == 0)
        {
            logger.LogInformation(
                "RLS coverage verified: {Covered} protected, {Exempt} deliberately exempt.",
                TenantTables.Length, ExemptTables.Length);
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to start: tenant-scoped table(s) {string.Join(", ", missing)} have a " +
            "TenantId but no Row-Level Security policy. Add each to RlsConfigurator.TenantTables, " +
            "or to ExemptTables with a comment saying why a cross-tenant scan needs it unfiltered.");
    }

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
