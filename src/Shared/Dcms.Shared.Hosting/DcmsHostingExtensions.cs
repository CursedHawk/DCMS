using System.Reflection;
using System.Threading.RateLimiting;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Dcms.Shared.Hosting;

public static class DcmsHostingExtensions
{
    /// <summary>Resource namespace shared by every service, so one query selects the platform.</summary>
    private const string ServiceNamespace = "dcms";

    /// <summary>
    /// Explicit latency buckets for HTTP server duration.
    ///
    /// <para>The SDK default is a 20-bucket ladder reaching 10 seconds. Every bucket is a
    /// separate time series once multiplied by route, status and method, and the top half of
    /// that ladder is empty on this platform — a cached delivery read targets p95 under 30 ms
    /// and the slowest interactive path is a publish. Twelve buckets weighted towards the
    /// millisecond end give better resolution where the traffic actually is, at roughly half
    /// the series count.</para>
    /// </summary>
    private static readonly ExplicitBucketHistogramConfiguration LatencyBuckets = new()
    {
        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
    };

    /// <summary>
    /// Builds and transcodes are measured in seconds to minutes, not milliseconds; the latency
    /// ladder above would put every one of them in the overflow bucket.
    /// </summary>
    private static readonly ExplicitBucketHistogramConfiguration BuildDurationBuckets = new()
    {
        Boundaries = [1, 5, 10, 30, 60, 120, 300, 600, 1800],
    };

