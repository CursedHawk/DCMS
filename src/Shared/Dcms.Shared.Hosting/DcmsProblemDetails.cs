using System.Diagnostics;
using Dcms.Shared.Audit.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Hosting;

/// <summary>
/// Turns an unhandled exception into an RFC 9457 response carrying the trace id and the
/// request id, so the person who hit it can quote something an operator can look up.
///
/// <para>Before this, an unhandled exception fell through to Kestrel's default: status 500, no
/// body, no identifier. The information needed to find the failure existed — the span was in
/// the trace store, the audit record had the trace id on it — and there was no way for a user
/// to name which one was theirs. "It broke around three o'clock" is not a query.</para>
///
/// <para><b>The body never contains the exception.</b> Outside Development the message, the
/// type and the stack trace stay in the log, and the trace id is the handle to them. That is
/// not a formality: exception messages on this platform quote connection strings, file paths
/// and occasionally the value that failed validation, and the failing request is exactly the
/// one most likely to be an attacker probing. The trace id discloses nothing on its own and
/// resolves to everything for someone who is allowed to look.</para>
/// </summary>
public sealed class DcmsExceptionHandler(
    IHostEnvironment environment,
    IProblemDetailsService problemDetails,
    ILogger<DcmsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        var requestId = context.Response.Headers[AuditMiddleware.CorrelationHeader].ToString();

        // Logged before the response is written: a client that disconnects mid-write must not
        // be able to make the record of its own failure disappear.
        logger.LogError(
            exception,
            "Unhandled exception on {Method} {Path} (trace {TraceId}, request {RequestId})",
            context.Request.Method,
            context.Request.Path,
            traceId,
            requestId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // A person in a browser gets a page; an API client gets the problem document below.
        // Same two identifiers either way — only the presentation differs.
        if (DcmsErrorPage.PrefersHtml(context.Request))
        {
            await DcmsErrorPage.WriteAsync(
                context, StatusCodes.Status500InternalServerError, traceId, requestId);
            return true;
        }

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = traceId,
            ["requestId"] = string.IsNullOrEmpty(requestId) ? null : requestId,
        };

        // Development only. The whole point of the production shape is that the detail is
        // reachable by trace id rather than shipped to the caller.
        if (environment.IsDevelopment())
        {
            extensions["exception"] = exception.ToString();
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "Quote the traceId when reporting this.",
                Extensions = extensions,
            },
        });
    }
}

public static class DcmsProblemDetailsExtensions
{
    /// <summary>
    /// Registers the handler. Called from <c>AddDcmsServiceDefaults</c>, so every service gets
    /// it from the line they all already call rather than from one each of them could forget.
    /// </summary>
    public static IServiceCollection AddDcmsProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Also runs for the statuses that never raise an exception — a 404, a 403, a 429 —
            // so a rate-limited caller can quote an id too. Those are the reports that most
            // often arrive with nothing else to go on.
            context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString());

            var requestId = context.HttpContext.Response.Headers[AuditMiddleware.CorrelationHeader].ToString();
            if (!string.IsNullOrEmpty(requestId))
            {
                context.ProblemDetails.Extensions.TryAdd("requestId", requestId);
            }
        });
        services.AddExceptionHandler<DcmsExceptionHandler>();
        return services;
    }

    /// <summary>
    /// Puts the handler in the pipeline. Must be called first, before anything that can throw —
    /// which includes the audit middleware, so the trace id it captured is still the one
    /// reported.
    /// </summary>
    public static IApplicationBuilder UseDcmsProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler();

        // Status codes that were never an exception — a 404 for an unpublished page, a 429
        // from the rate limiter — reach the same fork: page for a browser, problem document
        // for everything else. Only bodyless responses are filled in, so a handler that
        // already wrote its own 404 body keeps it.
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            if (!DcmsErrorPage.PrefersHtml(http.Request))
            {
                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http });
                return;
            }

            await DcmsErrorPage.WriteAsync(
                http,
                http.Response.StatusCode,
                Activity.Current?.TraceId.ToString(),
                http.Response.Headers[AuditMiddleware.CorrelationHeader].ToString());
        });
        return app;
    }
}
