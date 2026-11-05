using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Runtime;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// What a run's trace keeps (workbench W3): every host call in order, but never a reply's value,
/// and nothing of an HTTP call that could carry a credential. And how a TypeScript frame is
/// mapped back to the repository.
/// </summary>
public sealed class RunTraceTests
{
    private static readonly JsonObject Ok = new() { ["ok"] = true, ["result"] = "sk_live_secret_value" };

    [Fact]
    public void A_trace_keeps_calls_but_no_results_and_no_credentials()
    {
        var trace = new RunTrace();
        trace.Add("secrets.get", JsonNode.Parse("""{ "name": "STRIPE_KEY" }"""), Ok, TimeSpan.FromMilliseconds(3));
        trace.Add("http.fetch", JsonNode.Parse("""
            { "url": "https://api.stripe.com/v1/charges?api_key=sk_live_q", "method": "POST",
              "headers": { "Authorization": "Bearer sk_live_h", "X-Signature-Token": "t", "Accept": "application/json" },
              "body": "amount=1&secret=sk_live_b" }
            """), new JsonObject { ["ok"] = false, ["error"] = "Not declared." }, TimeSpan.FromMilliseconds(40));

        // A secret passed on anywhere, a webhook URL's token, userinfo, an unusual header.
        trace.Add("http.fetch", JsonNode.Parse("""
            { "url": "https://ops:hunter2@hooks.slack.com/services/T0/B0/xoxb-path-token", "headers": { "X-Auth": "plain-credential" } }
            """), Ok, TimeSpan.Zero);
        trace.Add("db", JsonNode.Parse("""{ "path": ["crm", "notes", "create"], "args": [{ "text": "key is sk_live_secret_value" }] }"""), Ok, TimeSpan.Zero);

        var text = trace.Entries.ToJsonString();
        text.Should().NotContain("sk_live").And.NotContain("hunter2").And.NotContain("xoxb").And.NotContain("plain-credential").And.NotContain("T0/B0");
        trace.Entries.Select(e => e!["op"]!.GetValue<string>()).Should().Equal("secrets.get", "http.fetch", "http.fetch", "db");
        trace.Entries[1]!["args"]!.GetValue<string>().Should().Contain("application/json").And.Contain("https://api.stripe.com/…").And.Contain("characters");
        trace.Entries[2]!["args"]!.GetValue<string>().Should().Contain("https://hooks.slack.com/…");
        trace.Entries[3]!["args"]!.GetValue<string>().Should().Contain("key is •••");
        trace.Entries[1]!["ok"]!.GetValue<bool>().Should().BeFalse();
        trace.Entries[1]!["error"]!.GetValue<string>().Should().Be("Not declared.");
    }

    [Fact]
    public void A_trace_is_bounded()
    {
        var trace = new RunTrace();
        for (var i = 0; i < RunTrace.MaxEntries + 50; i++)
        {
            trace.Add("db", JsonNode.Parse($$"""{ "path": ["a", "b", "get"], "args": ["{{new string('x', 1_000)}}"] }"""), Ok, TimeSpan.Zero);
        }
        trace.Entries.Should().HaveCount(RunTrace.MaxEntries);
        trace.Entries[^1]!["args"]!.GetValue<string>().Should().Be("51 more calls");
        trace.Entries[0]!["args"]!.GetValue<string>().Length.Should().BeLessThan(310);
    }

    [Fact]
    public void A_bundle_frame_maps_to_the_repository_file_and_line()
    {
        // esbuild's map of a two-file bundle: line 2 of the bundle is line 2 of lib/fail.ts.
        const string map = """
            { "version": 3, "sources": ["../lib/fail.ts", "../.dcms/runtime.js"], "mappings": ";AACE;ACAA", "names": [] }
            """;
        SourceMaps.Map("Error: kaput\n    at fail (function:2:3)\n    at run (function:3:1)", map)
            .Should().Be("Error: kaput\n    at fail (lib/fail.ts:2:3)\n    at run (@dcms/functions:2:3)");
        SourceMaps.Map("Error: no frames", map).Should().Be("Error: no frames");
        SourceMaps.Map("at x (function:2:3)", "not json").Should().Be("at x (function:2:3)");
    }
}
