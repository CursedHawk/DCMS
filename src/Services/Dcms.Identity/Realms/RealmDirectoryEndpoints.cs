using Dcms.Identity.Data;
using Dcms.Shared.Contracts.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Realms;

/// <summary>
/// A realm's people and groups, for its own signed-in users (#297): whom a site user can share a
/// saved view with. Authorised by that user's realm access token, which content-api passes on as
/// it received it from the edge. There is no service scope for this: no client secret anywhere
/// opens every tenant's directory, and a token opens only the realm that issued it, to a user who
/// may still sign in. Only active accounts are listed, and only their id, email and name.
/// </summary>
public static class RealmDirectoryEndpoints
{
    public const int PageSize = 20;
    public const int MaxIds = 50;

    public static IEndpointRouteBuilder MapRealmDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous to the authorization layer: the realm token is checked in CallerAsync, since no
        // scheme of identity's accepts tokens it issued for a site.
        var directory = app.MapGroup("/api/realm-directory").AllowAnonymous();

        directory.MapGet("/people", async (HttpContext http, string? search, string? ids, IdentityDbContext db, RealmStore store, CancellationToken ct) =>
        {
            if (await CallerAsync(http, store, ct) is not { } tenantId)
            {
                return Results.Unauthorized();
            }
            var people = db.RealmUsers.AsNoTracking().Where(u => u.TenantId == tenantId && u.Status == RealmUserStatus.Active);
            if (!string.IsNullOrWhiteSpace(ids))
            {
                var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
                if (wanted.Count > MaxIds)
                {
                    return Results.BadRequest(new { error = $"Ask for at most {MaxIds} people at once." });
                }
                people = people.Where(u => wanted.Contains(u.Id));
            }
            else if (!string.IsNullOrWhiteSpace(search))
            {
                if (search.Length > 100)
                {
                    return Results.BadRequest(new { error = "That search is too long." });
                }
                var pattern = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                people = people.Where(u => EF.Functions.ILike(u.Email, pattern) || (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern)));
            }
            var items = await people.OrderBy(u => u.DisplayName ?? u.Email).ThenBy(u => u.Email).Take(MaxIds)
                .Select(u => new RealmPerson(u.Id, u.Email, u.DisplayName)).ToListAsync(ct);
            return Results.Ok(new RealmPeople(string.IsNullOrWhiteSpace(ids) ? items.Take(PageSize).ToList() : items));
        });

        directory.MapGet("/groups", async (HttpContext http, IdentityDbContext db, RealmStore store, CancellationToken ct) =>
            await CallerAsync(http, store, ct) is { } tenantId
                ? Results.Ok(await db.RealmGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Name)
                    .Select(g => new RealmGroupName(g.Id, g.Name)).ToListAsync(ct))
                : Results.Unauthorized());

        return app;
    }

    /// <summary>
    /// The realm of the bearer's access token, when it is one identity signed for a site (the
    /// user's own or a builder preview's "view as") and its user may still sign in; else null.
    /// </summary>
    private static async Task<Guid?> CallerAsync(HttpContext http, RealmStore store, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }
        var server = http.RequestServices.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(header["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            ValidIssuer = RealmPreviewTokens.IssuerOf(server, http.Request),
            ValidateAudience = false, // held to the token's own realm below
            IssuerSigningKeys = server.SigningCredentials.Select(c => c.Key).ToList(),
            ValidTypes = [JsonWebTokenTypes.AccessToken],
        });
        if (!result.IsValid
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst(RealmStore.RealmClaim)?.Value, out var tenantId)
            || !result.ClaimsIdentity.FindAll(Claims.Audience).Any(a => a.Value == RealmAuthorization.Audience(tenantId))
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst(Claims.Subject)?.Value, out var userId)
            || await store.FindUserAsync(tenantId, userId, ct) is not { } user
            || !store.CanSignIn(user))
        {
            return null;
        }
        return tenantId;
    }
}
