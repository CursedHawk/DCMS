using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// A source scan, guarding the mistake that put three identical "your site is live" rows in
/// one admin's bell for a site published once.
///
/// <para>Every notification consumer originally keyed its <c>DedupeKey</c> on the source
/// event's id, which reads as obviously right and is not. An event id identifies a
/// <i>publish</i>, not the fact being published. site-builder's JetStream ack deadline was 30
/// seconds against builds that take three minutes, so a healthy build was redelivered and ran
/// three times — and each run minted a fresh <c>Guid.NewGuid()</c> event id for its
/// <c>site.published</c>. Three distinct dedupe keys, three notifications, and the unique index
/// meant to prevent exactly this never saw a collision.</para>
///
/// <para>The heartbeat in <c>AckHeartbeat</c> stops the rebuilds, but that is one producer.
/// The durable guarantee is that the key names the thing the notification is <b>about</b> — the
/// build, the asset, the item and the moment — so any producer announcing one fact twice still
/// yields one row. This test keeps the tempting version from coming back.</para>
///
/// <para>It is deliberately coarse: it does not judge whether a key is well chosen, only that
/// it is not the event id. Choosing well is a review question; this is the part that gets
/// skipped.</para>
/// </summary>
public sealed partial class NotificationDedupeKeyTests
{
    [Fact]
    public void No_notification_consumer_dedupes_on_the_event_id()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ConsumerRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in EventIdDedupe().Matches(source))
            {
                var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {match.Value.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "a DedupeKey must identify the FACT, not the publish. Keying on the event id "
            + "deduplicates redelivery of one message and nothing else — a producer that "
            + "announces the same fact twice mints a new event id and gets a second "
            + "notification, which is how one site publish produced three. Key on the build, "
            + "the asset, or the resource plus its moment instead. Found:\n{0}",
            string.Join("\n", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>Walks up from the test binary to the repository, which has no fixed path in CI.</summary>
    private static string ConsumerRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return Path.Combine(directory!.FullName, "src", "Services", "Dcms.AdminApi", "Notifications");
    }

    /// <summary>
    /// `DedupeKey: evt.EventId...` in any spelling — with or without a ToString, and whatever the
    /// event variable is called.
    /// </summary>
    [GeneratedRegex(@"DedupeKey\s*[:=]\s*[A-Za-z_][A-Za-z0-9_]*\.EventId\b", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdDedupe();
}
