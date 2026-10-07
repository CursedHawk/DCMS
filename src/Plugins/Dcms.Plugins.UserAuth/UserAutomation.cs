using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// What the directory offers Dynamic Apps flows (<c>automation.actions@1</c>, ADR 0022): invite
/// someone, and put them in or take them out of a group — "invite the contact when the deal
/// closes". Each needs <c>users-manage</c> of whoever publishes the flow (ADR 0021): a flow must not
/// let an app's publisher do what the console would refuse them. People are named by email, which
/// is what an app's records hold; groups by id or by name.
/// </summary>
public sealed class UserAutomationActions(IPluginContext context, IServiceProvider services, ILogger<UserDirectory> logger)
    : IAutomationActionProvider
{
    public const string Invite = "user-auth.invite";
    public const string AddToGroup = "user-auth.add-to-group";
    public const string RemoveFromGroup = "user-auth.remove-from-group";

    private static readonly JsonElement InviteSchema = JsonDocument.Parse("""
        {"type":"object","required":["email"],"properties":{
          "email":{"type":"string","format":"email"},
          "displayName":{"type":"string"},
          "groups":{"type":"array","items":{"type":"string"},"description":"Group ids or names to put them in."}}}
        """).RootElement.Clone();

    private static readonly JsonElement MembershipSchema = JsonDocument.Parse("""
        {"type":"object","required":["email","group"],"properties":{
          "email":{"type":"string","format":"email"},
          "group":{"type":"string","description":"The group's id or name."}}}
        """).RootElement.Clone();

    private UserDirectory Directory => new(context, services, logger);

    public Task<AutomationActionList> ListAsync(CancellationToken ct) => Task.FromResult(new AutomationActionList(
    [
        new AutomationActionDescriptor(Invite, 1,
            "Invite someone by email to sign in to the sites, optionally into groups. Someone who already has an account is left as they are. Output: the user.",
            OpRisk.Safe, InviteSchema, Permission: UserAuthPermissions.UsersManage),
        new AutomationActionDescriptor(AddToGroup, 1,
            "Put a site user (by email) into a group (by id or name). Output: the user.",
            OpRisk.Safe, MembershipSchema, Permission: UserAuthPermissions.UsersManage),
        new AutomationActionDescriptor(RemoveFromGroup, 1,
            "Take a site user (by email) out of a group (by id or name). Output: the user.",
            OpRisk.Safe, MembershipSchema, Permission: UserAuthPermissions.UsersManage),
    ]));

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionCall input, CancellationToken ct)
    {
        if (input.Major != 1 || input.Name is not (Invite or AddToGroup or RemoveFromGroup))
        {
            throw new ContractValidationException($"User Authentication offers no action {input.Name}@{input.Major}.");
        }
        var email = Text(input, "email") ?? throw new ContractValidationException($"{input.Name}: an email is required.");
        if (input.Name == Invite)
        {
            // Idempotent, so a retried step does not fail on the account its first try made.
            var user = await FindAsync(email, ct);
            if (user is null)
            {
                var groups = new List<Guid>();
                if (input.Input.TryGetProperty("groups", out var names) && names.ValueKind == JsonValueKind.Array)
                {
                    foreach (var name in names.EnumerateArray())
                    {
                        groups.Add(await GroupAsync(name.GetString() ?? "", input.Name, ct));
                    }
                }
                user = await Directory.InviteAsync(new UserInvite(email, Text(input, "displayName"), groups), ct);
            }
            return Result(user);
        }

        var member = await FindAsync(email, ct) ?? throw new ContractValidationException($"{input.Name}: nobody with the email {email} can sign in here.");
        var group = await GroupAsync(Text(input, "group") ?? "", input.Name, ct);
        var membership = new GroupMembership(member.Id, group);
        if (input.Name == AddToGroup)
        {
            await Directory.AddToGroupAsync(membership, ct);
        }
        else
        {
            await Directory.RemoveFromGroupAsync(membership, ct);
        }
        return Result((await FindAsync(email, ct))!);
    }

    private async Task<DirectoryUser?> FindAsync(string email, CancellationToken ct) =>
        (await Directory.ListUsersAsync(new UserSearch(email, 1, 100), ct)).Items
        .FirstOrDefault(u => string.Equals(u.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<Guid> GroupAsync(string group, string action, CancellationToken ct)
    {
        var groups = (await Directory.ListGroupsAsync(ct)).Groups;
        return (Guid.TryParse(group, out var id) ? groups.FirstOrDefault(g => g.Id == id) : null)?.Id
               ?? groups.FirstOrDefault(g => string.Equals(g.Name, group.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
               ?? throw new ContractValidationException($"{action}: there is no group '{group}'.");
    }

    private static string? Text(AutomationActionCall input, string name) =>
        input.Input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static AutomationActionResult Result(DirectoryUser user) =>
        new(JsonSerializer.SerializeToElement(user, JsonSerializerOptions.Web));
}

/// <summary>
/// Relays identity's "an account became active" (<see cref="Subjects.RealmUserActivated"/>) as this
/// plugin's <c>user.activated</c> event, in the tenant whose realm it is — so Dynamic Apps flows and
/// other plugins can act on it. A tenant without the plugin enabled has no one to tell.
/// </summary>
public sealed class RealmEventRelay(INatsJSContext jetStream, PluginHandlerRunner runner, ILogger<RealmEventRelay> logger) : BackgroundService
{
    private const string DurableName = "user-auth-realm-events";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.Tenancy,
                    new ConsumerConfig(DurableName) { FilterSubject = Subjects.RealmUserActivated, AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);
                await foreach (var msg in consumer.ConsumeAsync<RealmUserActivated>(cancellationToken: stoppingToken))
                {
                    if (msg.Data is { } activated)
                    {
                        await runner.RunAsync(activated.TenantId, UserAuthPermissions.PluginId, null,
                            (context, _) => context.PublishAsync(new UserActivated(activated.UserId, activated.Email), stoppingToken), stoppingToken);
                    }
                    await msg.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realm event relay unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
