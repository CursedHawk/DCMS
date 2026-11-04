using DotNet.Testcontainers.Builders;

namespace Dcms.IntegrationTests.Functions;

/// <summary>The Deno binary, copied out of the official image of the version script-runner's Dockerfile pins.</summary>
public sealed class DenoBinary : IAsyncLifetime
{
    // Keep in step with src/Services/Dcms.ScriptRunner/Dockerfile (denoland/deno:bin-<same version>).
    private const string Image = "denoland/deno:distroless-2.5.4";

    public string Path { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        // The binary is Linux's, as script-runner's image is.
        if (!DockerFactAttribute.Available || !OperatingSystem.IsLinux())
        {
            return;
        }
        await using var container = new ContainerBuilder(Image)
            .WithCommand("eval", "await new Promise(() => {})")
            .Build();
        await container.StartAsync();
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deno-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(Path, await container.ReadFileAsync("/bin/deno"));
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    public ValueTask DisposeAsync()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
        return ValueTask.CompletedTask;
    }
}
