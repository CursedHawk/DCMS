using Dcms.Shared.Audit.Propagation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Audit.Http;

/// <summary>
/// Stamps who-and-why onto an outgoing service-to-service call.
///
/// <para>The HTTP counterpart of what every NATS publisher already does. A call one service
/// makes on a person's behalf carries a client-credentials token, so the receiving service sees
/// a client id and records the hop rather than the decision: "ServiceClient:dcms-platform-api-service
/// suspended tenant X". These headers are what let it record the operator instead.</para>
///
/// <para>Adds nothing when there is no ambient request — a background poll has no person behind
/// it, and a set of blank headers would read as a deliberate "unknown" rather than as silence.
/// Never overwrites a header the caller set itself.</para>
///
/// <para><b>Only for calls inside the platform.</b> The receiving end treats these as an
/// assertion by a peer and stamps what it restores
/// <see cref="AuditAttribution.Propagated"/>; putting this handler on a client that talks to a
/// third party would send the acting user's id and display name out of the platform.</para>
/// </summary>
public sealed class AuditPropagationHandler(AuditAmbient ambient) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in AuditPropagation.Capture(ambient.Current))
        {
            if (!request.Headers.Contains(key))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class AuditPropagationHandlerExtensions
{
    /// <summary>
    /// Carries the ambient actor, correlation id and trace context on every request this client
    /// makes. Use on clients that call another DCMS service; never on one that calls out.
    /// </summary>
    public static IHttpClientBuilder AddAuditPropagation(this IHttpClientBuilder builder)
    {
        builder.Services.TryAddTransient<AuditPropagationHandler>();
        return builder.AddHttpMessageHandler<AuditPropagationHandler>();
    }
}
