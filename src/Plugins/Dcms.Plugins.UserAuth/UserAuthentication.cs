using System.Security.Claims;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// The <c>DcmsUser</c> authentication scheme (ADR 0022): the realm access token the edge
/// forwards for a signed-in site session, in <see cref="TokenHeader"/>. Signed by identity like
/// every DCMS token, and accepted only for the request's tenant — its audience is
/// <c>dcms.realm:{tenantId}</c> and its <c>realm</c> claim that tenant — so a token from one
/// tenant's site is nobody on another's. Never the default scheme, like <c>DcmsVisitor</c>.
///
/// <para>The principal keeps that audience, which is what <c>HttpCurrentActor</c> reads to
/// report <see cref="ActorKind.EndUser"/>, so the audit log names the user.</para>
/// </summary>
public static class UserAuthentication
{
    public const string SchemeName = "DcmsUser";

    /// <summary>Set by the edge only; it strips any copy a client sends (HeaderScrubbing).</summary>
    public const string TokenHeader = "X-Dcms-Realm-Token";

    public const string AudiencePrefix = "dcms.realm:";

    public static string Audience(Guid tenantId) => $"{AudiencePrefix}{tenantId}";

    private const string UserItem = "dcms.user-auth.user";

    public static void AddUserAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        services.AddAuthentication().AddJwtBearer(SchemeName, jwt =>
        {
            jwt.Authority = options.Authority;
            if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
            {
                jwt.MetadataAddress = options.MetadataAddress;
            }
            jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Issuer ?? options.Authority,
                // Any realm's here; which realm is checked against the request below.
                ValidateAudience = true,
                AudienceValidator = (audiences, _, _) => audiences.Any(a => a.StartsWith(AudiencePrefix, StringComparison.Ordinal)),
                ValidateLifetime = true,
                NameClaimType = "name",
                RoleClaimType = "role",
            };
            jwt.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var token = context.Request.Headers[TokenHeader].ToString();
                    if (token.Length == 0)
                    {
                        context.NoResult();
                    }
                    else
                    {
                        context.Token = token;
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId;
                    var principal = context.Principal!;
                    if (tenant is not { } tenantId
                        || !principal.HasClaim("aud", Audience(tenantId))
                        || principal.FindFirstValue("realm") != tenantId.ToString())
                    {
                        context.Fail("The user token belongs to another tenant.");
                    }
                    return Task.CompletedTask;
                },
            };
        });
    }

    /// <summary>The signed-in user of this request, if any; authenticated once per request.</summary>
    public static async Task<SiteUser?> CurrentUserAsync(this HttpContext http)
    {
        if (http.Items.TryGetValue(UserItem, out var cached))
        {
            return cached as SiteUser;
        }
        SiteUser? user = null;
        if (http.Request.Headers.ContainsKey(TokenHeader))
        {
            var result = await http.AuthenticateAsync(SchemeName);
            if (result.Succeeded && Guid.TryParse(result.Principal.FindFirstValue("sub"), out var id))
            {
                var principal = result.Principal;
                // As a policy naming this scheme would: an otherwise anonymous request is now this
                // user's, so its audit records name them (ActorKind.EndUser). Never over a platform user.
                if (http.User.Identity?.IsAuthenticated != true)
                {
                    http.User = principal;
                }
                user = new SiteUser(id, principal.FindFirstValue("email") ?? "", principal.FindFirstValue("name"),
                    principal.FindAll("groups").Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty).ToList());
            }
        }
        http.Items[UserItem] = user;
        return user;
    }
}
