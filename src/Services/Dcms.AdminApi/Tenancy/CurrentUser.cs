using System.Security.Claims;

namespace Dcms.AdminApi.Tenancy;

/// <summary>Reads the authenticated user's identity from the access token claims.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public string? Email => Principal?.FindFirstValue("email");

    public string? Name => Principal?.FindFirstValue("name");

    public bool IsSuperAdmin => Principal?.FindAll("role").Any(c => c.Value == "SuperAdmin") ?? false;

    public Guid RequireUserId() =>
        UserId ?? throw new InvalidOperationException("No authenticated user.");
}
