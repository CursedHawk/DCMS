namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// An operator asked for one of the platform's own managed certificates to be reissued before it
/// is due.
///
/// <para>Paired with a durable <c>ReissueRequestedAt</c> on the certificate row, for the reason
/// the tenant-domain reissue already works that way: the flag makes it certain, because the
/// hourly sweep picks up anything flagged, and the event makes it immediate. A dropped message
/// costs an hour rather than the reissue, and whoever pressed the button does not have to know
/// which of the two carried it.</para>
///
/// <para>Its own event rather than reusing <c>TenantDomainVerified</c>, which the edge already
/// consumes: that one names a hostname and sends the edge down the per-hostname HTTP-01 path.
/// Pointing it at a managed certificate's label would order a wildcard over a challenge type the
/// CA refuses for wildcards.</para>
/// </summary>
public sealed record ManagedCertificateReissueRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ManagedCertificateId,
    string Name) : IDcmsEvent
{
    public int Version => 1;
}
