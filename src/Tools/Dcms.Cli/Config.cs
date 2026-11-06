using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dcms.Cli;

/// <summary>This machine's sign-in: the console it talks to, and the tokens identity issued.</summary>
/// <param name="ExpiresAt">When the access token expires, in Unix milliseconds (the Node CLI's format, kept).</param>
internal sealed record Credentials(string Server, string TokenEndpoint, string AccessToken, string? RefreshToken, long ExpiresAt);

internal sealed record Remote(string Name, string Url);

/// <summary>Which app a clone is: kept beside the sign-in, never in the clone (someone else's content).</summary>
internal sealed record Link(string Tenant, string Environment, string App, string RepoId, string? Plugin, string? Repo, Remote Remote);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(Dictionary<string, Link>))]
internal sealed partial class CliJson : JsonSerializerContext;

/// <summary>Where the CLI keeps what it knows, and how it writes it: private to the user.</summary>
internal sealed class Config(string dir)
{
    public string Dir { get; } = dir;

    public string CredentialsPath => Path.Combine(Dir, "credentials.json");

    public string LinksPath => Path.Combine(Dir, "links.json");

    /// <summary>
    /// The folder for this OS: <c>$DCMS_CONFIG_DIR</c> if set; else <c>%APPDATA%\dcms</c>,
    /// <c>~/Library/Application Support/dcms</c>, or <c>$XDG_CONFIG_HOME/dcms</c> (<c>~/.config/dcms</c>).
    /// What the Node CLI wrote in <c>~/.config/dcms</c> is taken over on first use.
    /// </summary>
    public static Config ForThisMachine(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (env("DCMS_CONFIG_DIR") is { Length: > 0 } explicitDir) return new Config(explicitDir);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacy = Path.Combine(env("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config"), "dcms");
        var dir = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dcms")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "dcms")
            : legacy;
        var config = new Config(dir);
        if (dir != legacy && !Directory.Exists(dir) && Directory.Exists(legacy))
        {
            foreach (var name in new[] { "credentials.json", "links.json" })
            {
                var from = Path.Combine(legacy, name);
                if (File.Exists(from)) config.WritePrivate(Path.Combine(dir, name), File.ReadAllBytes(from));
            }
        }
        return config;
    }

    public Credentials? ReadCredentials() =>
        File.Exists(CredentialsPath) ? JsonSerializer.Deserialize(File.ReadAllBytes(CredentialsPath), CliJson.Default.Credentials) : null;

    public void WriteCredentials(Credentials value) =>
        WritePrivate(CredentialsPath, JsonSerializer.SerializeToUtf8Bytes(value, CliJson.Default.Credentials));

    public void RemoveCredentials() => File.Delete(CredentialsPath);

    public Dictionary<string, Link> ReadLinks() =>
        File.Exists(LinksPath) ? JsonSerializer.Deserialize(File.ReadAllBytes(LinksPath), CliJson.Default.DictionaryStringLink) ?? [] : [];

    public void WriteLinks(Dictionary<string, Link> links) =>
        WritePrivate(LinksPath, JsonSerializer.SerializeToUtf8Bytes(links, CliJson.Default.DictionaryStringLink));

    /// <summary>
    /// Written whole or not at all, readable by the user only: the folder 0700 and the file 0600
    /// on Linux and macOS; on Windows the user's own profile folder already is.
    /// </summary>
    // ponytail: files with private modes, like gh and npm without a keyring; the OS keychain if asked.
    public void WritePrivate(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        using (var file = OperatingSystem.IsWindows()
                   ? new FileStream(temp, FileMode.Create, FileAccess.Write)
                   : new FileStream(temp, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
        {
            file.Write(bytes);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Holds this machine's sign-in for one process at a time: identity rotates refresh tokens, so
    /// two commands refreshing at once would leave one holding a token already spent.
    /// </summary>
    public async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, "credentials.lock");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                // Never deleted: unlinking a held lock file lets the next two openers lock different files.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(100, ct);
            }
        }
    }
}
