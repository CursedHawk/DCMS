using Dcms.AiGateway.Providers;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Dcms.AiGateway;

/// <summary>
/// Adds each model call to the workspace's daily usage (<c>ai.usage</c>), which Settings → AI
/// usage charts. One upsert per call, keyed by day, payer, provider and model.
///
/// <para>Best effort: a usage row that fails to write is logged, never turned into a failed call.
/// The budget is enforced by <see cref="AiQuota"/>, not by this.</para>
/// </summary>
public sealed class AiUsageLedger(AiDbContext db, ILogger<AiUsageLedger> logger)
{
    public async Task RecordAsync(
        Guid tenantId, ResolvedCredentials credentials, string model,
        long input, long output, long cacheWrite, long cacheRead)
    {
        try
        {
            // Called service to service: nothing ambient says whose row this is.
            using var rls = RlsScope.Tenant(tenantId);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO ai.usage ("Id", "TenantId", "Day", "Source", "ProviderId", "UserId", "ProviderName", "ProviderKind", "Model",
                                      "Calls", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens")
                VALUES (@id, @tenant, @day, @source, @provider, @user, @name, @kind, @model, 1, @input, @output, @cacheRead, @cacheWrite)
                ON CONFLICT ("TenantId", "Day", "Source", "ProviderId", "UserId", "Model") DO UPDATE SET
                    "Calls" = ai.usage."Calls" + 1,
                    "InputTokens" = ai.usage."InputTokens" + EXCLUDED."InputTokens",
                    "OutputTokens" = ai.usage."OutputTokens" + EXCLUDED."OutputTokens",
                    "CacheReadTokens" = ai.usage."CacheReadTokens" + EXCLUDED."CacheReadTokens",
                    "CacheWriteTokens" = ai.usage."CacheWriteTokens" + EXCLUDED."CacheWriteTokens",
                    "ProviderName" = EXCLUDED."ProviderName",
                    "ProviderKind" = EXCLUDED."ProviderKind"
                """,
                [
                    new NpgsqlParameter("id", Guid.NewGuid()),
                    new NpgsqlParameter("tenant", tenantId),
                    new NpgsqlParameter("day", DateOnly.FromDateTime(DateTime.UtcNow)),
                    new NpgsqlParameter("source", credentials.UsageSource),
                    new NpgsqlParameter("provider", NpgsqlDbType.Uuid) { Value = (object?)credentials.ProviderId ?? DBNull.Value },
                    new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)credentials.PayingUserId ?? DBNull.Value },
                    new NpgsqlParameter("name", Clamp(credentials.ProviderName, AiProviderConfig.MaxName)),
                    new NpgsqlParameter("kind", credentials.Provider.ToString().ToLowerInvariant()),
                    new NpgsqlParameter("model", Clamp(model, 128)),
                    new NpgsqlParameter("input", input),
                    new NpgsqlParameter("output", output),
                    new NpgsqlParameter("cacheRead", cacheRead),
                    new NpgsqlParameter("cacheWrite", cacheWrite),
                ],
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI usage for tenant {TenantId} could not be recorded.", tenantId);
        }
    }

    private static string Clamp(string value, int max) => value.Length <= max ? value : value[..max];
}
