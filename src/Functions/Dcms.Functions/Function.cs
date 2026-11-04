using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.Functions;

/// <summary>
/// A function: mark the class with <see cref="FunctionAttribute"/> and say what starts it and
/// what it uses with the other attributes. <typeparamref name="TIn"/> is read from the input JSON
/// (a POST's body, an event, a message's payload) and what <see cref="Run"/> returns is sent back
/// as JSON. Use <see cref="JsonNode"/> for either to take or give any JSON.
/// </summary>
public abstract class Function<TIn, TOut> : IRunnable
{
    public abstract Task<TOut> Run(TIn input, Context ctx);

    /// <summary>An HTTP answer of your own: <c>return Respond(201, body)</c>. Any other return value is sent as JSON with 200. Declare TOut as <see cref="object"/> to return either.</summary>
    protected static HttpResult Respond(int status, object? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(new HttpResultBody(status, body, headers ?? new Dictionary<string, string>()));

    async Task<JsonNode?> IRunnable.RunAsync(JsonNode? input, Context ctx)
    {
        var typed = typeof(TIn) == typeof(JsonNode) ? (TIn)(object?)input! : input.Deserialize<TIn>(Wire.Options);
        return JsonSerializer.SerializeToNode<object?>(await Run(typed!, ctx), Wire.Results);
    }
}

internal interface IRunnable
{
    Task<JsonNode?> RunAsync(JsonNode? input, Context ctx);
}

/// <summary>What <c>Respond</c> builds: the shape the host turns into an HTTP response.</summary>
public sealed record HttpResult([property: JsonPropertyName("$response")] HttpResultBody Response);

public sealed record HttpResultBody(int Status, object? Body, IReadOnlyDictionary<string, string> Headers);

internal static class Wire
{
    /// <summary>Calls out: camelCase, and a value left null is left out (a partial update writes only what is set).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A function's result: camelCase, nulls kept, as JSON.stringify would.</summary>
    public static readonly JsonSerializerOptions Results = new(JsonSerializerDefaults.Web);
}
