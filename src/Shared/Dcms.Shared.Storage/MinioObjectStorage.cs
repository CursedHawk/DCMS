using System.Buffers;
using System.Runtime.CompilerServices;
using Minio;
using Minio.DataModel.Args;

namespace Dcms.Shared.Storage;

public sealed class MinioObjectStorage(IMinioClient client) : IObjectStorage
{
    /// <summary>For <see cref="OpenReadAsync"/>: a presigned URL carries its own credentials, so one
    /// pooled client serves every bucket. Recycled so a MinIO address change is picked up.</summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    public async Task PutAsync(string bucket, string key, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        var args = new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType);
        await client.PutObjectAsync(args, ct);
    }

    public async Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();
        var args = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(buffer, token));
        await client.GetObjectAsync(args, ct);
        buffer.Position = 0;
        return buffer;
    }

    public async Task GetToAsync(
        string bucket, string key, Stream destination,
        long? offset = null, long? length = null, CancellationToken ct = default)
    {
        if (length is <= 0)
        {
            return;
        }

        // ponytail: a range is served by reading from the start and discarding up to the offset,
        // not by a ranged GET. minio-dotnet 7.0.0's WithOffsetAndLength is unusable: GetObject
        // first stats the object with the SAME range, MinIO rightly answers that HEAD with 206,
        // and the SDK throws PartialContentException. Memory is one pooled buffer either way —
        // the ceiling is internal bandwidth on tail ranges of large objects. Upgrade path: a
        // presigned-URL GET carrying a Range header, or an SDK whose stat accepts the 206.
        var args = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithCallbackStream(async (stream, token) =>
            {
                if (offset is > 0)
                {
                    await PumpAsync(stream, null, offset.Value, token);
                }
                if (length is { } count)
                {
                    // Stop once the slice is written; the rest of the object is never read.
                    await PumpAsync(stream, destination, count, token);
                }
                else
                {
                    await stream.CopyToAsync(destination, token);
                }
            });

        await client.GetObjectAsync(args, ct);
    }

    /// <summary>Moves up to <paramref name="count"/> bytes to <paramref name="destination"/>, or
    /// discards them when it is null.</summary>
    private static async Task PumpAsync(Stream source, Stream? destination, long count, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (count > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
                if (read == 0)
                {
                    break;
                }
                if (destination is not null)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// One GET on a presigned URL. The SDK's own calls each begin with a bucket-exists HEAD, and
    /// GetObject stats the object first, so stat-then-get was five requests per media response
    /// (HEAD bucket, HEAD object, twice, then GET; measured 2026-10-23). Presigning is local.
    /// A range is a real ranged GET, which minio-dotnet 7's GetObject cannot do (see GetToAsync).
    /// </summary>
    public async Task<ObjectRead?> OpenReadAsync(string bucket, string key, ByteRange? range = null, CancellationToken ct = default)
    {
        var url = await client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(bucket).WithObject(key).WithExpiry(300));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (range is { } r)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.Start, r.End);
        }
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        try
        {
            response.EnsureSuccessStatusCode();
            var content = response.Content.Headers;
            var size = content.ContentRange?.Length ?? content.ContentLength
                       ?? throw new InvalidOperationException($"MinIO sent no length for {bucket}/{key}");
            return new ObjectRead(new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct)),
                size, content.ContentType?.ToString());
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The body, owning the response it came from.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public async Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            var args = new StatObjectArgs().WithBucket(bucket).WithObject(key);
            var stat = await client.StatObjectAsync(args, ct);
            return new StoredObjectInfo(stat.Size, stat.ContentType);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            var args = new StatObjectArgs().WithBucket(bucket).WithObject(key);
            await client.StatObjectAsync(args, ct);
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
    {
        var args = new RemoveObjectArgs().WithBucket(bucket).WithObject(key);
        await client.RemoveObjectAsync(args, ct);
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new ListObjectsArgs().WithBucket(bucket).WithPrefix(prefix).WithRecursive(true);
        await foreach (var item in client.ListObjectsEnumAsync(args, ct).ConfigureAwait(false))
        {
            if (item.Key is { Length: > 0 } key)
            {
                yield return key;
            }
        }
    }

    public async Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct = default)
    {
        // Batched rather than one RemoveObject per key: a published site build is
        // hundreds of small files, and a round trip each would make deleting a site
        // take minutes. 1000 is the S3 DeleteObjects limit MinIO also enforces.
        const int BatchSize = 1000;
        var deleted = 0;
        var batch = new List<string>(BatchSize);

        await foreach (var key in ListKeysAsync(bucket, prefix, ct).ConfigureAwait(false))
        {
            batch.Add(key);
            if (batch.Count == BatchSize)
            {
                await RemoveBatchAsync(bucket, batch, ct).ConfigureAwait(false);
                deleted += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await RemoveBatchAsync(bucket, batch, ct).ConfigureAwait(false);
            deleted += batch.Count;
        }
        return deleted;
    }

    public async Task<long> SizeOfPrefixAsync(string bucket, string prefix, CancellationToken ct = default)
    {
        var args = new ListObjectsArgs().WithBucket(bucket).WithPrefix(prefix).WithRecursive(true);
        long total = 0;
        await foreach (var item in client.ListObjectsEnumAsync(args, ct).ConfigureAwait(false))
        {
            total += (long)item.Size;
        }
        return total;
    }

    private Task RemoveBatchAsync(string bucket, List<string> keys, CancellationToken ct) =>
        client.RemoveObjectsAsync(
            new RemoveObjectsArgs().WithBucket(bucket).WithObjects(keys), ct);
}
