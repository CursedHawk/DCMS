using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.Functions;

/// <summary>
/// Everything a function reaches DCMS through. Each call is a host call: the sandbox has no
/// network, no files and no credentials, and the host checks every call against the function's
/// uses attributes, the deployer's permissions and the live configuration. <c>ctx.Db</c> and
/// <c>ctx.Plugins</c> are generated for the workspace (Sdk.g.cs).
/// </summary>
public sealed class Context
{
    private readonly Func<string, string, Task<string>> call;

    internal Context(Func<string, string, Task<string>> call, Action<string, string> log, JsonObject? context)
    {
        this.call = call;
        Log = new Logger(log);
        Functions = new FunctionsApi(this);
        Jobs = new JobsApi(this);
        Messages = new MessagesApi(this);
        Secrets = new SecretsApi(this);
        Email = new EmailApi(this);
        Notifications = new NotificationsApi(this);
        Content = new ContentApi(this);
        Links = new LinksApi(this);
        Http = new HttpApi(this);
        Request = context?["request"].Deserialize<HttpRequest>(Wire.Options);
        User = context?["user"].Deserialize<User>(Wire.Options);
        Trigger = context?["trigger"].Deserialize<TriggerInfo>(Wire.Options);
        Run = context?["run"].Deserialize<RunInfo>(Wire.Options);
    }

    public FunctionsApi Functions { get; }
    public JobsApi Jobs { get; }
    public MessagesApi Messages { get; }
    public SecretsApi Secrets { get; }
    public EmailApi Email { get; }
    public NotificationsApi Notifications { get; }
    public ContentApi Content { get; }
    public LinksApi Links { get; }
    public HttpApi Http { get; }
    public Logger Log { get; }

    /// <summary>The HTTP call that started this run; null for any other trigger.</summary>
    public HttpRequest? Request { get; }
    /// <summary>The signed-in caller, over HTTP; null otherwise.</summary>
    public User? User { get; }
    /// <summary>What started this run: the trigger and its event.</summary>
    public TriggerInfo? Trigger { get; }
    public RunInfo? Run { get; }

    /// <summary>A Dynamic Apps table by name. <c>ctx.Db.&lt;App&gt;.&lt;Table&gt;</c> is this, typed.</summary>
    public Table<TRow> Table<TRow>(string app, string table) => new(this, app, table);

    /// <summary>Another app's contract operation by name. <c>ctx.Plugins.&lt;App&gt;.&lt;Contract&gt;.&lt;Operation&gt;</c> is this, typed.</summary>
    public async Task<TOut?> Contract<TOut>(string instance, string contract, string operation, object? input = null) =>
        Read<TOut>(await CallAsync("contract", new JsonObject
        {
            ["path"] = new JsonArray(instance, contract, operation),
            ["args"] = input is null ? new JsonArray() : new JsonArray(Node(input)),
        }));

    internal async Task<JsonNode?> CallAsync(string op, JsonNode? args) =>
        JsonNode.Parse(await call(op, args?.ToJsonString() ?? "null"));

    internal static JsonNode? Node(object? value) => value as JsonNode ?? JsonSerializer.SerializeToNode(value, Wire.Options);

    internal static T? Read<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Wire.Options);
}

/// <summary>The host refused a call, or what it called failed; the message says why.</summary>
public sealed class HostException(string message) : Exception(message);

public sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, JsonNode? Body);

public sealed record User(string Id, string? Email, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Roles);

public sealed record TriggerInfo(string Kind, JsonNode? Detail);

/// <param name="Function">This function's name.</param>
/// <param name="Deployment">The deployment's number; 0 for a test from the editor.</param>
/// <param name="Depth">How many invokes deep this run is.</param>
/// <param name="TraceId">
/// This run's W3C trace id (32 hex digits): the trace of the request, job or event that started
/// it, which every run it sets off shares. Quote it to support, or send it on yourself as a
/// <c>traceparent</c> header; the platform does not add one to your outbound requests.
/// </param>
public sealed record RunInfo(string Function, int Deployment, int Depth, string? TraceId = null);

