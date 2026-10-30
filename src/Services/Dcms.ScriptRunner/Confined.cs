using System.Diagnostics;
using System.Text;

namespace Dcms.ScriptRunner;

/// <summary>
/// Starts a program the way every tenant-facing process here starts (ADR 0025): first in line for
/// the OOM killer, no core dumps, a kernel cap on writable memory, and an environment holding
/// nothing of this host's — PATH only, for choom to find prlimit.
/// </summary>
internal static class Confined
{
    public static Process Start(string program, IEnumerable<string> args, long dataLimitBytes,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo("choom")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        // choom and prlimit each exec the next, so the process is the program itself, with both applied.
        foreach (var arg in (string[])["-n", "1000", "--", "prlimit", "--core=0", $"--data={dataLimitBytes}", "--", program])
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        start.Environment.Clear();
        start.Environment["PATH"] = path ?? "/usr/bin:/bin";
        start.Environment["NO_COLOR"] = "1";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }
        return Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
    }

    /// <summary>Reads a stream to its end, keeping at most <paramref name="max"/> characters; past that, <paramref name="onOverflow"/> once.</summary>
    public static async Task<string> ReadBoundedAsync(StreamReader reader, int max, Action? onOverflow)
    {
        var text = new StringBuilder();
        var buffer = new char[8_192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (text.Length + read > max)
            {
                if (onOverflow is not null)
                {
                    onOverflow();
                    return text.ToString();
                }
                continue;
            }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    public static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
