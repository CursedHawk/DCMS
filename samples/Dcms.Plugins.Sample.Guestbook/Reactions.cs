using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.Sample.Guestbook.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.Sample.Guestbook;

/*
 * Everything the guestbook does that is not a request: hooks it intercepts, events it handles,
 * jobs it runs. Each is a class the manifest names; the runtime constructs it with the plugin's
 * context and DI, runs it in the right host, and keeps one failure from taking down another.
 */

/// <summary>
/// Hook on our own <c>guestbook.signing</c>: masks blocked words instead of refusing, which
/// shows the other half of what an interceptor may do — rewrite the payload and let it through.
/// </summary>
public sealed class MaskBlockedWords(GuestbookOptions options) : IPluginHookHandler<EntrySigning>
{
    public ValueTask<HookResult<EntrySigning>> HandleAsync(EntrySigning hook, IPluginContext context, CancellationToken ct)
    {
        var words = BlockedWords.For(options, context);
        return ValueTask.FromResult(HookResult.Continue(hook with
        {
            Name = BlockedWords.Mask(hook.Name, words),
            Message = BlockedWords.Mask(hook.Message, words),
        }));
    }
}

/// <summary>
/// Hook on the Forms plugin's <c>forms.submitting</c>: another plugin's operation, intercepted
/// by referencing its .Api only. Refuses a submission containing a blocked word.
/// </summary>
public sealed class RefuseSpamForms(GuestbookOptions options) : IPluginHookHandler<FormSubmitting>
{
    public ValueTask<HookResult<FormSubmitting>> HandleAsync(FormSubmitting hook, IPluginContext context, CancellationToken ct)
    {
        var words = BlockedWords.For(options, context);
        var spam = hook.Data.Values
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Any(v => words.Any(w => v.GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return ValueTask.FromResult(spam
            ? HookResult.Cancel(hook, "This message looks like spam.")
            : HookResult.Continue(hook));
    }
}

internal static class BlockedWords
{
    /// <summary>The operator's list, plus the instance's own when the hook runs for exactly one guestbook.</summary>
    public static IReadOnlyList<string> For(GuestbookOptions options, IPluginContext context)
    {
        var words = new List<string>(options.BlockedWords);
        if (context.Instance?.Config.RootElement is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("blockedWords", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            words.AddRange(list.EnumerateArray().Select(w => w.GetString()).OfType<string>());
        }
        return words.Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Mask(string text, IReadOnlyList<string> words) =>
        words.Aggregate(text, (current, word) =>
            Regex.Replace(current, Regex.Escape(word), m => new string('*', m.Length), RegexOptions.IgnoreCase));
}

/// <summary>Our own event: tells moderators when an entry waits for them (admin bell, by permission).</summary>
public sealed class NotifyModerators : IPluginEventHandler<EntrySigned>
{
    public async Task HandleAsync(EntrySigned e, IPluginContext context, CancellationToken ct)
    {
        if (!e.NeedsApproval)
        {
            return;
        }
        await context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
            Title: "A guestbook entry waits for approval",
            Body: $"{e.Name} signed the guestbook.",
            RequiredPermission: GuestbookPermissions.Moderate,
            DedupeKey: $"guestbook-entry:{e.EntryId}",
            LinkPath: $"/app/{GuestbookPlugin.Id}/overview"), ct);
    }
}

/// <summary>
/// Another plugin's event: the Blog's <c>blog.post.published</c>, handled for every blog in the
/// tenant. Keeps a plugin-wide counter document with optimistic concurrency.
/// </summary>
public sealed class CountBlogPosts : IPluginEventHandler<BlogPostPublished>
{
    public async Task HandleAsync(BlogPostPublished e, IPluginContext context, CancellationToken ct)
    {
        var storage = context.Contracts.Get<IPluginStorage>();
        var address = new DocumentAddress("stats", "blog", InstanceScoped: false);
        var current = await storage.GetAsync(address, ct);
        var count = (current?.Data.GetProperty("published").GetInt32() ?? 0) + 1;
        await storage.PutAsync(new PutDocument("stats", "blog",
            JsonSerializer.SerializeToElement(new { published = count, lastSlug = e.Slug }),
            ExpectedVersion: current?.Version ?? 0, InstanceScoped: false), ct);
    }
}

/// <summary>
/// An interval job (manifest: every day): the platform's scheduler runs it once per tenant with an
/// enabled guestbook, with no instance — so it reads every guestbook the store remembers.
/// </summary>
public sealed class DailyDigest : IPluginJobHandler
{
    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        var store = new EntryStore(context);
        long waiting = 0;
        foreach (var guestbook in await store.KnownAsync(ct))
        {
            waiting += await store.CountAsync(guestbook.InstanceId, EntryStatus.Pending, ct);
        }
        if (waiting == 0)
        {
            return;
        }
        await context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
            Title: $"{waiting} guestbook {(waiting == 1 ? "entry waits" : "entries wait")} for approval",
            Body: "Approve or reject them on the guestbook's page.",
            RequiredPermission: GuestbookPermissions.Moderate,
            DedupeKey: $"guestbook-digest:{DateTime.UtcNow:yyyy-MM-dd}",
            LinkPath: $"/app/{GuestbookPlugin.Id}/overview"), ct);
    }
}

/// <summary>
/// An on-demand job, enqueued from the admin route <c>POST /export</c> for one instance: writes a
/// CSV of every entry to the plugin's files (<c>dcms.blobs</c>, downloadable from its Files data
/// set) and emails the person who asked, when they gave an address.
/// </summary>
public sealed class ExportEntries : IPluginJobHandler
{
    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        var instance = context.Instance ?? throw new InvalidOperationException("The export runs for one guestbook.");
        var store = new EntryStore(context);
        var csv = new StringBuilder("signed_at,name,status,message\n");
        for (var page = 1; ; page++)
        {
            var (entries, total) = await store.ListAsync(instance.InstanceId, null, page, 100, ct);
            foreach (var e in entries)
            {
                csv.Append($"{e.SignedAt:O},{Csv(e.Name)},{e.Status},{Csv(e.Message)}\n");
            }
            if (entries.Count == 0 || page * 100 >= total)
            {
                break;
            }
        }

        var key = $"exports/{instance.Slug}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        await context.Contracts.Get<IPluginBlobs>().PutAsync(new BlobPut(key, "text/csv", Encoding.UTF8.GetBytes(csv.ToString())), ct);

        if (payload?.ValueKind == JsonValueKind.Object && payload.Value.TryGetProperty("email", out var email)
            && email.GetString() is { Length: > 0 } to)
        {
            await context.Contracts.Get<IPluginEmail>().SendAsync(new EmailSend(
                [to], $"Your {instance.Name} export is ready",
                $"<p>The export of <strong>{System.Net.WebUtility.HtmlEncode(instance.Name)}</strong> is ready: "
                + $"open the guestbook in the admin, Data, Files, and download <code>{System.Net.WebUtility.HtmlEncode(key)}</code>.</p>",
                DedupeKey: key), ct);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
