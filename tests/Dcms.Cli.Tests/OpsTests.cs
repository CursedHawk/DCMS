using System.Text.Json.Nodes;
using AwesomeAssertions;
using Dcms.Cli;
using Xunit;

namespace Dcms.Cli.Tests;

/// <summary>How `dcms op call` turns a catalog entry and the given values into a request.</summary>
public sealed class OpsTests
{
    private static JsonObject Route(string method, string path, JsonArray? files = null) =>
        new() { ["kind"] = "route", ["method"] = method, ["path"] = path, ["files"] = files };

    [Fact]
    public void Path_parameters_fill_the_route_constraints_and_catch_alls_included()
    {
        var (verb, url, content) = Commands.Request(Route("POST", "/api/admin/sites/{id:guid}/git/commit"),
            new() { ["id"] = "0b0e7c2e-1111-4222-8333-944445555666" }, [], """{"message":"m"}""", [], [], null);
        verb.Method.Should().Be("POST");
        url.Should().Be("/api/admin/sites/0b0e7c2e-1111-4222-8333-944445555666/git/commit");
        content.Should().NotBeNull();

        Commands.Request(Route("DELETE", "/api/admin/sites/{id:guid}/publish-branches/{**branch}"),
            new() { ["id"] = "s", ["branch"] = "feature/a b" }, [], null, [], [], null).Url
            .Should().Be("/api/admin/sites/s/publish-branches/feature/a%20b");
        Commands.Request(Route("GET", "/api/admin/media"), [], new() { ["folderId"] = "f 1" }, null, [], [], null).Url
            .Should().Be("/api/admin/media?folderId=f%201");
    }

    [Fact]
    public void A_missing_path_parameter_is_named()
    {
        var call = () => Commands.Request(Route("GET", "/api/admin/sites/{id:guid}"), [], [], null, [], [], null);
        call.Should().Throw<CliException>().WithMessage("*--path id=*");
    }

    [Fact]
    public void An_agent_not_limited_to_environments_chooses_one_on_every_tool()
    {
        static AgentLink Agent(params string[] envs) => new("id", "a", "https://x", "t", "w", envs, false, "claude-code", null, DateTimeOffset.MaxValue);
        Commands.EnvironmentProperty(Agent())!["enum"].Should().BeNull("any environment, by name");
        Commands.EnvironmentProperty(Agent("staging")).Should().BeNull("its one environment needs no choosing");
        Commands.EnvironmentProperty(Agent("staging", "development"))!["enum"]!.AsArray().Select(e => (string)e!).Should().Equal("staging", "development");
    }

    [Fact]
    public void Search_matches_words_in_any_order_best_first()
    {
        (string, string, string)[] entries =
        [
            ("sites.publish", "Dangerous", "Publishes a site: merges the branch into release."),
            ("forms.list", "Read", "The workspace's forms."),
            ("plugins.forms.submissions@1.List", "Read", "Newest-first page of submissions. [apps: form-1]"),
        ];
        Commands.Search(entries, "form submissions").Select(e => e.Item1)
            .Should().Equal(["plugins.forms.submissions@1.List", "forms.list"], "both words beat one");
        Commands.Search(entries, "publish the site").Select(e => e.Item1).Should().Equal("sites.publish");
        Commands.Search(entries, "blog").Should().BeEmpty();
        Commands.Search(entries, null).Should().HaveCount(3);
    }

    [Fact]
    public void A_path_parameter_the_route_does_not_have_is_refused()
    {
        var none = () => Commands.Request(Route("POST", "/api/admin/media/delete"), new() { ["id"] = "x" }, [], "{}", [], [], null);
        none.Should().Throw<CliException>().WithMessage("*no path parameter id*describe*");
        var other = () => Commands.Request(Route("GET", "/api/admin/sites/{id:guid}"), new() { ["id"] = "1", ["site"] = "x" }, [], null, [], [], null);
        other.Should().Throw<CliException>().WithMessage("*no path parameter site; it takes id.");
    }

    [Fact]
    public void A_contract_operation_posts_to_the_contracts_proxy_on_its_plane()
    {
        var op = new JsonObject { ["kind"] = "contract", ["contract"] = "users.directory@1", ["name"] = "ListUsers", ["plane"] = "ai" };
        var (verb, url, _) = Commands.Request(op, [], [], null, [], [], "crm");
        verb.Method.Should().Be("POST");
        url.Should().Be("/api/admin/contracts/users.directory%401/ListUsers?plane=ai&instance=crm");
    }

    [Fact]
    public void A_file_operation_sends_multipart_with_the_file_and_form_fields()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "hello");
            var (_, _, content) = Commands.Request(Route("POST", "/api/admin/media", new JsonArray("file")), [], [], null, [file], new() { ["folderId"] = "x" }, null);
            content.Should().BeOfType<MultipartFormDataContent>();
            var missing = () => Commands.Request(Route("POST", "/api/admin/media", new JsonArray("file")), [], [], null, [], [], null);
            missing.Should().Throw<CliException>().WithMessage("*--file file=*");
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>`dcms link-agent` writes the agent's MCP configuration without disturbing what else is in it.</summary>
public sealed class LinkAgentTests
{
    [Fact]
    public void Adding_a_server_keeps_the_others()
    {
        var dir = Directory.CreateTempSubdirectory("dcms-mcp-").FullName;
        try
        {
            var path = Path.Combine(dir, ".mcp.json");
            File.WriteAllText(path, """{"mcpServers":{"other":{"command":"x"}},"keep":1}""");
            Commands.MergeJson(path, "dcms-acme", new JsonObject { ["command"] = "dcms", ["args"] = new JsonArray("mcp", "--agent", "id") })
                .Should().Be($"{path}#dcms-acme");
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            ((string?)doc["mcpServers"]!["other"]!["command"]).Should().Be("x");
            ((string?)doc["mcpServers"]!["dcms-acme"]!["args"]![2]).Should().Be("id");
            ((int)doc["keep"]!).Should().Be(1);
            Commands.ToolName("sites.write-files").Should().Be("sites_write-files");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
