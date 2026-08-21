namespace Dcms.Shared.Audit.Propagation;

/// <summary>
/// Makes the current unit of work's <see cref="AuditScope"/> reachable from code that cannot
/// be given one.
///
/// <para>Specifically the event publisher, which is a <b>singleton</b> — it holds a NATS
/// connection, and making it scoped to get at a scoped service would open a connection per
/// request. The same problem applies to the two outbox dispatchers, which run on a timer with
/// no request anywhere in sight. An <c>AsyncLocal</c> flows down the call stack instead, so a
/// publish issued during a request finds that request's context and one issued from a
/// background loop finds nothing, which is the honest answer.</para>
///
/// <para>Registered as a singleton holding a static <c>AsyncLocal</c>: one per process, which
/// is what "ambient" means. It is read-mostly and never enumerated, so there is no
/// synchronisation to get wrong.</para>
/// </summary>
public sealed class AuditAmbient
{
    private static readonly AsyncLocal<AuditScope?> Ambient = new();

    /// <summary>The scope of the request or message being handled on this flow, if any.</summary>
    public AuditScope? Current => Ambient.Value;

    /// <summary>
    /// Makes <paramref name="scope"/> ambient until the returned handle is disposed, restoring
    /// whatever was there before. Nesting is safe and is not hypothetical: a consumer that
    /// republishes runs one inside another.
    /// </summary>
    public IDisposable Enter(AuditScope scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new Restoration(previous);
    }

    private sealed class Restoration(AuditScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