/// <summary>The run's log, shown with the run to holders of the app's logs permission. Console.WriteLine lands here too.</summary>
public sealed class Logger(Action<string, string> write)
{
    public void Info(params object?[] values) => write("info", Format(values));
    public void Debug(params object?[] values) => write("debug", Format(values));
    public void Warn(params object?[] values) => write("warn", Format(values));
    public void Error(params object?[] values) => write("error", Format(values));

    private static string Format(object?[] values) =>
        string.Join(" ", values.Select(v => v switch
        {
            null => "null",
            string s => s,
            JsonNode n => n.ToJsonString(),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ when v.GetType().IsPrimitive => v.ToString(),
            _ => JsonSerializer.Serialize(v, Wire.Results),
        }));
}

public sealed class FunctionsApi(Context ctx)
{
    /// <summary>Another function of this deployment, now; at most 5 deep.</summary>
    public async Task<TOut?> Invoke<TOut>(string name, object? input = null) =>
        Context.Read<TOut>(await ctx.CallAsync("functions.invoke", new JsonObject { ["name"] = name, ["input"] = Context.Node(input) }));
}

public sealed class JobsApi(Context ctx)
{
    /// <summary>Runs a function of this deployment later, on the workspace's jobs page; the same dedupeKey is one job.</summary>
    public async Task<string> Enqueue(string function, object? payload = null, string? dedupeKey = null)
    {
        var result = await ctx.CallAsync("jobs.enqueue", new JsonObject
        {
            ["function"] = function,
            ["payload"] = Context.Node(payload),
            ["options"] = dedupeKey is null ? null : new JsonObject { ["dedupeKey"] = dedupeKey },
        });
        return result?["id"]?.GetValue<string>() ?? "";
    }
}

public sealed class MessagesApi(Context ctx)
{
    /// <summary>To every function on the topic in this environment, as declared with <see cref="EmitsAttribute"/>. Returns how many runs it started.</summary>
    public async Task<int> Emit(string topic, object? payload = null) =>
        (await ctx.CallAsync("messages.emit", new JsonObject { ["topic"] = topic, ["payload"] = Context.Node(payload) }))?["started"]?.GetValue<int>() ?? 0;
}

public sealed class SecretsApi(Context ctx)
{
    /// <summary>A secret of this app, as declared with <see cref="UsesSecretsAttribute"/>; null when it is not set.</summary>
    public async Task<string?> Get(string name) =>
        (await ctx.CallAsync("secrets.get", new JsonObject { ["name"] = name }))?.GetValue<string>();

    /// <summary>
    /// Stands for the secret in an <see cref="HttpApi.Fetch"/>'s URL or headers, e.g.
    /// <c>["Authorization"] = $"Bearer {ctx.Secrets.Ref("STRIPE_KEY")}"</c>: the host puts the
    /// value in, so it never reaches the function, its log or its trace.
    /// </summary>
    public string Ref(string name) => $"{{{{dcms-secret:{name}}}}}";
}

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string? Text = null, string? Html = null, string? ReplyTo = null);

public sealed class EmailApi(Context ctx)
{
    /// <summary>At most 20 recipients. Text is escaped; html is sanitized. Needs <see cref="UsesEmailAttribute"/>. Returns the number of recipients.</summary>
    public async Task<int> Send(EmailMessage message) =>
        (await ctx.CallAsync("email.send", Context.Node(message)))?["recipients"]?.GetValue<int>() ?? 0;
}

public sealed record Notification(string Title, string Body, string? Severity = null, string? LinkPath = null, string? Audience = null);

public sealed class NotificationsApi(Context ctx)
{
    /// <summary>To members holding the audience permission (the first of <see cref="UsesNotificationsAttribute"/> when left out).</summary>
    public async Task Raise(Notification notification) => await ctx.CallAsync("notifications.raise", Context.Node(notification));
}

public sealed record ContentPage(IReadOnlyList<JsonNode> Items, int Page, int PageSize, int TotalCount);

public sealed class ContentApi(Context ctx)
{
    /// <summary>A published content item, or null. Needs <see cref="UsesContentAttribute"/>.</summary>
    public async Task<JsonNode?> Get(string contentType, string slug, string? instanceId = null) =>
        await ctx.CallAsync("content.get", new JsonObject { ["contentType"] = contentType, ["slug"] = slug, ["instanceId"] = instanceId });

