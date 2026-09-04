using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Forces the JWT bearer options to be built now, so a contradictory configuration is a
    /// startup failure rather than a 500 on the first request that touches authentication.
    ///
    /// <para>JwtBearer validates its own options lazily, in a PostConfigure that runs the first
    /// time the handler is resolved. An Authority of <c>http://…</c> alongside
    /// <c>RequireHttpsMetadata=true</c> is rejected there — correctly — but the container has
    /// already reported itself started by then, and the first thing to notice is whatever
    /// request happened to arrive. On this platform that was the compose healthcheck: the
    /// service came up, answered 500 to <c>/health/live</c>, and failed the deploy gate with a
    /// stack trace about options configuration rather than a line saying which two settings
    /// disagree.</para>
    ///
    /// <para>Resolving them at startup costs one object and turns that into an immediate,
    /// named failure. Call it right after <c>builder.Build()</c>.</para>
    /// </summary>
    public static void ValidateDcmsResourceAuthentication(this IServiceProvider services)
    {
        var monitor = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        try
        {
            _ = monitor.Get(JwtBearerDefaults.AuthenticationScheme);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "The JWT bearer configuration is contradictory, so this service could accept no "
                + "token. Check Auth:Authority / Auth:MetadataAddress against "
                + "Auth:RequireHttpsMetadata — an http:// address needs RequireHttpsMetadata "
                + "false, which is how every service here reaches identity inside the compose "
                + $"network. Underlying error: {ex.Message}",
                ex);
        }
    }
}
