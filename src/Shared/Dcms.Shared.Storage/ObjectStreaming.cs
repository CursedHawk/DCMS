using Microsoft.AspNetCore.Http;

namespace Dcms.Shared.Storage;

/// <summary>
/// One resolved byte range of an object, as an HTTP <c>Range</c> header asked for it.
/// </summary>
/// <param name="Start">First byte served, inclusive.</param>
/// <param name="End">Last byte served, inclusive.</param>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;
}

/// <summary>What a <c>Range</c> header resolved to against a known object size.</summary>
public enum RangeOutcome
{
    /// <summary>No range asked for (or more than one, or a form we do not serve): send it whole.</summary>
    Whole,
    /// <summary>A single satisfiable range: send 206 with that slice.</summary>
    Partial,
    /// <summary>A range that cannot be met: send 416.</summary>
    Unsatisfiable,
}

/// <summary>
/// Serves objects out of storage without holding them in memory (PERF-01).
///
/// <para>Delivery used to download the whole object into a <see cref="MemoryStream"/> and hand
/// that to <c>Results.Stream</c>. It worked because a MemoryStream is seekable, which is exactly
/// what made it expensive: memory scaled with concurrency × file size, every Range request
/// downloaded the entire object to serve a slice of it, and site-host copied the buffer a second
/// time into a <c>byte[]</c>. With 50 MB uploads on a 7.6 GB host and anonymous delivery, that is
/// a memory-exhaustion DoS a handful of parallel video seeks can trigger.</para>
///
/// <para>Here the range is resolved from a HEAD, then only the requested bytes are pulled from
/// MinIO straight into the response body.</para>
/// </summary>
public static class ObjectStreaming
{
    /// <summary>
    /// Resolves a <c>Range</c> header against an object size. Pure, so the arithmetic that decides
    /// what bytes a caller receives is testable on its own.
    ///
    /// <para>Only a single <c>bytes=</c> range is honoured. A multi-range request is answered with
    /// the whole object, which RFC 9110 permits and which beats building a multipart body for a
    /// case no browser uses for media.</para>
    /// </summary>
    public static RangeOutcome ResolveRange(string? header, long size, out ByteRange range)
    {
        range = default;

        if (string.IsNullOrWhiteSpace(header)) return RangeOutcome.Whole;

        const string Prefix = "bytes=";
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return RangeOutcome.Whole;

        var spec = header[Prefix.Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal)) return RangeOutcome.Whole;

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return RangeOutcome.Whole;

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        // An empty object can satisfy no range at all, and `bytes=0-` against it is still a 416.
        if (size <= 0) return RangeOutcome.Unsatisfiable;

        long start;
        long end;

        if (fromText.Length == 0)
        {
            // Suffix form: `bytes=-500` means the LAST 500 bytes, not "up to byte 500".
            if (!long.TryParse(toText, out var suffix) || suffix <= 0) return RangeOutcome.Whole;
            if (suffix > size) suffix = size;
            start = size - suffix;
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(fromText, out start) || start < 0) return RangeOutcome.Whole;
            // A start past the last byte is the one case that must 416 rather than serve nothing.
            if (start >= size) return RangeOutcome.Unsatisfiable;

            if (toText.Length == 0)
            {
                end = size - 1;
            }
            else
            {
                if (!long.TryParse(toText, out end) || end < start) return RangeOutcome.Whole;
                if (end > size - 1) end = size - 1;
            }
        }

        range = new ByteRange(start, end);
        return RangeOutcome.Partial;
    }

    /// <summary>
    /// Writes an object to the response, honouring <c>Range</c>, and returns the result the
    /// endpoint should hand back. 404 when the object is gone.
    /// </summary>
    public static async Task<IResult> WriteObjectAsync(
        HttpContext http,
        IObjectStorage storage,
        string bucket,
        string key,
        string contentType,
        CancellationToken ct)
    {
        var info = await storage.StatAsync(bucket, key, ct);
        if (info is null)
        {
            return Results.NotFound();
        }

        var response = http.Response;
        response.Headers.AcceptRanges = "bytes";
        response.ContentType = contentType;

        switch (ResolveRange(http.Request.Headers.Range, info.Size, out var range))
        {
            case RangeOutcome.Unsatisfiable:
                // The size still has to be disclosed here, or a client cannot correct its ask.
                response.Headers.ContentRange = $"bytes */{info.Size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            case RangeOutcome.Partial:
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{info.Size}";
                response.ContentLength = range.Length;
                // HEAD must carry the headers and no body, exactly like the 200 path below.
                if (!HttpMethods.IsHead(http.Request.Method))
                {
                    await storage.GetToAsync(bucket, key, response.Body, range.Start, range.Length, ct);
                }
                return Results.Empty;

            default:
                response.ContentLength = info.Size;
                if (!HttpMethods.IsHead(http.Request.Method))
                {
                    await storage.GetToAsync(bucket, key, response.Body, ct: ct);
                }
                return Results.Empty;
        }
    }
}
