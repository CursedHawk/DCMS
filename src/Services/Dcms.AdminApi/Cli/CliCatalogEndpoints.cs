using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Agents;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Dcms.AdminApi.Cli;

/// <summary>
/// The operation catalog (ADR 0032, C3): what the dcms command line and desktop agents may call
/// here, described well enough to call it. Two halves, both filtered to the caller:
/// <list type="bullet">
/// <item>routes marked <c>.Cli(...)</c>: name, method, path, its path and query parameters, its
/// body's JSON Schema and its risk; only those the caller's permissions reach and, for an agent,
/// only those its token allows (never human-only routes; reads only for a read-only token);</item>
/// <item>the plugin and platform contracts: <c>plane=ai</c> for an agent, as the console's own
/// assistant sees them; <c>plane=admin</c> for a person.</item>
/// </list>
/// So a route marked today is in every client tomorrow, with no new CLI.
/// </summary>
public static class CliCatalogEndpoints
{
    public sealed record CliParameter(string Name, string Type, bool Required);

    public sealed record CliOperation(
        string Name, string Summary, string Risk, bool HumanOnly, string Method, string Path,
        IReadOnlyList<CliParameter> PathParameters, IReadOnlyList<CliParameter> Query, JsonNode? Body, IReadOnlyList<string>? Files,
        bool ReturnsExternalText);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static IEndpointRouteBuilder MapCliCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/cli/catalog", async (
            HttpContext http, ClaimsPrincipal user, IAuthorizationService authz, ITenantContext tenant,
            EndpointDataSource endpoints, ContractDispatcher dispatcher, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.BadRequest(new { error = "Name the workspace (X-Dcms-Tenant)." });
            var agent = user.IsAgent();
            var readOnly = user.IsReadOnlyAgent();
            var allowed = ContractEndpoints.Permissions(user, authz);

            var operations = new List<CliOperation>();
            foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
            {
                if (endpoint.Metadata.GetMetadata<CliOperationMetadata>() is not { } cli) continue;
                // For an agent, exactly what its token would let through (AgentScopeMiddleware): nothing it would be refused.
                var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
                if (agent && AgentScope.Refusal(cli, user, tenantId, EnvironmentName(http), method) is not null) continue;
                if (endpoint.Metadata.GetMetadata<PermissionMetadata>() is { } permission && !await allowed(permission.Permission)) continue;
                operations.Add(Describe(endpoint, cli));
            }

            var contracts = await dispatcher.CatalogAsync(tenantId, agent ? ContractPlane.Ai : ContractPlane.Admin, allowed, ct);
            if (readOnly)
            {
                contracts = [.. contracts.Select(c => c with { Operations = [.. c.Operations.Where(o => o.Risk == PluginSdk.Abstractions.Contracts.OpRisk.Read)] })
                    .Where(c => c.Operations.Count > 0)];
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                operations = operations.OrderBy(o => o.Name, StringComparer.Ordinal),
                contracts,
                plane = agent ? "ai" : "admin",
            }, Json);
            // Ask again with If-None-Match: the catalog changes with a release or the caller's permissions.
            var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..32]}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, no-cache";
            return http.Request.Headers.IfNoneMatch.ToString() == etag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(body, "application/json");
        }).RequireAuthorization()
          .Cli("cli.catalog", "What you may call here: every operation, with its parameters, body schema and risk.", CliRisk.Read);

        // What a desktop agent is taught about DCMS: `dcms link-agent` writes it as the agent's skill.
        app.MapGet("/api/admin/cli/skill", () => Results.Text(Skill.Value, "text/markdown; charset=utf-8"))
            .RequireAuthorization()
            .Cli("cli.skill", "The guide desktop agents read about DCMS: how it is shaped and how to work in it.", CliRisk.Read);

        return app;
    }

    private static readonly Lazy<string> Skill = new(() =>
    {
        using var stream = typeof(CliCatalogEndpoints).Assembly.GetManifestResourceStream("dcms-skill.md")
            ?? throw new InvalidOperationException("dcms-skill.md is not embedded.");
        return new StreamReader(stream).ReadToEnd();
    });

    private static string EnvironmentName(HttpContext http) =>
        http.Request.Headers["X-Dcms-Environment"].ToString() is { Length: > 0 } named ? named : "production";

    /// <summary>One route as a client needs it: where it is, what it takes, what it can change.</summary>
    public static CliOperation Describe(RouteEndpoint endpoint, CliOperationMetadata cli)
    {
        var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
        var pathNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        var parameters = handler?.GetParameters() ?? [];

        var path = endpoint.RoutePattern.Parameters
            .Select(p => new CliParameter(p.Name, TypeOf(parameters.FirstOrDefault(h => string.Equals(h.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.ParameterType), !p.IsOptional))
            .ToList();
        var query = parameters
            .Where(p => IsQuery(p, pathNames))
            .Select(p => new CliParameter(p.GetCustomAttribute<FromQueryAttribute>()?.Name ?? p.Name!, TypeOf(p.ParameterType), IsRequired(p)))
            .ToList();
        var bodyType = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
        return new CliOperation(cli.Name, cli.Summary, cli.Risk.ToString(), cli.HumanOnly, method,
            "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), path, query, bodyType is null || cli.Files is not null ? null : Schema(bodyType), cli.Files,
            cli.ExternalText);
    }

    private static bool IsQuery(ParameterInfo p, HashSet<string> pathNames)
    {
        if (p.Name is null || pathNames.Contains(p.Name)) return false;
        if (p.GetCustomAttribute<FromQueryAttribute>() is not null) return true;
        if (p.GetCustomAttributes().Any(a => a is FromBodyAttribute or FromServicesAttribute or FromHeaderAttribute or FromRouteAttribute or FromFormAttribute)) return false;
        return IsSimple(Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType);
    }

    private static bool IsSimple(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(decimal)
        || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeSpan)
        || (t.IsArray && IsSimple(t.GetElementType()!));

    private static bool IsRequired(ParameterInfo p) =>
        !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) is null
        && !(p.ParameterType == typeof(string) && new NullabilityInfoContext().Create(p).WriteState is NullabilityState.Nullable);

    private static string TypeOf(Type? type)
    {
        var t = type is null ? typeof(string) : Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(bool) ? "boolean"
            : t == typeof(int) || t == typeof(long) || t == typeof(short) ? "integer"
            : t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "number"
            : t == typeof(Guid) ? "uuid"
            : t.IsArray ? "array"
            : "string";
    }

    private static JsonNode? Schema(Type type)
    {
        try
        {
            return Json.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // A body the exporter cannot describe is still a body: the summary says what to send.
            return null;
        }
    }
}
