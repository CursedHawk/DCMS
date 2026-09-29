namespace Dcms.PluginSdk.Abstractions.Contracts;

/// <summary>
/// How much damage an operation can do. The same three values the admin agent's mode table
/// uses (<c>apps/admin/src/features/agent/modes.ts</c>), so an operation maps onto an agent tool
/// without a translation table.
/// </summary>
public enum OpRisk
{
    /// <summary>No side effects.</summary>
    Read,

    /// <summary>A write that is cheap to undo: a draft, a profile field, a queued email.</summary>
    Safe,

    /// <summary>A write that is public, destructive or hard to undo.</summary>
    Dangerous,
}

/// <summary>
/// Which callers outside the plugin runtime may reach an operation. Every operation is always
/// callable plugin-to-plugin by a plugin that declares the contract in its <c>Consumes</c>;
/// these flags add the other planes.
/// </summary>
[Flags]
public enum OpExposure
{
    /// <summary>Plugin-to-plugin only.</summary>
    Internal = 0,

    /// <summary>Public site plane (content-api): anonymous callers and signed-in visitors.</summary>
    Site = 1,

    /// <summary>Admin plane (admin-api): tenant members, gated by <see cref="OperationAttribute.Permission"/>.</summary>
    Admin = 2,

    /// <summary>Offered to AI agents as a tool (admin assistant; the site chatbot for read ops).</summary>
    Ai = 4,
}

/// <summary>
/// Marks an interface as a versioned plugin contract. The id is <c>{name}@{major}</c>. A minor
/// change must be additive; anything breaking is a new major, and a provider may ship both.
/// Names starting with <c>dcms.</c> are reserved for platform contracts.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class DcmsContractAttribute(string name, int major) : Attribute
{
    public string Name { get; } = name;
    public int Major { get; } = major;
    public string Id => ContractIds.Format(Name, Major);

    /// <summary>What the contract is for — shown to admins and to AI consumers.</summary>
    public string? Description { get; set; }

    /// <summary>Event records (each marked <see cref="ContractEventAttribute"/>) this contract's provider publishes.</summary>
    public Type[] Events { get; set; } = [];

    /// <summary>Hook records (each marked <see cref="ContractHookAttribute"/>) this contract's provider runs.</summary>
    public Type[] Hooks { get; set; } = [];
}

/// <summary>
/// Declares one contract operation. <paramref name="risk"/> is required so a write can never
/// default to read. Methods take one input record (or none) plus a CancellationToken and return
/// <c>Task</c> or <c>Task&lt;TOut&gt;</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OperationAttribute(OpRisk risk) : Attribute
{
    public OpRisk Risk { get; } = risk;

    /// <summary>Permission key an external caller (admin, AI, site) must hold. Null = none.</summary>
    public string? Permission { get; set; }

    public OpExposure Expose { get; set; } = OpExposure.Internal;

    /// <summary>
    /// The result carries text written by people with no access to the tenant (a form
    /// submission, a visitor's display name). AI surfaces wrap it as untrusted.
    /// </summary>
    public bool ReturnsExternalText { get; set; }

    public string? Description { get; set; }
}

/// <summary>Names an event record published under a contract, e.g. <c>visitor.registered</c>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContractEventAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public static class ContractIds
{
    public const string PlatformPrefix = "dcms.";

    public static string Format(string name, int major) => $"{name}@{major}";

    public static bool IsPlatform(string contractId) => contractId.StartsWith(PlatformPrefix, StringComparison.Ordinal);

    /// <summary>Dotted kebab-case name, '@', positive major: <c>visitors.profiles@1</c>.</summary>
    public static bool IsValid(string contractId)
    {
        var at = contractId.LastIndexOf('@');
        if (at <= 0 || !int.TryParse(contractId.AsSpan(at + 1), out var major) || major < 1)
        {
            return false;
        }
        var name = contractId[..at];
        return name.Split('.').All(part =>
            part.Length > 0
            && part.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            && part[0] != '-' && part[^1] != '-');
    }

    public static string Of<TContract>() => Of(typeof(TContract));

    public static string Of(Type contract) =>
        contract.GetCustomAttributes(typeof(DcmsContractAttribute), false).FirstOrDefault() is DcmsContractAttribute a
            ? a.Id
            : throw new InvalidOperationException($"'{contract.FullName}' is not marked [DcmsContract].");

    public static string HookName(Type hookType) =>
        hookType.GetCustomAttributes(typeof(ContractHookAttribute), false).FirstOrDefault() is ContractHookAttribute a
            ? a.Name
            : throw new InvalidOperationException($"'{hookType.FullName}' is not marked [ContractHook].");

    public static string EventName(Type eventType) =>
        eventType.GetCustomAttributes(typeof(ContractEventAttribute), false).FirstOrDefault() is ContractEventAttribute a
            ? a.Name
            : throw new InvalidOperationException($"'{eventType.FullName}' is not marked [ContractEvent].");
}
