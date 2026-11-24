using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Agents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Agents;

/// <summary>
/// Desktop agents' tokens (ADR 0032, C2): a person mints one for one workspace, some environments
/// and optionally reads only, and their agent (Claude Code, …) calls admin-api with it. The token
/// acts as the person, never as more: the permissions are the person's own, read live, and an
/// agent is never a SuperAdmin (its principal has no role claims). On top of that,
/// <see cref="AgentScopeMiddleware"/> holds it to its workspace, environments and reads, and to the
/// routes offered to agents at all.
/// </summary>
public static class AgentTokenAuthentication
{
    public const string Scheme = "AgentToken";

    /// <summary>admin-api's default scheme: an agent token to <see cref="Scheme"/>, any other bearer to JWT.</summary>
    public const string SelectorScheme = "DcmsBearer";

    public static IServiceCollection AddAgentTokenAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, null, o => o.ForwardDefaultSelector = context =>
                AgentClaims.Carries(context.Request.Headers.Authorization.ToString()) ? Scheme : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, AgentTokenHandler>(Scheme, _ => { });
        return services;
    }

    /// <summary>A new token: the prefix, then 32 random bytes as base64url.</summary>
    public static string NewToken() => AgentClaims.TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>What a list shows to tell tokens apart: the first characters after the prefix.</summary>
    public static string HintOf(string token) => token[AgentClaims.TokenPrefix.Length..][..6];

    public static string CacheKey(string hash) => "agent-token:" + hash;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What one token is, as the handler caches it.</summary>
internal sealed record AgentGrant(Guid Id, Guid TenantId, Guid UserId, string? Email, string Name, string[] Environments, bool ReadOnly,
    DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);

internal sealed class AgentTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    TenancyDbContext db, IMemoryCache cache, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // ponytail: per-process cache; a revoke on another replica takes up to this long to bite.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!AgentClaims.Carries(header)) return AuthenticateResult.NoResult();
        var hash = AgentTokenAuthentication.Hash(header["Bearer ".Length..].Trim());

        var grant = await cache.GetOrCreateAsync(AgentTokenAuthentication.CacheKey(hash), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            // Before any tenant is known: the hash is the lookup, across tenants (ADR 0015).
            using var rls = RlsScope.Platform();
            var row = await db.AgentTokens.IgnoreTenantFilter().AsNoTracking()
                .Where(t => t.TokenHash == hash && t.RevokedAt == null)
                .Select(t => new { t.Id, t.TenantId, t.UserId, t.Name, t.Environments, t.ReadOnly, t.ExpiresAt, t.LastUsedAt })
                .FirstOrDefaultAsync(Context.RequestAborted);
            if (row is null) return null;
            var email = await db.Memberships.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == row.TenantId && m.UserId == row.UserId).Select(m => m.Email).FirstOrDefaultAsync(Context.RequestAborted);
            return new AgentGrant(row.Id, row.TenantId, row.UserId, email, row.Name, [.. row.Environments], row.ReadOnly, row.ExpiresAt, row.LastUsedAt);
        });
        var now = clock.GetUtcNow();
        if (grant is null) return AuthenticateResult.Fail("This agent token is not known, or was revoked.");
        if (grant.ExpiresAt <= now) return AuthenticateResult.Fail("This agent token has expired.");

        if (grant.LastUsedAt is null || now - grant.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            using var rls = RlsScope.Platform();
            await db.AgentTokens.IgnoreTenantFilter().Where(t => t.Id == grant.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
            cache.Set(AgentTokenAuthentication.CacheKey(hash), grant with { LastUsedAt = now }, CacheFor);
        }

        // The person, narrowed: no role claims, so never a SuperAdmin, whatever they are.
        var claims = new List<Claim>
        {
            new("sub", grant.UserId.ToString()),
            new("client_id", AgentClaims.ClientId),
            new(AgentClaims.Grant, grant.Id.ToString()),
            new(AgentClaims.Name, grant.Name),
            new(AgentClaims.TenantId, grant.TenantId.ToString()),
            new(AgentClaims.ReadOnly, grant.ReadOnly ? "true" : "false"),
        };
        if (grant.Email is not null) claims.Add(new Claim("email", grant.Email));
        claims.AddRange(grant.Environments.Select(e => new Claim(AgentClaims.Environment, e)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AgentTokenAuthentication.Scheme, "email", "role"));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AgentTokenAuthentication.Scheme));
    }
}

/// <summary>
/// Holds an agent to what its token says (ADR 0032), after the tenant, environment and membership
/// are resolved. An agent reaches only routes offered to agents (<see cref="CliOperationMetadata"/>,
/// not human-only): everything else, including any route nobody has decided about yet, is refused.
/// Then its workspace, its environments, and — read-only tokens — reads only.
/// </summary>
public sealed class AgentScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        var user = context.User;
        if (!user.IsAgent())
        {
            await next(context);
            return;
        }
        if (Refusal(context, user, tenant.TenantId) is { } reason)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = reason, code = "agent_scope" }, context.RequestAborted);
            return;
        }
        await next(context);
    }

    /// <summary>Why an agent may not make this request, or null (<see cref="AgentScope.Refusal"/>).</summary>
    public static string? Refusal(HttpContext context, ClaimsPrincipal user, Guid? tenantId) =>
        AgentScope.Refusal(context.GetEndpoint()?.Metadata.GetMetadata<CliOperationMetadata>(), user, tenantId,
            context.Request.Headers[EnvironmentResolution.Header].ToString() is { Length: > 0 } named ? named : TenantEnvironment.Production,
            context.Request.Method);
}

/// <summary>A person's own agent tokens: mint one for this workspace, list them, revoke one.</summary>
public static class AgentTokenEndpoints
{
    public sealed record MintRequest(string Name, string[]? Environments, bool ReadOnly, int? ExpiresInDays);

