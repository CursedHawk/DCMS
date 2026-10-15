using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dcms.Shared.Caching.Idempotency;

/// <summary>
/// One <c>Idempotency-Key</c> is one operation (ADR 0024): a repeated request carrying a key the
/// service has already seen is answered with the first response instead of being performed again.
///
/// <list type="bullet">
///   <item>The first request with a key claims it in Redis (<c>SET NX</c>) and runs.</item>
///   <item>A repeat while the first is still running gets <c>409 idempotency-in-flight</c>.</item>
///   <item>A repeat after it finished gets the stored response, marked <c>Idempotency-Replayed</c>.</item>
///   <item>The same key on a different request (method, path, query or body) is a client bug:
///   <c>422 idempotency-key-mismatch</c>.</item>
/// </list>
///
/// <para><b>What is not remembered.</b> A 5xx, an exception, and the statuses that say "try again"
/// (401, 403, 408, 429) release the key: the operation did not happen, and the SPA's own retry —
/// a 401 renewal replays with the same key — must be able to run it.</para>
///
/// <para><b>Who must send one.</b> Where <see cref="IdempotencyOptions.RequireForUsers"/> is set
/// (admin-api, platform-api), a person's mutating request without a key is refused: the consoles
/// always send one, so a request without one is something else replaying a request by hand.
/// Service principals and anonymous callers are honoured when they send a key and never refused
/// for leaving it out: webhooks and service-to-service calls dedupe by their own means.</para>
///
/// <para>Fails open: if Redis cannot be reached the request runs as if no key had been sent,
/// because refusing every write on the platform during a cache outage is the larger harm.</para>
/// </summary>
public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IdempotencyOptions options,
    IConnectionMultiplexer redis,
    ILogger<IdempotencyMiddleware> logger)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const int MaxStoredBody = 256 * 1024;
    private const long MaxHashedBody = 2 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method) && !HttpMethods.IsDelete(request.Method)
            || context.WebSockets.IsWebSocketRequest
            || context.GetEndpoint()?.Metadata.GetMetadata<IdempotencyExemptAttribute>() is not null)
        {
            await next(context);
            return;
        }

        var key = request.Headers[Header].ToString();
        if (key.Length == 0)
        {
            if (options.RequireForUsers && IsUser(context.User))
            {
                await RefuseAsync(context, StatusCodes.Status400BadRequest, "idempotency-key-required");
                return;
            }
            await next(context);
            return;
        }
        if (key.Length > 128 || key.Any(c => c is < '!' or > '~'))
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "idempotency-key-invalid");
            return;
        }

        // Scoped to the caller and workspace, so one person's key can never answer for another.
        var redisKey = $"idem:{options.Service}:{context.User.FindFirst("sub")?.Value ?? "anon"}:"
                       + $"{request.Headers["X-Dcms-Tenant"]}:{key}";
        var fingerprint = await FingerprintAsync(request, context.RequestAborted);

        IDatabase db;
        bool claimed;
        try
        {
            db = redis.GetDatabase();
            claimed = await db.StringSetAsync(redisKey, Serialize(new Entry(InFlight, fingerprint)), Ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Idempotency store unreachable; running {Method} {Path} without the guard.", request.Method, request.Path);
            await next(context);
            return;
        }

        if (!claimed)
        {
            await AnswerRepeatAsync(context, db, redisKey, fingerprint);
            return;
        }

        var original = context.Response.Body;
        var capture = new CaptureStream(original, MaxStoredBody);
        context.Response.Body = capture;
        try
        {
            await next(context);
        }
        catch
        {
            await ReleaseAsync(db, redisKey);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        var status = context.Response.StatusCode;
        if (status >= 500 || status is 401 or 403 or 408 or 429)
        {
            await ReleaseAsync(db, redisKey);
            return;
        }

        var response = context.Response;
        var done = new Entry(Done, fingerprint)
        {
            Status = status,
            ContentType = response.ContentType,
            Location = response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
            Body = capture.Overflowed ? null : Convert.ToBase64String(capture.Captured),
            Overflowed = capture.Overflowed,
        };
        try
        {
            await db.StringSetAsync(redisKey, Serialize(done), Ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The work is done either way; a repeat now runs again rather than replaying.
            logger.LogWarning(ex, "Could not record the outcome of idempotency key for {Path}.", request.Path);
        }
    }

    private async Task AnswerRepeatAsync(HttpContext context, IDatabase db, string redisKey, string fingerprint)
    {
        var stored = Deserialize(await db.StringGetAsync(redisKey));
        if (stored is null || stored.State == InFlight)
        {
            if (stored is not null && stored.Fingerprint != fingerprint)
            {
                await RefuseAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency-key-mismatch");
                return;
            }
            context.Response.Headers.RetryAfter = "1";
            await RefuseAsync(context, StatusCodes.Status409Conflict, "idempotency-in-flight");
            return;
        }
        if (stored.Fingerprint != fingerprint)
        {
            await RefuseAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency-key-mismatch");
            return;
        }
        if (stored.Overflowed)
        {
            // Done, but its answer was too large to keep. Saying so beats performing it again.
            await RefuseAsync(context, StatusCodes.Status409Conflict, "idempotency-already-processed");
            return;
        }

        var response = context.Response;
        response.StatusCode = stored.Status;
        response.Headers[ReplayedHeader] = "true";
        if (stored.ContentType is not null)
        {
            response.ContentType = stored.ContentType;
        }
        if (stored.Location is not null)
        {
            response.Headers.Location = stored.Location;
        }
        var body = stored.Body is null ? [] : Convert.FromBase64String(stored.Body);
        if (body.Length > 0)
        {
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private async Task ReleaseAsync(IDatabase db, string redisKey)
    {
        try
        {
            await db.KeyDeleteAsync(redisKey);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // It expires on its own; until then a retry is told the first is still in flight.
            logger.LogWarning(ex, "Could not release idempotency key {Key}.", redisKey);
        }
    }

    /// <summary>
    /// What makes two requests the same request: method, path, query and body. A body too large or
    /// of unknown length (an upload) is identified by its length and type instead of being read
    /// twice; a multipart upload is never buffered.
    /// </summary>
    private static async Task<string> FingerprintAsync(HttpRequest request, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{request.QueryString}\n"));

        var length = request.ContentLength;
        var multipart = request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
        if (length is > 0 and <= MaxHashedBody && !multipart)
        {
            request.EnableBuffering();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            request.Body.Position = 0;
        }
        else
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{length}\n{request.ContentType}"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsUser(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirst("sub")?.Value, out _);

    private static Task RefuseAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error });
    }

    private const string InFlight = "inflight";
    private const string Done = "done";

    private sealed record Entry(string State, string Fingerprint)
    {
        public int Status { get; init; }
        public string? ContentType { get; init; }
        public string? Location { get; init; }
        public string? Body { get; init; }
        public bool Overflowed { get; init; }
    }

    private static string Serialize(Entry entry) => JsonSerializer.Serialize(entry);

    private static Entry? Deserialize(RedisValue value) =>
        value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Entry>(value.ToString());

    /// <summary>Passes every write through, keeping a copy of the first <c>limit</c> bytes.</summary>
    private sealed class CaptureStream(Stream inner, int limit) : Stream
    {
        private readonly MemoryStream _copy = new();

        public bool Overflowed { get; private set; }
        public byte[] Captured => _copy.ToArray();

        private void Keep(ReadOnlySpan<byte> data)
        {
            if (Overflowed)
            {
                return;
            }
            if (_copy.Length + data.Length > limit)
            {
                Overflowed = true;
                _copy.SetLength(0);
                return;
            }
            _copy.Write(data);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Keep(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <param name="Service">Names this service in the key, so two services never share one.</param>
/// <param name="RequireForUsers">Refuse a person's mutating request that carries no key.</param>
public sealed record IdempotencyOptions(string Service, bool RequireForUsers);

/// <summary>
/// The endpoint is exempt, and says why: a callback a provider calls, a write that is idempotent
/// by nature, a transport endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotencyExemptAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

public static class IdempotencyExtensions
{
    /// <summary>
    /// After authentication (the key is scoped to the caller) and before authorization (so a
    /// refused request releases its key rather than being remembered as refused).
    /// </summary>
    public static IApplicationBuilder UseDcmsIdempotency(this IApplicationBuilder app, string service, bool requireForUsers) =>
        app.UseMiddleware<IdempotencyMiddleware>(new IdempotencyOptions(service, requireForUsers));

    public static TBuilder IdempotencyExempt<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotencyExemptAttribute(reason));
}
