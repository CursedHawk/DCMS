namespace Dcms.Shared.Data.Sites;

public enum SiteAuditStatus { Queued, Building, Testing, Succeeded, Failed }

/// <summary>
/// One PageSpeed Insights audit of a site (#649): of its live address, or of an unpublished draft
/// built for the purpose (<see cref="SiteBuild.AuditToken"/>). Each strategy's result is kept as a
/// summary — scores, metrics and the audits that failed — not the whole Lighthouse report.
/// </summary>
public sealed class SiteAudit : EnvironmentEntity
{
    public Guid SiteId { get; set; }

    /// <summary><c>live</c> or <c>draft</c>.</summary>
    public string Target { get; set; } = "live";

    /// <summary>The branch a draft was built from.</summary>
    public string? Branch { get; set; }

    /// <summary>A draft's audit build; null for a live audit.</summary>
    public Guid? BuildId { get; set; }

    /// <summary>The address PageSpeed Insights was given.</summary>
    public string? Url { get; set; }

    public SiteAuditStatus Status { get; set; } = SiteAuditStatus.Queued;
    public string? MobileJson { get; set; }
    public string? DesktopJson { get; set; }
    public string? Error { get; set; }
    public Guid? RequestedBy { get; set; }
    public Guid? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
