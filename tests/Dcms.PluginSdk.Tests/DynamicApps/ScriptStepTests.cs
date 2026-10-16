using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>script.run@1 on the flow side (ADR 0025): what reaches the runner, and what a run keeps.</summary>
public sealed class ScriptStepTests
{
    private static readonly IFlowAction Script = ActionCatalog.Find("script.run@1")!;
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_code_is_copied_as_written_while_args_are_rendered()
    {
        var input = JsonNode.Parse("""{ "code": "export default (c) => '{{ row.title }}'", "args": { "title": "{{ row.title }}" } }""");
        var scope = new Dictionary<string, JsonNode?> { ["row"] = JsonNode.Parse("""{ "title": "Deal" }""") };

        var rendered = Expressions.Render(input, scope, Now, Script.HtmlInputs, Script.LiteralInputs)!;

        rendered["code"]!.GetValue<string>().Should().Be("export default (c) => '{{ row.title }}'");
        rendered["args"]!["title"]!.GetValue<string>().Should().Be("Deal");
        Expressions.TemplateExpressions(input!.AsObject(), Script.LiteralInputs).Should().Equal("row.title");
    }

    [Fact]
    public void A_run_records_which_code_it_ran_not_the_code()
    {
        var recorded = Script.Recorded(new JsonObject { ["code"] = "export default () => 1", ["args"] = new JsonObject { ["n"] = 1 } });

        recorded["code"]!["chars"]!.GetValue<int>().Should().Be(22);
        recorded["code"]!["sha256"]!.GetValue<string>().Should().HaveLength(64);
        recorded["args"]!["n"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData("""{ "status": "ok", "result": { "big": true } }""", null, false)]
    [InlineData("""{ "status": "error", "error": "nope" }""", "nope", false)]
    [InlineData("""{ "status": "limit", "error": "The script ran longer than 2 s." }""", "longer than", true)]
    public async Task The_runners_verdict_becomes_the_steps_output_or_how_it_failed(string reply, string? failure, bool limit)
    {
        var runner = new FakeRunner(reply);
        var context = Context(runner, new Dictionary<string, JsonNode?> { ["row"] = JsonNode.Parse("""{ "amount": 5 }"""), ["steps"] = new JsonObject() });

        var run = () => Script.RunAsync(context, JsonNode.Parse("""{ "code": "export default (c) => c", "args": { "x": 1 } }""")!.AsObject(), TestContext.Current.CancellationToken);

        if (failure is null)
        {
            (await run())!.ToJsonString().Should().Be("""{"big":true}""");
        }
        else
        {
            (await run.Should().ThrowAsync<FlowFatalException>()).Which.Should().Match<FlowFatalException>(e => e.Message.Contains(failure) && e.Limit == limit);
        }
        var sent = JsonNode.Parse(runner.Body!)!;
        sent["ctx"]!["row"]!["amount"]!.GetValue<int>().Should().Be(5, "the script sees the run's scope");
        sent["ctx"]!["args"]!["x"]!.GetValue<int>().Should().Be(1);
        sent["timeoutMs"]!.GetValue<int>().Should().Be(2_000);
    }

    [Fact]
    public async Task An_unreachable_runner_is_passing_trouble_to_retry_not_a_failed_script()
    {
        var context = Context(new FakeRunner("", HttpStatusCode.ServiceUnavailable), new Dictionary<string, JsonNode?>());
        var run = () => Script.RunAsync(context, JsonNode.Parse("""{ "code": "export default () => 1" }""")!.AsObject(), TestContext.Current.CancellationToken);
        await run.Should().ThrowAsync<HttpRequestException>();
    }

    private static FlowActionContext Context(FakeRunner runner, Dictionary<string, JsonNode?> scope)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("dcms-script-runner", c => c.BaseAddress = new Uri("http://script-runner:8080"))
            .ConfigurePrimaryHttpMessageHandler(() => runner);
        return new FlowActionContext(services.BuildServiceProvider(), null!, new FlowRun { Id = Guid.NewGuid() }, "calc") { Scope = scope };
    }

    private sealed class FakeRunner(string reply, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/run");
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
