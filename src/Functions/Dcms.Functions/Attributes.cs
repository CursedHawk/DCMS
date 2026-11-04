namespace Dcms.Functions;

// A C# function declares what TypeScript's defineFunction({...}) does, as attributes. The build
// reads them from the compiled code (their arguments are constants), never by running it, and
// turns them into the same definition a TypeScript function's describe pass produces. The host
// validates that definition the same way for both languages.

/// <summary>Makes a <see cref="Function{TIn, TOut}"/> a function of this app, called <paramref name="name"/>.</summary>
/// <param name="name">Lowercase letters, digits and dashes, as in <c>order-total</c>: its HTTP path, <c>/fn/{name}</c>, and what others invoke it by.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FunctionAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>What it does, shown on the app's screens. At most 500 characters.</summary>
    public string? Description { get; set; }
}

/// <summary>Who may call it over HTTP, at GET or POST <c>/api/{app}/fn/{name}</c>. Without it, it is not callable over HTTP.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HttpAttribute(params Access[] access) : Attribute
{
    public Access[] Access { get; } = access;
}

public enum Access
{
    /// <summary>Anyone.</summary>
    Public,
    /// <summary>Anyone signed in to the site.</summary>
    User,
    /// <summary>Site users a role grants "invoke" on this function (Users → Roles).</summary>
    Role,
    /// <summary>Callers presenting one of this app's keys in X-Api-Key.</summary>
    ApiKey,
}

public enum RowEvent { Created, Updated, Deleted }

public enum RelationEvent { Created, Deleted }

/// <summary>Runs when a Dynamic Apps record changes. The input is the event: the record, and for an update what changed.</summary>
/// <param name="table">"app.table": the app's address, then the table's API name.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnRowAttribute(string table, RowEvent @event) : Attribute
{
    public string Table { get; } = table;
    public RowEvent Event { get; } = @event;

    /// <summary>For <see cref="RowEvent.Updated"/>: only when one of these fields changed.</summary>
    public string[]? Fields { get; set; }
}

/// <param name="relationship">"app.relationship".</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnRelationAttribute(string relationship, RelationEvent @event) : Attribute
{
    public string Relationship { get; } = relationship;
    public RelationEvent Event { get; } = @event;
}

/// <summary>Runs when a new configuration of a Dynamic Apps app goes live (input: number, hash, previous).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnAppPublishedAttribute(string app) : Attribute
{
    public string App { get; } = app;
}

/// <summary>A message another function emitted, or one posted to the app's <c>/fn/_messages/{topic}</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnMessageAttribute(string topic) : Attribute
{
    public string Topic { get; } = topic;
}

/// <summary>A cron expression (5 fields, UTC), at most every 5 minutes.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnScheduleAttribute(string cron) : Attribute
{
    public string Cron { get; } = cron;
}

/// <summary>An interval such as "15m", "1h" or "1d", between 5 minutes and 7 days.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnEveryAttribute(string interval) : Attribute
{
    public string Interval { get; } = interval;
}

/// <summary>A platform event: one of <see cref="PlatformEvents"/>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OnEventAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public static class PlatformEvents
{
    public const string FormSubmitted = "form.submitted";
    public const string VisitorRegistered = "visitor.registered";
    public const string UserInvited = "user.invited";
    public const string UserActivated = "user.activated";
}

// ---------------------------------------------------------------- uses: everything it may reach

/// <summary>A Dynamic Apps table it uses, and what it may do with its records: "read", "create", "update", "delete".</summary>
/// <param name="table">"app.table".</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesTableAttribute(string table, params string[] verbs) : Attribute
{
    public string Table { get; } = table;
    public string[] Verbs { get; } = verbs;
}

/// <summary>Other apps' contracts it calls, as "name@major".</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesContractsAttribute(params string[] contracts) : Attribute
{
    public string[] Contracts { get; } = contracts;
}

/// <summary>Host names outbound HTTP may go to, e.g. "api.stripe.com" or "*.example.com".</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesEgressAttribute(params string[] hosts) : Attribute
{
    public string[] Hosts { get; } = hosts;
}

/// <summary>Names of this app's secrets it reads.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesSecretsAttribute(params string[] names) : Attribute
{
    public string[] Names { get; } = names;
}

/// <summary>Message topics it emits.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EmitsAttribute(params string[] topics) : Attribute
{
    public string[] Topics { get; } = topics;
}

/// <summary>Sends mail through the platform's queue, in the workspace's name (<c>ctx.Email.Send</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UsesEmailAttribute : Attribute;

/// <summary>Reads the workspace's published content (<c>ctx.Content</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UsesContentAttribute : Attribute;

/// <summary>Raises in-app notifications for members holding one of these permissions.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesNotificationsAttribute(params string[] audiences) : Attribute
{
    public string[] Audiences { get; } = audiences;
}

/// <summary>Apps of other environments, as "environment/app", through the links their managers set up.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesLinksAttribute(params string[] links) : Attribute
{
    public string[] Links { get; } = links;
}

/// <summary>Limits of its own, below the platform's.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LimitsAttribute : Attribute
{
    /// <summary>Wall time, at most the trigger's own (30 s over HTTP, 5 min otherwise). 0: the default.</summary>
    public int TimeoutMs { get; set; }

    /// <summary>32–256. 0: the default.</summary>
    public int HeapMb { get; set; }
}
