using Dcms.Cli;

// A Windows update leaves the replaced file behind: it cannot be deleted while it runs.
if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } self)
{
    try
    {
        File.Delete(self + ".old");
    }
    catch (IOException)
    {
        // still running elsewhere; the next start takes it
    }
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"dcms/{Commands.Version}");
var config = Config.ForThisMachine();
var commands = new Commands(config, new Api(config, http, Console.Out), http, Console.Out, Console.Error, Console.In, Environment.CurrentDirectory);
return await commands.Build().Parse(args).InvokeAsync();
