namespace Dcms.PluginSdk.Abstractions.Contracts;

/// <summary>
/// The caller's input was wrong. The contract dispatcher answers 400 with the message, so it
/// must say what to fix and never carry anything the caller should not see.
/// </summary>
public sealed class ContractValidationException(string message) : Exception(message);

/// <summary>The write lost a race or named a stale version. The dispatcher answers 409.</summary>
public sealed class ContractConflictException(string message) : Exception(message);

/// <summary>A quota or rate limit refused the call. The dispatcher answers 429.</summary>
public sealed class ContractLimitException(string message) : Exception(message);
