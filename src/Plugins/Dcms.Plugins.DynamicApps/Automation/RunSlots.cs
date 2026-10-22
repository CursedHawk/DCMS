namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// A fixed number of slots, each refilled the moment its work ends: claim as many items as slots
/// are free, run them, and claim again as soon as any finishes. When a claim comes back short
/// nothing else is due, so the loop rests for <c>idle</c> before asking again.
/// </summary>
public static class RunSlots
{
    /// <param name="claim">Claims up to the given number of items; fewer means nothing else is waiting.</param>
    /// <param name="run">Runs one item; it must not throw (a fault is ignored, and its slot freed).</param>
    /// <param name="onClaimFailed">A claim threw; the loop rests and tries again.</param>
    public static async Task RunAsync<T>(
        int slots,
        Func<int, CancellationToken, Task<IReadOnlyCollection<T>>> claim,
        Func<T, Task> run,
        TimeSpan idle,
        Action<Exception> onClaimFailed,
        CancellationToken ct)
    {
        var running = new List<Task>(slots);
        while (!ct.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            if (running.Count < slots)
            {
                try
                {
                    running.AddRange((await claim(slots - running.Count, ct)).Select(run));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    onClaimFailed(ex);
                }
            }
            try
            {
                await (running.Count == slots ? Task.WhenAny(running) : Task.Delay(idle, ct)).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
