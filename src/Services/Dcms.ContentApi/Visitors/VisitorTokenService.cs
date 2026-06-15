using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.ContentApi.Visitors;

public sealed class VisitorTokenOptions
{
    public const string SectionName = "Visitor";

    /// <summary>Symmetric signing key (≥ 32 bytes). Supplied via Vault in prod.</summary>
    public string SigningKey { get; set; } = "dev-visitor-signing-key-change-me-please-32b";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed record RefreshTokenMaterial(string Token, string TokenHash, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and validates visitor JWTs. Tenant isolation comes from the audience
/// claim (dcms.site:{tenantId}): a token minted for one tenant fails validation
/// at another tenant's endpoint. Pure crypto — no I/O — so it is unit-tested.
/// </summary>
public sealed class VisitorTokenService(VisitorTokenOptions options)
{
    private const string Issuer = "dcms";
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.SigningKey));
    private readonly JsonWebTokenHandler _handler = new();

    public static string Audience(Guid tenantId) => $"dcms.site:{tenantId}";

    public string IssueAccessToken(Guid tenantId, Guid visitorId, string email)
    {
        var now = DateTimeOffset.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience(tenantId),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(options.AccessTokenMinutes).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = visitorId.ToString(),
                ["email"] = email,
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>Validates signature, lifetime and tenant audience. Returns the visitor id on success.</summary>
    public async Task<Guid?> ValidateAccessTokenAsync(string token, Guid tenantId)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience(tenantId),
            IssuerSigningKey = _key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid)
        {
            return null;
        }
        var sub = result.ClaimsIdentity.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public RefreshTokenMaterial IssueRefreshToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return new RefreshTokenMaterial(token, HashToken(token), DateTimeOffset.UtcNow.AddDays(options.RefreshTokenDays));
    }

    public static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
