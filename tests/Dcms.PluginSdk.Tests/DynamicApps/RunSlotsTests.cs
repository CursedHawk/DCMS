using Dcms.Plugins.DynamicApps.Automation;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>
/// The automation worker's run slots. Until 2026-10-22 it claimed four runs and waited for all four,
/// so one slow script held three slots idle: 14.2 runs/s fell to 4.1 with 10 % slow scripts.
/// </summary>
public sealed class RunSlotsTests
{
    [Fact]
    public async Task A_slow_item_holds_its_own_slot_and_no_other()
    {
        var queue = new Queue<string>(["slow", "a", "b", "c", "d", "e"]);
        var slow = new TaskCompletionSource();
        var done = new List<string>();
        using var stop = new CancellationTokenSource();

        var pool = RunSlots.RunAsync<string>(
            slots: 2,
            claim: (n, _) =>
            {
                lock (queue)
                {
                    return Task.FromResult<IReadOnlyCollection<string>>(
                        Enumerable.Range(0, Math.Min(n, queue.Count)).Select(_ => queue.Dequeue()).ToList());
                }
            },
            run: async item =>
            {
                if (item == "slow") await slow.Task;
                else await Task.Delay(5);
                lock (done) done.Add(item);
            },
            idle: TimeSpan.FromMilliseconds(10),
            onClaimFailed: _ => { },
            stop.Token);

        // Batch-and-wait would finish nothing past "a" until "slow" did.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (done) if (done.Count == 5) break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        lock (done) done.Should().BeEquivalentTo(["a", "b", "c", "d", "e"]);

        slow.SetResult();
        await stop.CancelAsync();
        await pool;
        done.Should().Contain("slow");
    }
}
