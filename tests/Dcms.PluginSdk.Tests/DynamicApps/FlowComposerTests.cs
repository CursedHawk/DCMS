using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>"Write with AI" in the flow editor: a model's answer becomes a resolved, validated flow — or is sent back once.</summary>
public sealed class FlowComposerTests
{
    private static readonly AppConfig Config = ChangeApplier.Apply(new AppConfig(), JsonSerializer.Deserialize<List<ChangeOperation>>("""
        [ { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
              "fields": [ { "apiName": "title", "displayName": "Title" }, { "apiName": "amount", "displayName": "Amount", "type": "decimal" },
                          { "apiName": "status", "displayName": "Status" } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "big_deal", "displayName": "Big deal",
              "trigger": { "event": "row.created", "tableId": "deals" },
              "steps": [ { "id": "notify", "action": "dcms.notifications.raise@1", "input": { "title": "Big", "body": "{{ row.title }}" } } ] } } ]
        """, ConfigJson.Options)!);

    private const string Good = """
        Here it is:
        ```json
        { "apiName": "won_deal", "displayName": "Deal won",
          "trigger": { "event": "row.updated", "tableId": "deals", "changedFields": ["status"] },
          "condition": "row.status == 'won'",
          "steps": [ { "id": "tell", "action": "dcms.notifications.raise@1", "input": { "title": "Won", "body": "{{ row.title }}" } } ],
          "note": "Notifies members when a deal is won." }
        ```
        """;

    private sealed class ScriptedModel(params string?[] answers) : IPluginAi
    {
        public List<AiCompletionRequest> Asked { get; } = [];

        public Task<AiCompletion> CompleteAsync(AiCompletionRequest input, CancellationToken ct)
        {
            Asked.Add(input);
            return Task.FromResult(new AiCompletion(answers[Math.Min(Asked.Count - 1, answers.Length - 1)]));
        }
    }

    private static Task<FlowComposition> Compose(IPluginAi ai, FlowDef? editing = null) =>
        FlowComposer.ComposeAsync(Config, ActionCatalog.All, new FlowComposeRequest("Tell everyone when a deal is won", editing), ai, TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_answer_in_names_becomes_a_flow_in_ids_with_no_issues()
    {
        var model = new ScriptedModel(Good);
        var result = await Compose(model);

        var deals = Config.Tables.Single();
        result.Flow.ApiName.Should().Be("won_deal");
        result.Flow.Trigger.TableId.Should().Be(deals.Id);
        result.Flow.Trigger.ChangedFields.Should().Equal(deals.Fields.Single(f => f.ApiName == "status").Id);
        result.Note.Should().Be("Notifies members when a deal is won.");
        result.Issues.Should().BeEmpty();
        model.Asked.Should().ContainSingle();
        model.Asked[0].System.Should().Contain("- deals \"Deal\": title: text, amount: decimal").And.Contain("records.query@1")
            .And.Contain("coalesce(a, b, …)").And.Contain("Existing flows (api name, trigger): big_deal (row.created)");
    }

    [Fact]
    public async Task A_refused_answer_goes_back_once_with_the_reason()
    {
        var model = new ScriptedModel(Good.Replace("\"tableId\": \"deals\"", "\"tableId\": \"opportunities\""), Good);
        var result = await Compose(model);

        model.Asked.Should().HaveCount(2);
        model.Asked[1].Prompt.Should().Contain("The server refused it").And.Contain("opportunities");
        result.Flow.ApiName.Should().Be("won_deal");
    }

    [Fact]
    public async Task What_still_fails_validation_comes_back_as_issues_for_the_editor()
    {
        var bad = Good.Replace("row.status == 'won'", "secrets.key == 1");
        var result = await Compose(new ScriptedModel(bad, bad));
        result.Issues.Should().Contain(i => i.Code == "invalid-expression");
    }

    [Fact]
    public async Task Changing_a_saved_flow_keeps_its_api_name_and_shows_the_model_its_current_state()
    {
        var existing = Config.Flows.Single();
        var model = new ScriptedModel(Good);
        var result = await Compose(model, existing);

        result.Flow.Id.Should().Be(existing.Id);
        result.Flow.ApiName.Should().Be("big_deal", "other flows and the site invoke it by that name");
        model.Asked[0].Prompt.Should().Contain("The flow as it is in the editor now").And.Contain("\"tableId\":\"deals\"");
    }

    [Fact]
    public async Task A_new_flow_never_takes_an_existing_name()
    {
        var result = await Compose(new ScriptedModel(Good.Replace("won_deal", "big_deal")));
        result.Flow.ApiName.Should().Be("big_deal_2");
    }

    [Fact]
    public async Task No_provider_says_so()
    {
        var act = () => Compose(new ScriptedModel((string?)null));
        (await act.Should().ThrowAsync<ContractValidationException>()).Which.Message.Should().Contain("no AI provider");
    }
}
