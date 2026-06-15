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
}
