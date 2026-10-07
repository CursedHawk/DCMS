using System.Collections.Concurrent;
using System.Text;
using Dcms.Identity.Data;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dcms.Identity.Realms;

/// <summary>Realm providers' client secrets, under identity's own Transit key — no other service can turn them back into plaintext.</summary>
public sealed class RealmSecrets(ITransitEncryptor transit)
{
    public const string TransitKey = "dcms-realm-secrets";

    public Task<string> EncryptAsync(string secret, CancellationToken ct) => transit.EncryptAsync(TransitKey, Encoding.UTF8.GetBytes(secret), ct);

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken ct) =>
        Encoding.UTF8.GetString(await transit.DecryptAsync(TransitKey, ciphertext, ct));
}

/// <summary>Where a provider's sign-in comes back to, and the short-lived cookie holding its answer until the realm decides.</summary>
public static class RealmExternal
{
    public const string Scheme = "DcmsRealmExternal";
    public const string SchemePrefix = "realm-oidc-";

    /// <summary>The provider id the challenge was for, carried through the round trip and checked on return.</summary>
    public const string ProviderItem = "dcms.realm.provider";

    public static string SchemeFor(Guid providerId) => SchemePrefix + providerId.ToString("N");

    /// <summary>The redirect URI to register at the provider; stable across slug and key changes.</summary>
    public static string CallbackPath(Guid providerId) => $"/realm/sso/{providerId:N}/callback";
}

/// <summary>Lets tests point provider back-channel calls (discovery, keys, token) at a stub.</summary>
public sealed class RealmOidcBackchannel(HttpMessageHandler handler)
{
    public HttpMessageHandler Handler { get; } = handler;
}

/// <summary>
/// One OpenID Connect scheme per realm provider, added while running (ADR 0022): the platform's
/// own, well-trodden <see cref="OpenIdConnectHandler"/> does state, nonce, PKCE, the code
/// exchange and token validation for every tenant's Google, Entra or OIDC client, each with its
/// own callback path. A provider's settings are loaded into this cache before its scheme is
/// used, because options are built synchronously; changing a provider evicts both.
/// </summary>
public sealed class RealmOidcSchemes(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    IServiceScopeFactory scopes)
{
    internal sealed record Settings(RealmProvider Provider, string ClientSecret);

    private readonly ConcurrentDictionary<string, Settings> _settings = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal Settings? For(string scheme) => _settings.GetValueOrDefault(scheme);

    /// <summary>The provider's scheme, registered with its current settings.</summary>
    public async Task<string> EnsureAsync(RealmProvider provider, CancellationToken ct)
    {
        var name = RealmExternal.SchemeFor(provider.Id);
        if (_settings.ContainsKey(name) && await schemes.GetSchemeAsync(name) is not null)
        {
            return name;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (!_settings.ContainsKey(name))
            {
                using var scope = scopes.CreateScope();
                var secret = provider.SecretCiphertext is { } ciphertext
                    ? await scope.ServiceProvider.GetRequiredService<RealmSecrets>().DecryptAsync(ciphertext, ct)
                    : throw new InvalidOperationException($"Realm provider {provider.Key} has no client secret.");
                _settings[name] = new Settings(provider, secret);
                optionsCache.TryRemove(name);
            }
            if (await schemes.GetSchemeAsync(name) is null)
            {
                schemes.AddScheme(new AuthenticationScheme(name, provider.DisplayName, typeof(OpenIdConnectHandler)));
            }
            return name;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The provider a callback is for, registered if this process has not seen it yet — after a
    /// restart, or on another replica than the one that sent the person out.
    /// </summary>
    public async Task EnsureByIdAsync(Guid providerId, CancellationToken ct)
    {
        if (_settings.ContainsKey(RealmExternal.SchemeFor(providerId)))
        {
            return;
        }
        using var scope = scopes.CreateScope();
        var provider = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().RealmProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId && p.Enabled && p.Kind != RealmProviderKind.Dcms, ct);
        if (provider is not null)
        {
            await EnsureAsync(provider, ct);
        }
    }

    public void Evict(Guid providerId)
    {
        var name = RealmExternal.SchemeFor(providerId);
        _settings.TryRemove(name, out _);
        optionsCache.TryRemove(name);
        schemes.RemoveScheme(name);
    }
}

/// <summary>Builds a realm provider's OIDC options from <see cref="RealmOidcSchemes"/>' cache.</summary>
public sealed class RealmOidcOptions(RealmOidcSchemes schemes, IConfiguration configuration, IServiceProvider services)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) { }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name is null || !name.StartsWith(RealmExternal.SchemePrefix, StringComparison.Ordinal) || schemes.For(name) is not { } settings)
        {
            return;
        }
        var provider = settings.Provider;
        options.SignInScheme = RealmExternal.Scheme;
        options.Authority = Authority(provider);
        options.ClientId = provider.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.CallbackPath = RealmExternal.CallbackPath(provider.Id);
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.RequireHttpsMetadata = !configuration.GetValue("Identity:AllowInsecureHttp", false);
        if (services.GetService<RealmOidcBackchannel>() is { } stub)
        {
            options.BackchannelHttpHandler = stub.Handler;
        }

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (provider.Kind == RealmProviderKind.Google && provider.HostedDomain is { } hd)
            {
                // A hint to Google's account chooser; the hd claim is what is enforced, below.
                context.ProtocolMessage.SetParameter("hd", hd);
            }
            context.ProtocolMessage.Prompt = "select_account";
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            if (provider.Kind == RealmProviderKind.Google && provider.HostedDomain is { } hd
                && !string.Equals(context.Principal?.FindFirst("hd")?.Value, hd, StringComparison.OrdinalIgnoreCase))
            {
                context.Fail($"Only {hd} accounts can sign in here.");
            }
            return Task.CompletedTask;
        };
        // A refusal or a provider error comes back to the realm's sign-in page, not an exception.
        options.Events.OnRemoteFailure = context =>
        {
            var slug = context.Properties?.Items.TryGetValue("dcms.realm.slug", out var s) == true ? s : null;
            context.Response.Redirect(slug is null ? "/" : $"/realm/{Uri.EscapeDataString(slug)}/login?error=sso");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    public static string Authority(RealmProvider provider) => provider.Kind switch
    {
        RealmProviderKind.Google => "https://accounts.google.com",
        RealmProviderKind.Entra => $"https://login.microsoftonline.com/{provider.EntraTenant}/v2.0",
        _ => provider.Issuer!.TrimEnd('/'),
    };
}

/// <summary>Registers a provider's scheme before the authentication middleware looks for its callback.</summary>
public sealed class RealmOidcCallbackMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RealmOidcSchemes schemes)
    {
        var path = context.Request.Path.Value;
        if (path is not null && path.StartsWith("/realm/sso/", StringComparison.Ordinal) && path.EndsWith("/callback", StringComparison.Ordinal)
            && Guid.TryParseExact(path["/realm/sso/".Length..^"/callback".Length], "N", out var providerId))
        {
            await schemes.EnsureByIdAsync(providerId, context.RequestAborted);
        }
        await next(context);
    }
}
