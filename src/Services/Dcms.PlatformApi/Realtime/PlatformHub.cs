using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dcms.PlatformApi.Realtime;

/// <summary>
/// Push-only transport for the platform console's live updates.
///
/// <para><b>No client-to-server methods, and only one message type.</b> Everything pushed here
/// is a <c>ResourceChanged</c> carrying a tag and nothing else (see
/// <see cref="PlatformResourceTags"/>) — the console refetches over its ordinary authorised
/// REST calls. That is what makes the group a single "everyone watching the platform" rather
/// than a fan-out keyed on who may see what: a tag discloses nothing, and the refetch it
/// triggers is refused on its own merits if the caller may not read that page.</para>
///
/// <para><b>Who may connect.</b> Any operator holding at least one platform-console permission.
/// A signed-in tenant user with no global role holds none and is refused — not because a tag
/// would tell them anything, but because a console socket is not something an account with no
/// business on this console should be able to hold open.</para>
///
/// <para>Cross-replica fan-out is the Redis backplane's job, under a channel prefix of its own:
/// this Redis is shared with admin-api's and content-api's hubs, and a shared prefix
/// cross-delivers between them.</para>
/// </summary>
[Authorize]
public sealed class PlatformHub(
    IPlatformPermissionResolver permissions,
    ILogger<PlatformHub> logger) : Hub
{
    /// <summary>Every console open on this platform. There is no finer grouping to make.</summary>
    public const string Group = "platform";

    public override async Task OnConnectedAsync()
    {
        var roles = Context.User?.FindAll("role").Select(c => c.Value).Distinct().ToArray() ?? [];

        // The same short-circuit /me uses: a SuperAdmin holds everything whether or not the
        // rows exist, so a half-seeded permission table cannot lock out the one person who can
        // repair it.
        var allowed = roles.Contains("SuperAdmin", StringComparer.Ordinal)
                      || (await permissions.GetPermissionsAsync(roles, Context.ConnectionAborted)).Count > 0;

        if (!allowed)
        {
            logger.LogWarning(
                "Console hub connection refused: {Sub} holds no platform permission.",
                Context.User?.FindFirst("sub")?.Value);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        Context.Items[CountedKey] = true;
        Interlocked.Increment(ref _connected);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Only decrement for a connection that was actually counted. SignalR calls this for a
        // connection aborted above too, and decrementing for one that never incremented would
        // drift the count below zero — which would read as "nobody is watching" while somebody
        // is, and silence the sample broadcaster for the rest of the process's life.
        if (Context.Items.Remove(CountedKey)) Interlocked.Decrement(ref _connected);
        return base.OnDisconnectedAsync(exception);
    }

    private const string CountedKey = "dcms.counted";
    private static int _connected;

    /// <summary>
    /// Whether this replica is holding any console connection.
    ///
    /// <para>Read by <see cref="PlatformSampleBroadcaster"/> so a platform with nobody looking
    /// at it does no sampling work at all. Per replica, which is the right scope: a replica
    /// with a connection broadcasts to the whole group over the backplane, so one watcher
    /// anywhere is enough for everyone to be served.</para>
    /// </summary>
    public static bool AnyConnected => Volatile.Read(ref _connected) > 0;
}

/// <summary>
/// Tells every open console that a class of platform data changed.
/// </summary>
public interface IPlatformChangePublisher
{
    Task PublishAsync(string tag, string? id = null, CancellationToken ct = default);
}

public sealed class PlatformChangePublisher(
    IHubContext<PlatformHub> hub,
    ILogger<PlatformChangePublisher> logger) : IPlatformChangePublisher
{
    public async Task PublishAsync(string tag, string? id = null, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(PlatformHub.Group).SendAsync("ResourceChanged", new { tag, id }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missed refetch hint costs a stale table until the fallback poll. Whatever
            // happened has already happened and must not be undone by a socket problem.
            logger.LogDebug(ex, "Publishing platform change {Tag} failed.", tag);
        }
    }
}
