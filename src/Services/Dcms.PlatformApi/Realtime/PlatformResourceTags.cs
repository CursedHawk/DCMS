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
/// <para><b>The list is short because it is honest.</b> Only three things on this console
/// change in a way something inside the platform can announce. Object-store sizes and Loki's
/// delete queue are read from those systems, which tell us nothing when they change and are
/// perfectly well served by the polls they already have — pretending otherwise would be a
/// socket that carries no news.</para>
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
