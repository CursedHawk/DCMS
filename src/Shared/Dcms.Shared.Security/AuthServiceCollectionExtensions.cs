using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.Shared.Security;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds JwtBearer validation against the identity service's discovery
    /// document. Access tokens are signed (not encrypted) JWTs issued by
    /// OpenIddict; the audience must match this service's resource name.
    /// </summary>
    public static IServiceCollection AddDcmsResourceAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                {
                    jwt.MetadataAddress = options.MetadataAddress;
                }
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer ?? options.Authority,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorization();
        return services;
    }
}