    public async Task<ContentPage> List(string contentType, int? page = null, int? pageSize = null, string? instanceId = null) =>
        Context.Read<ContentPage>(await ctx.CallAsync("content.list", new JsonObject
        {
            ["contentType"] = contentType, ["page"] = page, ["pageSize"] = pageSize, ["instanceId"] = instanceId,
        }))!;
}

public sealed class LinksApi(Context ctx)
{
    /// <summary>A contract operation of another environment's app, through a link declared with <see cref="UsesLinksAttribute"/>.</summary>
    public async Task<TOut?> Call<TOut>(string environment, string app, string contract, string operation, object? input = null) =>
        Context.Read<TOut>(await ctx.CallAsync("links.call", new JsonObject
        {
            ["environment"] = environment, ["app"] = app, ["contract"] = contract, ["operation"] = operation, ["input"] = Context.Node(input),
        }));

    /// <summary>Records of another environment's app: <c>method</c> is find, get, create, update or delete; the arguments follow the table name.</summary>
    public async Task<JsonNode?> Records(string environment, string app, string method, string table, params object?[] args) =>
        await ctx.CallAsync("links.records", new JsonObject
        {
            ["environment"] = environment, ["app"] = app, ["table"] = table, ["method"] = method,
            ["args"] = new JsonArray(args.Select(Context.Node).ToArray()),
        });
}

/// <param name="Bytes">The body as it came.</param>
/// <param name="Url">Where it came from, after any redirects.</param>
public sealed record HttpResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Bytes, string Url)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The body as UTF-8 text.</summary>
    public string Body => System.Text.Encoding.UTF8.GetString(Bytes);

    /// <summary>The body read as JSON.</summary>
    public T? Json<T>() => Bytes.Length == 0 ? default : JsonSerializer.Deserialize<T>(Bytes, Wire.Options);
}

public sealed class HttpApi(Context ctx)
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient());

    /// <summary>
    /// A ready <see cref="HttpClient"/>, for code and packages written against one (ADR 0031). It
    /// goes through the same broker as <see cref="Fetch"/>: only the declared hosts answer, and a
    /// secret's <see cref="SecretsApi.Ref"/> works in its headers and URL.
    /// </summary>
    public HttpClient Client => SharedClient.Value;

    /// <summary>
    /// Outbound HTTP, to the hosts declared with <see cref="UsesEgressAttribute"/> only, through
    /// redirects to them too (<paramref name="followRedirects"/>). The body is sent as it is when
    /// it is <c>byte[]</c> or <c>string</c>, else as JSON. <paramref name="timeoutMs"/>: 10 s
    /// unless said, at most 30 s.
    /// </summary>
    public async Task<HttpResponse> Fetch(string url, string method = "GET", IReadOnlyDictionary<string, string>? headers = null,
        object? body = null, int? timeoutMs = null, bool followRedirects = true)
    {
        var sent = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        byte[]? bytes = body switch
        {
            null => null,
            byte[] b => b,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            _ => JsonSerializer.SerializeToUtf8Bytes(body, Wire.Options),
        };
        if (body is not (null or byte[] or string)) sent.TryAdd("content-type", "application/json");
        var answer = await ctx.CallAsync("http.fetch", new JsonObject
        {
            ["url"] = url,
            ["method"] = method,
            ["headers"] = Context.Node(sent),
            ["bodyBase64"] = bytes is null ? null : Convert.ToBase64String(bytes),
            ["redirect"] = followRedirects ? "follow" : "manual",
            ["timeoutMs"] = timeoutMs,
            ["raw"] = true,
        });
        return new HttpResponse(
            answer?["status"]?.GetValue<int>() ?? 0,
            (answer?["headers"] as JsonObject)?.ToDictionary(h => h.Key, h => h.Value?.GetValue<string>() ?? "") ?? [],
            answer?["bodyBase64"]?.GetValue<string>() is { } encoded ? Convert.FromBase64String(encoded) : [],
            answer?["url"]?.GetValue<string>() ?? url);
    }
}
