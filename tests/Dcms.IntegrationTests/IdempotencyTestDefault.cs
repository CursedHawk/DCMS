using System.Runtime.CompilerServices;

namespace Dcms.IntegrationTests;

/// <summary>
/// admin-api and platform-api refuse a person's write that carries no <c>Idempotency-Key</c>
/// (ADR 0024). The consoles always send one; these tests' hand-built requests do not, and what
/// they test is not that. So every host in this assembly starts with the requirement off, and
/// <c>IdempotencyTests</c> turns it back on for the host that proves it.
/// </summary>
internal static class IdempotencyTestDefault
{
    [ModuleInitializer]
    internal static void TurnOffTheRequirement() =>
        Environment.SetEnvironmentVariable("Idempotency__RequireForUsers", "false");
}
