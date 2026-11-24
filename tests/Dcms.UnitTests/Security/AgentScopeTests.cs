using System.Security.Claims;
using AwesomeAssertions;
using Dcms.Shared.Audit;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Agents;
using Microsoft.AspNetCore.Http;

namespace Dcms.UnitTests.Security;

/// <summary>What a desktop agent's token may reach (ADR 0032), decided from its claims and the route alone.</summary>
public sealed class AgentScopeTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly CliOperationMetadata Read = new("sites.list", "Lists sites.", CliRisk.Read);
    private static readonly CliOperationMetadata Write = new("sites.create", "Creates a site.", CliRisk.Safe);

    private static ClaimsPrincipal Agent(bool readOnly = false, params string[] environments)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(AgentClaims.Grant, Guid.NewGuid().ToString()),
            new(AgentClaims.Name, "Claude Code on laptop"),
            new(AgentClaims.TenantId, Tenant.ToString()),
            new(AgentClaims.ReadOnly, readOnly ? "true" : "false"),
        };
        claims.AddRange(environments.Select(e => new Claim(AgentClaims.Environment, e)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AgentToken"));
    }

    [Fact]
    public void An_agent_reaches_only_routes_offered_to_agents()
    {
        AgentScope.Refusal(null, Agent(), Tenant, "production", "GET").Should().Contain("not offered");
        AgentScope.Refusal(Read with { HumanOnly = true }, Agent(), Tenant, "production", "GET").Should().Contain("Only a person");
        AgentScope.Refusal(Read, Agent(), Tenant, "production", "GET").Should().BeNull();
    }

    [Fact]
    public void An_agent_stays_in_its_workspace_and_environments()
    {
        AgentScope.Refusal(Read, Agent(), Guid.NewGuid(), "production", "GET").Should().Contain("another workspace");
        AgentScope.Refusal(Read, Agent(), null, "production", "GET").Should().Contain("another workspace");
        AgentScope.Refusal(Read, Agent(false, "staging"), Tenant, "production", "GET").Should().Contain("production environment");
        AgentScope.Refusal(Read, Agent(false, "staging"), Tenant, "staging", "GET").Should().BeNull();
        AgentScope.Refusal(Read, Agent(), Tenant, "anything", "GET").Should().BeNull("no list is every environment the person reaches");
    }

    [Fact]
    public void An_agent_limited_to_some_environments_never_reaches_beyond_them()
    {
        var acrossEnvironments = Write with { CrossEnvironment = true };
        AgentScope.Refusal(acrossEnvironments, Agent(false, "staging"), Tenant, "staging", "DELETE").Should().Contain("beyond one environment");
        AgentScope.Refusal(acrossEnvironments, Agent(), Tenant, "staging", "DELETE").Should().BeNull("a token for every environment may manage them");
    }

    [Fact]
    public void A_read_only_agent_only_reads()
    {
        AgentScope.Refusal(Write, Agent(readOnly: true), Tenant, "production", "POST").Should().Contain("only read");
        AgentScope.Refusal(Write, Agent(readOnly: false), Tenant, "production", "POST").Should().BeNull();
        AgentScope.Refusal(Read, Agent(readOnly: true), Tenant, "production", "POST").Should().BeNull("a read that happens to be a POST");
        AgentScope.Refusal(Write with { Risk = CliRisk.PerOperation }, Agent(readOnly: true), Tenant, "production", "POST")
            .Should().BeNull("the contracts route refuses per operation");
    }

    [Fact]
    public void Agent_tokens_are_told_from_jwts_by_their_prefix()
    {
        AgentClaims.Carries("Bearer dcms_agt_abc").Should().BeTrue();
        AgentClaims.Carries("Bearer eyJhbGciOi.x.y").Should().BeFalse();
        AgentClaims.Carries(null).Should().BeFalse();
        Agent().IsAgent().Should().BeTrue();
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "Bearer")).IsAgent().Should().BeFalse();
    }

    [Fact]
    public void The_audit_names_what_a_person_acted_through()
    {
        static HttpCurrentActor ActorOf(ClaimsPrincipal user) => new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        ActorOf(Agent()).Key.Should().Be("agent:Claude Code on laptop");
        ActorOf(Agent()).Kind.Should().Be(Dcms.Shared.Kernel.Abstractions.ActorKind.User);
        var cli = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("client_id", "dcms-cli")], "Bearer"));
        ActorOf(cli).Key.Should().Be("dcms-cli");
        var console = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("client_id", "dcms-admin-spa")], "Bearer"));
        ActorOf(console).Key.Should().BeNull();
        var service = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "dcms-site-builder")], "Bearer"));
        ActorOf(service).Key.Should().Be("dcms-site-builder");
    }
}
