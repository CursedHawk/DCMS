extern alias PlatformApiApp;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using DelegatedConsoleEndpoints = PlatformApiApp::Dcms.PlatformApi.Delegation.DelegatedConsoleEndpoints;
using PlatformHub = PlatformApiApp::Dcms.PlatformApi.Realtime.PlatformHub;

namespace Dcms.IntegrationTests.Console;

/// <summary>
/// The platform console's bell across tabs: reading or dismissing in one tells the operator's
/// other consoles (their own group, not every operator's), which refetch their bell.
/// </summary>
public sealed class ConsoleBellTabsTests
{
    [Fact]
    public async Task A_read_tells_the_operators_own_consoles_and_nobody_elses()
    {
        var hub = new RecordingHub();
        await DelegatedConsoleEndpoints.TellOperatorsTabsAsync(Context(hub, "op-1"), CancellationToken.None);
        hub.Sent.Should().ContainSingle().Which.Should().Be((PlatformHub.UserGroup("op-1"), "ResourceChanged"));

        // No subject, no group: told to nobody rather than to everyone.
        await DelegatedConsoleEndpoints.TellOperatorsTabsAsync(Context(hub, null), CancellationToken.None);
        hub.Sent.Should().HaveCount(1);
    }

    private static DefaultHttpContext Context(RecordingHub hub, string? subject) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(subject is null ? [] : [new Claim("sub", subject)], "test")),
        RequestServices = new ServiceCollection().AddSingleton<IHubContext<PlatformHub>>(hub).BuildServiceProvider(),
    };

    private sealed class RecordingHub : IHubContext<PlatformHub>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Group, string Method)> Sent { get; } = new();

        public IHubClients Clients => new To(this, "");
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class To(RecordingHub hub, string group) : IHubClients, IClientProxy
        {
            public IClientProxy All => this;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
            public IClientProxy Client(string connectionId) => this;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
            public IClientProxy Group(string groupName) => new To(hub, groupName);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new To(hub, groupName);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
            public IClientProxy User(string userId) => this;
            public IClientProxy Users(IReadOnlyList<string> userIds) => this;

            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                hub.Sent.Enqueue((group, method));
                return Task.CompletedTask;
            }
        }
    }
}
