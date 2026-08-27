using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Dcms.Shared.Audit.Redaction;

/// <summary>What may be recorded about one property.</summary>
public enum FieldDisposition
{
    /// <summary>Before and after are recorded verbatim.</summary>
    Record,

    /// <summary>The field is named as changed; neither value is recorded.</summary>
    Redact,

    /// <summary>The field is left out of the diff altogether.</summary>
    Omit,
}

/// <summary>
/// Decides what a diff is allowed to say.
///
/// <para><b>Default-deny, by entity type.</b> A type nobody has opted in is recorded as
/// changed field <i>names</i> with no values. That is the right way round: a type added
/// tomorrow — a plugin's table, a new column holding something nobody thought of as a secret
/// — leaks nothing until somebody has looked at it and said it is safe. The opposite default
/// fails silently and in the one direction that matters.</para>
///
/// <para>Inside an opted-in type there are two further nets: an explicit deny list registered
/// with the type, and a name heuristic (<c>password</c>, <c>secret</c>, <c>token</c>,
/// <c>hash</c>, <c>apikey</c>, <c>ciphertext</c>, <c>credential</c>, <c>private</c>). The
/// heuristic exists because the deny list is maintained by hand and hands forget; it costs a
/// redacted diff on a false positive, which is the cheap direction to be wrong in.</para>
///
/// <para><see cref="Version"/> is stamped onto every record as <c>RedactionVersion</c>, so a
/// reader years later can tell whether a blank field meant "unchanged" under this policy or
/// "withheld" under a later one.</para>
/// </summary>
public sealed class AuditRedactor
{
    /// <summary>
    /// Bumped whenever the policy changes what it would emit for the same input. Never
    /// reused: old records keep the version that produced them.
    /// </summary>
    public const int Version = 1;

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "secret", "token", "hash", "apikey", "api_key",
        "ciphertext", "credential", "private", "salt", "signature",
    ];

    /// <summary>
    /// True when a name looks like it holds a secret, by the same heuristic that withholds an
    /// audit field's value.
    ///
    /// <para>Public so that telemetry — span attributes and log properties — can apply the
    /// identical test. Copying the fragment list into a second place would let the two drift,
    /// and the direction they would drift is that a fragment added here for a newly discovered
    /// leak keeps appearing in traces.</para>
    /// </summary>
    public static bool LooksSensitiveName(string name) => LooksSensitive(name);

    private readonly ConcurrentDictionary<Type, TypePolicy> _policies = new();

    /// <summary>
    /// Opts a type into value capture. Call from a composition root; safe to call twice with
    /// the same type, which makes it usable from both a library default and a plugin.
    /// </summary>
    /// <param name="resourceType">Vocabulary shared with <c>.WithAudit(..., resourceType)</c>.</param>
    /// <param name="labelProperty">Property whose value is a human-readable name for the row.</param>
    /// <param name="deny">Properties whose values must never appear, beyond the heuristic.</param>
    /// <param name="omit">Properties to leave out of the diff entirely.</param>
    public AuditRedactor Allow(
        Type type,
        string resourceType,
        string? labelProperty = null,
        IEnumerable<string>? deny = null,
        IEnumerable<string>? omit = null)
    {
        _policies[type] = new TypePolicy(
            resourceType,
            labelProperty,
            new HashSet<string>(deny ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(omit ?? [], StringComparer.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>The resource type to stamp on records about this entity, if it is opted in.</summary>
    public string? ResourceTypeFor(Type type) => Policy(type)?.ResourceType;

    public string? LabelPropertyFor(Type type) => Policy(type)?.LabelProperty;

    /// <summary>True when values may be recorded for this type at all.</summary>
    public bool IsAllowed(Type type) => Policy(type) is not null;

    public FieldDisposition DispositionFor(Type type, string propertyName)
    {
        var policy = Policy(type);
        if (policy is null)
        {
            // Default-deny: the field is named, its values are not.
            return IsIgnoredByName(type, propertyName) ? FieldDisposition.Omit : FieldDisposition.Redact;
        }

        if (policy.Omit.Contains(propertyName) || HasAttribute<AuditIgnoreAttribute>(type, propertyName))
        {
            return FieldDisposition.Omit;
        }

        if (policy.Deny.Contains(propertyName)
            || HasAttribute<AuditSensitiveAttribute>(type, propertyName)
            || LooksSensitive(propertyName))
        {
            return FieldDisposition.Redact;
        }

        return FieldDisposition.Record;
    }

    /// <summary>
    /// Produces the diff entry for one property, applying the disposition and normalising the
    /// values into something JSON can carry without surprises.
    /// </summary>
    public AuditFieldChange? Describe(Type type, string propertyName, object? before, object? after)
    {
        switch (DispositionFor(type, propertyName))
        {
            case FieldDisposition.Omit:
                return null;

            case FieldDisposition.Redact:
                return new AuditFieldChange(propertyName, null, null, Redacted: true);

            default:
                return new AuditFieldChange(propertyName, Normalize(before), Normalize(after), Redacted: false);
        }
    }

    /// <summary>
    /// Caps what a single value can contribute. A 2 MB HTML body in a before-value would make
    /// the record unreadable, and would push the row past anything a reader can render — the
    /// fact that the field changed is the part that matters.
    /// </summary>
    public const int MaxValueLength = 512;

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        string s => Truncate(s),
        bool or int or long or short or byte or double or float or decimal => value,
        Guid g => g.ToString(),
        DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        byte[] b => $"<{b.Length} bytes>",
        _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string Truncate(string value) =>
        value.Length <= MaxValueLength
            ? value
            : string.Concat(value.AsSpan(0, MaxValueLength), $"… (+{value.Length - MaxValueLength} chars)");

    private TypePolicy? Policy(Type type)
    {
        if (_policies.TryGetValue(type, out var policy))
        {
            return policy;
        }

        // A type can opt itself in, which is how a plugin's entity gets covered without the
        // plugin having to reach a composition root it does not own.
        var attribute = type.GetCustomAttribute<AuditedAttribute>();
        if (attribute is null)
        {
            return null;
        }

        return _policies.GetOrAdd(type, new TypePolicy(attribute.ResourceType, attribute.LabelProperty, [], []));
    }

    private static bool IsIgnoredByName(Type type, string propertyName) =>
        HasAttribute<AuditIgnoreAttribute>(type, propertyName);

    private static bool HasAttribute<T>(Type type, string propertyName) where T : Attribute =>
        type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetCustomAttribute<T>() is not null;

    private static bool LooksSensitive(string propertyName)
    {
        foreach (var fragment in SensitiveNameFragments)
        {
            if (propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private sealed record TypePolicy(
        string ResourceType,
        string? LabelProperty,
        HashSet<string> Deny,
        HashSet<string> Omit);
}