    /// <summary>
    /// Cross-cutting wiring shared by every service: Vault configuration,
    /// Serilog, OpenTelemetry (OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set),
    /// audit recording, and base health checks (Postgres added when
    /// ConnectionStrings:Postgres is present; Redis/NATS/MinIO checks register
    /// with their Add* extensions).
    /// </summary>
    /// <param name="auditProfile">
    /// Whether this service resolves the acting principal from an HTTP request or acts as
    /// itself while draining a queue. Every service already calls this method, which is why
    /// audit is wired here: it is the one line nobody can forget to write.
    /// </param>
    public static WebApplicationBuilder AddDcmsServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        AuditProfile auditProfile = AuditProfile.Api)
    {
        builder.Configuration.AddDcmsVault(serviceName);

        // Minted here rather than inside AddDcmsAudit so the same instance id can also become
        // the OpenTelemetry resource attribute below. AddDcmsAudit registers this with
        // TryAddSingleton, so registering first is what makes the audit log and the telemetry
        // agree on which process they are talking about — minting a second identity would give
        // two different ids for one process and quietly break that join.
        var serviceIdentity = new AuditServiceIdentity(serviceName);
        builder.Services.AddSingleton(serviceIdentity);

        // Records go to a Critical log line until a service adds AddDcmsAuditData (durable
        // outbox) — a floor rather than a destination, so an incompletely wired service still
        // records instead of silently discarding.
        builder.Services.AddDcmsAudit(serviceName, auditProfile);

        builder.Services.AddSingleton<DcmsMetrics>();
        builder.Services.AddSingleton<TenantEnrichmentProcessor>();
        builder.Services.AddSingleton<SensitiveAttributeProcessor>();
        builder.Services.AddDcmsProblemDetails();

        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);
        var environmentName = builder.Environment.EnvironmentName;

        // The instance the audit log stamps on every record, so a span, a log line and an audit
        // row all name the same process. Without it, chasing a producer-sequence gap back to a
        // restart means guessing which replica wrote what.
        var serviceInstanceId = serviceIdentity.Instance;
        var serviceVersion = typeof(DcmsHostingExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", serviceName)
                .WriteTo.Console();

            // Serilog stays the logging provider, so the log signal is exported by Serilog's own
            // OTLP sink rather than by OpenTelemetry's logging provider. Enabling both would ship
            // every line twice, and the duplicate would look like a real repeat in Loki.
            if (otlpEnabled)
            {
                loggerConfiguration.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint!;
                    options.Protocol = OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName,
                        ["service.namespace"] = ServiceNamespace,
                        ["service.version"] = serviceVersion,
                        ["service.instance.id"] = serviceInstanceId,
                        ["deployment.environment.name"] = environmentName,
                    };
                });
            }
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceNamespace: ServiceNamespace, serviceVersion: serviceVersion, serviceInstanceId: serviceInstanceId)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environmentName)]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes fire every few seconds on eight services. Left in, they are
                    // the majority of all spans and they say nothing.
                    options.Filter = context => !IsProbePath(context.Request.Path);
                })
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(DcmsActivitySource.Name)
                // Order matters: enrich first so the attributes exist, then scrub, so a tenant
                // attribute added above is still subject to the same test as everything else.
                .AddProcessor<TenantEnrichmentProcessor>()
                .AddProcessor<SensitiveAttributeProcessor>())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // An unsubscribed meter is dropped by the SDK without an error, so every meter
                // the platform defines is named here. DcmsMeterRegistrationTests fails the build
                // if one is added and this list is not.
                .AddMeter(DcmsMeters.All)
                .AddView("http.server.request.duration", LatencyBuckets)
                .AddView("dcms.site.build.duration", BuildDurationBuckets)
                .AddView("dcms.media.process.duration", BuildDurationBuckets));

        // Conditional for the same reason the Postgres health check below is: the Redis
        // instrumentation resolves IConnectionMultiplexer out of the container, and three of the
        // eight services (the workers) never register one. Asking unconditionally would turn a
        // service that simply does not use the cache into a startup failure.
        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
        {
            otel.WithTracing(tracing => tracing.AddRedisInstrumentation());
        }

        if (otlpEnabled)
        {
            otel.UseOtlpExporter();
        }

        var healthChecks = builder.Services.AddHealthChecks();

        var postgres = builder.Configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(postgres))
        {
            healthChecks.AddNpgSql(postgres, name: "postgres", tags: ["ready"]);
        }

        return builder;
    }

    /// <summary>
    /// Registers a per-client fixed-window global rate limiter for public delivery
    /// surfaces. Partitioned by client IP; limits/window are configurable under
    /// "RateLimiting" (defaults 120 req / 60 s). Health and SignalR hub paths are
    /// never limited. Enforced by <c>app.UseRateLimiter()</c>; returns 429.
    /// </summary>
    public static IServiceCollection AddDcmsRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:PermitLimit", 600);
        var windowSeconds = configuration.GetValue("RateLimiting:WindowSeconds", 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var path = httpContext.Request.Path;
                if (path.StartsWithSegments("/health") || path.StartsWithSegments("/hub"))
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    /// <summary>
    /// Adds standard hardening response headers to every response: nosniff,
    /// clickjacking and referrer controls, and a permissive-by-default but
    /// configurable Content-Security-Policy. Call early in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseDcmsSecurityHeaders(this WebApplication app)
    {
        var csp = app.Configuration["Security:ContentSecurityPolicy"];
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            if (!string.IsNullOrWhiteSpace(csp))
            {
                headers["Content-Security-Policy"] = csp;
            }
            await next();
        });
    }

    /// <summary>
    /// /health/live = process is up; /health = all dependency checks (used by
    /// compose healthchecks and the smoke script).
    /// </summary>
    public static WebApplication MapDcmsDefaultEndpoints(this WebApplication app)
    {
        // Health probes accept any method, so they register as mutating endpoints. Exempt
        // rather than silent: the coverage test should see a decision, not an omission.
        const string probeReason = "Liveness/readiness probe. Polled every few seconds by compose and the edge; it changes nothing.";

        app.MapHealthChecks("/health").AuditExempt(probeReason);
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        }).AuditExempt(probeReason);
        return app;
    }

    /// <summary>
    /// Paths whose spans are noise. Compose polls <c>/health/live</c> on eight containers every
    /// ten seconds, which is ~70k spans a day that describe nothing but the poller. The same
    /// paths are already excluded from the audit log for the same reason.
    /// </summary>
    private static bool IsProbePath(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
}
