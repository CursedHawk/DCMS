using System.Threading.RateLimiting;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Dcms.Shared.Hosting;

public static class DcmsHostingExtensions
{
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

        // Records go to a Critical log line until a service adds AddDcmsAuditData (durable
        // outbox) — a floor rather than a destination, so an incompletely wired service still
        // records instead of silently discarding.
        builder.Services.AddDcmsAudit(serviceName, auditProfile);

        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.Console());

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
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
}
