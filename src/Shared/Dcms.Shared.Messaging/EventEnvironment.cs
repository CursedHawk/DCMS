using Dcms.Shared.Data.Rls;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

/// <summary>
/// Which environment of its tenant an event is about (ADR 0028). The publisher stamps the
/// environment in force for the event's tenant as a header — none for production — so no event
/// record had to change; a consumer acts in it with <see cref="Scope{T}"/>.
/// </summary>
public static class EventEnvironment
{
    public const string Header = "Dcms-Environment";

    /// <summary>The environment the message names; production (the tenant's id) when it names none.</summary>
    public static Guid Of(NatsHeaders? headers, Guid tenantId) =>
        headers is not null && headers.TryGetValue(Header, out var value) && Guid.TryParse(value.ToString(), out var id)
            ? id
            : tenantId;

    /// <summary>Act as the message's tenant, in the environment it names, until disposed.</summary>
    public static IDisposable Scope<T>(this INatsJSMsg<T> message, Guid tenantId) =>
        RlsScope.Environment(tenantId, Of(message.Headers, tenantId));
}
