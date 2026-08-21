using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Audit;

/// <summary>Which ambient-context machinery a service needs.</summary>
public enum AuditProfile
{
    /// <summary>Serves HTTP requests: actor and correlation come from the request.</summary>
    Api,

    /// <summary>Drains a queue: actor is the service itself, context comes from message headers.</summary>
    Consumer,
}

public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit recorder and the ambient context it needs. Called from
    /// <c>AddDcmsServiceDefaults</c>, so every service gets it from the one line they all
    /// already have.
    ///
    /// The sink registered here is <see cref="LoggingAuditSink"/> — a floor, not a
    /// destination. A service that owns the audit schema replaces it with the transactional
    /// outbox sink, and the two services that cannot reach the schema replace it with the
    /// JetStream channel sink. Registering a working default rather than nothing means a
    /// service wired up incompletely still records instead of silently discarding.
    /// </summary>
    public static IServiceCollection AddDcmsAudit(
        this IServiceCollection services,
        string serviceName,
        AuditProfile profile)
    {
        services.TryAddSingleton(new AuditServiceIdentity(serviceName));

        // Singleton, because the things that need it are singletons: the NATS publisher and the
        // two outbox dispatchers. It holds an AsyncLocal, so what it hands back is still the
        // caller's own scope.
        services.TryAddSingleton<AuditAmbient>();

        // Emitted through System.Diagnostics.Metrics, which the existing OpenTelemetry wiring
        // already exports. An audit log that has stopped writing looks like a quiet platform;
        // these numbers are what tells the two apart.
        services.AddMetrics();
        services.TryAddSingleton<AuditMetrics>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditSink, LoggingAuditSink>();

        // Fallback tenant context. identity, ai-gateway and email-worker have no tenancy at
        // all, and the recorder must not fail to construct in them. Services that do have
        // tenancy register their own afterwards (AddDcmsTenancyData / AddNullTenantContext),
        // and the later registration is the one that resolves.
        services.TryAddScoped<ITenantContext, TenantlessContext>();

        services.TryAddScoped<AuditScope>();
        services.TryAddScoped<IAuditRecorder, AuditRecorder>();

        switch (profile)
        {
            case AuditProfile.Api:
                services.AddHttpContextAccessor();
                services.TryAddScoped<ICurrentActor, HttpCurrentActor>();
                break;

            case AuditProfile.Consumer:
                services.TryAddSingleton<ICurrentActor, SystemCurrentActor>();
                break;
        }

        return services;
    }

    /// <summary>
    /// For services that have no notion of a tenant. Records made here land on the platform
    /// scope (Guid.Empty), which is correct: a login or a token issue genuinely belongs to no
    /// tenant, and pretending otherwise would put it in someone's log.
    /// </summary>
    private sealed class TenantlessContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
