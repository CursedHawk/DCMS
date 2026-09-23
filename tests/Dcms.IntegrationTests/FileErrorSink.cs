using Serilog.Core;
using Serilog.Events;

namespace Dcms.IntegrationTests;

/// <summary>
/// Appends the host's error-level log events, with exceptions, to the file named by
/// <c>DCMS_TEST_LOG</c>. A 500 from a <c>WebApplicationFactory</c> host says nothing about why,
/// and the host logs through Serilog to its own console, which the test runner does not show.
/// Registered as an <see cref="ILogEventSink"/> because the service defaults build Serilog with
/// <c>ReadFrom.Services</c>. Inert when the variable is unset.
/// </summary>
public sealed class FileErrorSink(string path) : ILogEventSink
{
    private static readonly Lock Gate = new();

    public static string? PathFromEnvironment => Environment.GetEnvironmentVariable("DCMS_TEST_LOG");

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }
        lock (Gate)
        {
            File.AppendAllText(path, $"[{logEvent.Level}] {logEvent.RenderMessage()}\n{logEvent.Exception}\n\n");
        }
    }
}
