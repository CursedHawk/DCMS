using System.Runtime.CompilerServices;
using Minio;
using Minio.DataModel.Args;

namespace Dcms.Shared.Storage;

public sealed class MinioObjectStorage(IMinioClient client) : IObjectStorage
{
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

    private Task RemoveBatchAsync(string bucket, List<string> keys, CancellationToken ct) =>
        client.RemoveObjectsAsync(
            new RemoveObjectsArgs().WithBucket(bucket).WithObjects(keys), ct);
}
