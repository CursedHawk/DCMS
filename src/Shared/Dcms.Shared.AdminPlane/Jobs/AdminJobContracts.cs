namespace Dcms.AdminApi.Media
{
    /// <summary>One file of a Google Drive import: what admin-api's endpoint enqueues and jobs-worker's handler runs.</summary>
    public static class DriveFileJobContract
    {
        public const string Kind = "media.drive-file";

        /// <summary>The Data Protection purpose the token is sealed under: one key ring, admin-api seals, jobs-worker opens.</summary>
        public const string TokenPurpose = "Dcms.Jobs.DriveImport.Token.v1";
    }

    /// <param name="ProtectedToken">The picker's access token, encrypted with <see cref="DriveFileJobContract.TokenPurpose"/>.</param>
    public sealed record DriveFilePayload(string FileId, string Name, Guid? FolderId, string ProtectedToken, Guid? UploadedBy);
}

namespace Dcms.AdminApi.Environments
{
    /// <summary>The environment jobs (ADR 0026): enqueued by admin-api's endpoints, run by jobs-worker.</summary>
    public static class EnvironmentJobKinds
    {
        public const string Copy = "environments.copy";
        public const string Apply = "environments.apply";
        public const string Rollback = "environments.rollback";
    }

    public sealed record EnvironmentApplyPayload(Guid Source, Guid UpTo, Dictionary<string, string> Resolutions, Guid? AppliedBy, bool IsSuperAdmin = false);

    public sealed record EnvironmentRollbackPayload(Guid MigrationId, Dictionary<string, string> Resolutions, Guid? By = null, bool IsSuperAdmin = false);

    /// <param name="Data">The data scopes to copy as well (<see cref="Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier.Scope"/>); configuration always comes.</param>
    public sealed record EnvironmentCopyPayload(Guid Source, List<string>? Data = null);
}

namespace Dcms.AdminApi.Sites.Audits
{
    /// <summary>A site audit (#649): enqueued by admin-api's endpoint, run by jobs-worker.</summary>
    public static class SiteAuditJobContract
    {
        public const string Kind = "site.audit";
    }

    public sealed record SiteAuditPayload(Guid AuditId);
}
