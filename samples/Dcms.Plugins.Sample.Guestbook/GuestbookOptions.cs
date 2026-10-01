namespace Dcms.Plugins.Sample.Guestbook;

/// <summary>
/// Operator settings: per deployment, not per tenant — <c>Plugins:sample-guestbook:*</c> in the
/// host's configuration (env <c>Plugins__sample-guestbook__MaxMessageLength=1000</c>). Read once
/// in <see cref="GuestbookPlugin.ConfigureServices"/> through <c>host.SettingsFor(id)</c>.
/// </summary>
public sealed class GuestbookOptions
{
    public int MaxMessageLength { get; set; } = 500;

    /// <summary>Signatures one network address may make per guestbook per hour.</summary>
    public int SignaturesPerHour { get; set; } = 5;

    /// <summary>Words masked in every guestbook and refused in form submissions, platform-wide.</summary>
    public List<string> BlockedWords { get; set; } = [];
}