    public static IEndpointRouteBuilder MapAgentTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/me/agent-tokens", async (
            MintRequest body, HttpContext http, CurrentUser me, ITenantContext tenant, TenancyDbContext db,
            TimeProvider clock, AuditScope audit, CancellationToken ct) =>
        {
            // A token never mints another: what an agent may do is a person's decision, each time.
            if (http.User.IsAgent()) return Results.Json(new { error = "An agent cannot mint agent tokens.", code = "agent_scope" }, statusCode: 403);
            if (tenant.TenantId is not { } tenantId) return Results.BadRequest(new { error = "Name the workspace (X-Dcms-Tenant)." });
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) return Results.BadRequest(new { error = "Give the agent a name of up to 100 characters." });
            var days = body.ExpiresInDays ?? 90;
            if (days is < 1 or > 365) return Results.BadRequest(new { error = "A token lasts 1 to 365 days." });
            var userId = me.RequireUserId();

            // Each environment named must be one the person holds a role in: a token reaches no further than they do.
            var environments = (body.Environments ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (environments.Length > 0)
            {
                using var rls = RlsScope.Platform();
                var held = await db.Environments.IgnoreTenantFilter().AsNoTracking()
                    .Where(e => e.TenantId == tenantId && environments.Contains(e.Name)
                                && db.MemberRoles.IgnoreTenantFilter().Any(r => r.TenantId == tenantId && r.EnvironmentId == e.Id
                                                                               && r.Membership!.UserId == userId))
                    .Select(e => e.Name).ToListAsync(ct);
                if (environments.Except(held, StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
                {
                    return Results.BadRequest(new { error = $"You hold no role in: {string.Join(", ", missing)}." });
                }
            }

            var token = AgentTokenAuthentication.NewToken();
            var row = new AgentToken
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                Name = name,
                Environments = [.. environments],
                ReadOnly = body.ReadOnly,
                TokenHash = AgentTokenAuthentication.Hash(token),
                Hint = AgentTokenAuthentication.HintOf(token),
                CreatedAt = clock.GetUtcNow(),
                ExpiresAt = clock.GetUtcNow().AddDays(days),
            };
            db.AgentTokens.Add(row);
            await db.SaveChangesAsync(ct);
            audit.Declared?.For("agent_token", row.Id, name)
                .With("environments", environments.Length == 0 ? "all" : string.Join(",", environments))
                .With("read_only", body.ReadOnly)
                .With("expires_at", row.ExpiresAt);
            return Results.Ok(new
            {
                row.Id,
                // Shown once: only its hash is kept.
                token,
                row.Name,
                workspace = tenant.TenantSlug,
                environments,
                row.ReadOnly,
                row.ExpiresAt,
            });
        }).RequireAuthorization()
          .PermissionExempt("Self-scoped: a member mints a token for themselves, reaching no further than their own permissions.")
          .WithAudit(AuditActions.AgentTokenCreated, "agent_token", AuditCategory.Auth)
          .Cli("agents.mint", "Mints a desktop agent's token for this workspace (shown once).", CliRisk.Safe, humanOnly: true);

        app.MapGet("/api/admin/me/agent-tokens", async (HttpContext http, CurrentUser me, TenancyDbContext db, CancellationToken ct) =>
        {
            // The person's own tokens in every workspace: keyed by user, so platform scope (ADR 0015).
            using var rls = RlsScope.Platform();
            var userId = me.RequireUserId();
            var tokens = await db.AgentTokens.IgnoreTenantFilter().AsNoTracking()
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync(ct);
            var ids = tokens.Select(t => t.TenantId.ToString()).Distinct().ToList();
            var tenants = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => new { t.Identifier, Name = t.Name ?? t.Identifier }, ct);
            return Results.Ok(tokens.Select(t => new
            {
                t.Id,
                t.Name,
                t.Hint,
                workspace = tenants.GetValueOrDefault(t.TenantId.ToString())?.Identifier,
                workspaceName = tenants.GetValueOrDefault(t.TenantId.ToString())?.Name,
                t.Environments,
                t.ReadOnly,
                t.CreatedAt,
                t.ExpiresAt,
                t.LastUsedAt,
            }));
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped)
          .Cli("agents.list", "Your desktop agents' tokens, in every workspace.", CliRisk.Read, humanOnly: true);

        app.MapDelete("/api/admin/me/agent-tokens/{id:guid}", async (
            Guid id, HttpContext http, CurrentUser me, TenancyDbContext db, IMemoryCache cache, TimeProvider clock,
            AuditScope audit, CancellationToken ct) =>
        {
            if (http.User.IsAgent()) return Results.Json(new { error = "An agent cannot revoke agent tokens.", code = "agent_scope" }, statusCode: 403);
            using var rls = RlsScope.Platform();
            var userId = me.RequireUserId();
            var row = await db.AgentTokens.IgnoreTenantFilter().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
            if (row is null) return Results.NotFound();
            if (row.RevokedAt is null)
            {
                row.RevokedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            cache.Remove(AgentTokenAuthentication.CacheKey(row.TokenHash));
            audit.Declared?.InTenant(row.TenantId).For("agent_token", row.Id, row.Name);
            return Results.NoContent();
        }).RequireAuthorization()
          .AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped)
          .PermissionExempt("Self-scoped: a person revokes only their own agent tokens.")
          .WithAudit(AuditActions.AgentTokenRevoked, "agent_token", AuditCategory.Auth)
          .Cli("agents.revoke", "Revokes one of your agents' tokens: it stops working at once.", CliRisk.Dangerous, humanOnly: true);

        return app;
    }
}
