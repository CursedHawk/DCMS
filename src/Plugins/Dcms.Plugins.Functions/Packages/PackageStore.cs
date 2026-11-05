using Dcms.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Packages;

/// <summary>
/// Package archives for builds (ADR 0031): fetched once from the public registry by their exact
/// URL, checked against their integrity before anything else, and kept by that hash. Public
/// packages are the same bytes for every tenant, so they are shared. Nothing is extracted or run
/// here: script-runner only unpacks them, offline, into the build.
/// </summary>
public sealed class PackageStore(IHttpClientFactory http, IObjectStorage storage, ILogger<PackageStore> logger)
{
    public const string HttpClientName = "functions-packages";
    public const string Bucket = "dcms-packages";
    public const int MaxPackageBytes = 25 * 1024 * 1024;

    /// <summary>The archive's bytes, which are what <paramref name="integrity"/> says, or an exception saying why not.</summary>
    public async Task<byte[]> GetAsync(string url, string integrity, CancellationToken ct)
    {
        var key = Integrity.Key(integrity);
        try
        {
            if (await storage.ExistsAsync(Bucket, key, ct))
            {
                await using var cached = await storage.GetAsync(Bucket, key, ct);
                var bytes = await ReadBoundedAsync(cached, ct);
                if (Integrity.Matches(integrity, bytes)) return bytes;
                logger.LogWarning("Cached package {Key} does not match its integrity; fetching it again.", key);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The cache is a cache: without it a build fetches from the registry.
            logger.LogWarning(e, "Could not read cached package {Key}.", key);
        }

        using var response = await http.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PackageException($"{url} answered {(int)response.StatusCode}.");
        }
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var fetched = await ReadBoundedAsync(body, ct);
        if (!Integrity.Matches(integrity, fetched))
        {
            throw new PackageException($"{url} is not what the lockfile's integrity says: refused.");
        }
        try
        {
            using var copy = new MemoryStream(fetched, writable: false);
            await storage.PutAsync(Bucket, key, copy, fetched.Length, "application/octet-stream", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not cache package {Key}.", key);
        }
        return fetched;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxPackageBytes)
            {
                throw new PackageException($"A package is at most {MaxPackageBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>A package that cannot be installed, and why: a build problem, not a server error.</summary>
public sealed class PackageException(string message) : Exception(message);
