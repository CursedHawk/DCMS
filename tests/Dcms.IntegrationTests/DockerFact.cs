using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Dcms.IntegrationTests;

/// <summary>
/// Integration tests need a Docker daemon for Testcontainers. This fact
/// self-skips when Docker is unavailable so the suite stays green on machines
/// without it (CI runs with Docker and exercises everything).
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerAvailable = new(Probe);

    public DockerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!DockerAvailable.Value)
        {
            Skip = "Docker daemon not available on this machine.";
        }
    }

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("localhost", 2375).Wait(500)
                   || File.Exists("/var/run/docker.sock")
                   || Environment.GetEnvironmentVariable("DOCKER_HOST") is not null;
        }
        catch
        {
            return false;
        }
    }
}
