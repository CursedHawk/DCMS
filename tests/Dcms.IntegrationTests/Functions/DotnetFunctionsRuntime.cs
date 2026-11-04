using System.Diagnostics;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// What script-runner's image holds for C# functions (ADR 0030), published from this checkout as
/// its Dockerfile does: Dcms.Functions.WasmHost's wwwroot (.NET on WebAssembly and
/// dotnet-harness.js) and dcms-csc with its refs/.
/// </summary>
public sealed class DotnetFunctionsRuntime : IAsyncLifetime
{
    public string HarnessPath { get; private set; } = "";
    public string CompilerPath { get; private set; } = "";
    private string? _root;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFactAttribute.Available || !OperatingSystem.IsLinux())
        {
            return;
        }
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Dcms.sln")))
        {
            repo = repo.Parent;
        }
        _root = Directory.CreateTempSubdirectory("dcms-dotnet-fn-").FullName;
        await PublishAsync(Path.Combine(repo!.FullName, "src/Functions/Dcms.Functions.WasmHost/Dcms.Functions.WasmHost.csproj"), Path.Combine(_root, "wasmhost"));
        await PublishAsync(Path.Combine(repo.FullName, "src/Services/Dcms.ScriptRunner.CSharp/Dcms.ScriptRunner.CSharp.csproj"), Path.Combine(_root, "csc"));
        HarnessPath = Path.Combine(_root, "wasmhost", "wwwroot", "dotnet-harness.js");
        CompilerPath = Path.Combine(_root, "csc", "dcms-csc.dll");
    }

    private static async Task PublishAsync(string project, string output)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in (string[])["publish", project, "-c", "Release", "-o", output, "--nologo"])
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish {Path.GetFileName(project)} failed:\n{await stdout}\n{await stderr}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}
