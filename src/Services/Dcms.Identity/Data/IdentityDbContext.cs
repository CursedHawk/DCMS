using Dcms.Identity.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Identity.Data;

/// <summary>
/// Owns the "identity" schema: ASP.NET Core Identity tables plus the OpenIddict
/// entity sets (applications, authorizations, scopes, tokens).
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<DcmsUser, DcmsRole, Guid>(options)
{
    public const string Schema = "identity";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.UseOpenIddict();
    }
}
