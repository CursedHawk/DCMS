using System.IO.Pipes;
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
            if (Environment.GetEnvironmentVariable("DOCKER_HOST") is not null
                || File.Exists("/var/run/docker.sock"))
            {
                return true;
            }

            // Docker Desktop on Windows serves the engine over a named pipe and
            // leaves TCP 2375 closed unless you opt in, so without this check the
            // whole suite silently skips on a perfectly working machine.
            if (OperatingSystem.IsWindows() && NamedPipeAvailable())
            {
                return true;
            }

            using var client = new TcpClient();
            return client.ConnectAsync("localhost", 2375).Wait(500);
        }
        catch
        {
            return false;
        }
    }

    private static bool NamedPipeAvailable()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut);
            pipe.Connect(500);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
