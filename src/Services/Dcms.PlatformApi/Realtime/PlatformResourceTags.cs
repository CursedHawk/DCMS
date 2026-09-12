namespace Dcms.PlatformApi.Realtime;

/// <summary>
/// The vocabulary of "this class of platform data changed", pushed to open consoles so they
/// refetch instead of polling.
///
/// <para>Mirrors <c>ResourceTags</c> on the tenant plane in shape and in reasoning: a tag names
/// a class of data rather than an event or a row, the console maps it onto the react-query keys
/// that are now stale, and a tag either side does not recognise degrades to "nothing
/// refetches". That last property is what lets the console and its API be deployed
/// independently.</para>
///
/// <para><b>Two kinds of tag live here, and the difference matters.</b> The first three are
/// announcements: something inside the platform happened and said so. The last three are
/// <i>sample ticks</i> for data read out of Prometheus, Loki and the tenant plane, none of
/// which tell this API anything when they change. Those were browser-side polls until the
/// console stopped polling entirely; taking the same period server-side keeps the data as
/// fresh as it ever was while costing one timer per replica instead of one per open tab.</para>
/// </summary>
public static class PlatformResourceTags
{
    /// <summary>A tenant was created, suspended or resumed.</summary>
    public const string Tenants = "tenants";

    /// <summary>A managed certificate changed state — issued, refused, expiring, expired.</summary>
    public const string Certificates = "certificates";

    /// <summary>The console's bell has something new in it.</summary>
    public const string Notifications = "notifications";

    /// <summary>
    /// A fresh sample of the golden signals is available.
    ///
    /// <para>The three tags below are different in kind from the three above: nothing inside
    /// the platform <i>announces</i> that Prometheus scraped again or that a tenant's content
    /// count moved. They are periodic samples, and they used to be pulled by every open browser
    /// on its own timer. <see cref="PlatformSampleBroadcaster"/> now takes the sample's period
    /// server-side and pushes the tick, so the cost is one timer per replica rather than one
    /// per console, and the console's code path is the same as for a real change.</para>
    /// </summary>
    public const string Health = "health";

    /// <summary>Object-store usage and Loki's delete queue, both read from systems that do not push.</summary>
    public const string Stores = "stores";

    /// <summary>Platform-wide totals, which move on the tenant plane where this API hears nothing.</summary>
    public const string Overview = "overview";

    /// <summary>
    /// The class of data a platform notification kind implies has changed, beyond the bell
    /// itself.
    ///
    /// <para>Derived from the kind rather than asked of the producer, exactly as the tenant
    /// plane does it: admin-api raises these and does not know what any console is showing. A
    /// kind with no entry pushes only the notifications tag, which is the right answer for a
    /// fact that appears in the bell and on no page.</para>
    /// </summary>
    public static string? ForKind(string kind) => kind switch
    {
        "certificate.issued" => Certificates,
        "certificate.failed" => Certificates,
        "certificate.blocked" => Certificates,
        "certificate.expiring" => Certificates,
        "certificate.expired" => Certificates,
        _ => null,
    };
}
