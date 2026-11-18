using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.Sites;

public static class SiteDeletionEndpoints
{
    public static IEndpointRouteBuilder MapSiteDeletion(this IEndpointRouteBuilder app)
    {
        // Deleting a site destroys its build history and its git repo, so it needs
        // the strongest site permission rather than the everyday editing one.
        app.MapDelete("/api/admin/sites/{id:guid}", async (
            Guid id, SiteDeleter deleter, CancellationToken ct) =>
        {
            var result = await deleter.DeleteAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SiteDeleted, "site")
            .ManagedByDeployments("sites");

        return app;
    }
}
