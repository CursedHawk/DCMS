using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Metadata;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>Flows as configuration: how they are written, what the validator refuses, and which events start them.</summary>
public sealed class FlowModelTests
{
    private const string App = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
              "fields": [ { "apiName": "title", "displayName": "Title" }, { "apiName": "status", "displayName": "Status" },
                          { "apiName": "amount", "displayName": "Amount", "type": "decimal" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "tags", "displayName": "Tag",
              "fields": [ { "apiName": "name", "displayName": "Name" } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "tags", "kind": "manyToMany",
              "sourceTableId": "deals", "targetTableId": "tags", "inverseApiName": "deals" } },
          { "op": "create", "type": "flow", "value": { "apiName": "big_deal", "displayName": "Big deal",
              "trigger": { "event": "row.created", "tableId": "deals" }, "condition": "row.amount >= 10000",
              "steps": [ { "id": "notify", "action": "dcms.notifications.raise@1", "input": { "title": "Big deal", "body": "{{ row.title }}" } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "status_changed", "displayName": "Status changed",
              "trigger": { "event": "row.updated", "tableId": "deals", "changedFields": ["status"] },
              "steps": [ { "id": "log", "action": "event.publish@1", "input": { "name": "deal.moved" } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "tagged", "displayName": "Tagged",
              "trigger": { "event": "relation.created", "relationshipId": "deals.tags" },
              "steps": [ { "id": "go", "action": "flow.invoke@1", "input": { "flow": "manual_one" } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "manual_one", "displayName": "Manual",
              "steps": [ { "id": "noop", "action": "records.query@1", "input": { "table": "deals" } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "nightly", "displayName": "Nightly",
              "trigger": { "event": "schedule", "everyMinutes": 1440 },
              "steps": [ { "id": "count", "action": "records.query@1", "input": { "table": "deals" } } ] } }
        ]
        """;

    private static AppConfig Build(string json, AppConfig? from = null) =>
        ChangeApplier.Apply(from ?? new AppConfig(), JsonSerializer.Deserialize<List<ChangeOperation>>(json, ConfigJson.Options)!);

    private static readonly AppConfig Config = Build(App);

    private static IEnumerable<string> Errors(AppConfig config) =>
        ConfigValidator.Validate(config).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    [Fact]
    public void Flows_are_written_with_names_and_validate()
    {
        Errors(Config).Should().BeEmpty();
        var deals = Config.Tables.Single(t => t.ApiName == "deals");
        var changed = Config.Flows.Single(f => f.ApiName == "status_changed").Trigger;
        changed.TableId.Should().Be(deals.Id);
        changed.ChangedFields.Should().Equal(deals.Fields.Single(f => f.ApiName == "status").Id);
        Config.Flows.Single(f => f.ApiName == "tagged").Trigger.RelationshipId.Should().Be(Config.Relationships.Single().Id);
    }

    private static AppEvent Event(string name, string? table = null, string[]? changed = null, JsonObject? payload = null) => new()
    {
        EventName = name,
        Entity = table is null ? null : new AppEventEntity(table, Guid.NewGuid()),
        ChangedFields = changed ?? [],
        Payload = payload ?? [],
    };

    [Fact]
    public void Events_start_the_flows_whose_trigger_they_match()
    {
        Names(Event("row.created", "deals")).Should().Equal("big_deal");
        Names(Event("row.created", "tags")).Should().BeEmpty();
        Names(Event("row.updated", "deals", ["status"])).Should().Equal("status_changed");
        Names(Event("row.updated", "deals", ["title"])).Should().BeEmpty("only a change to status starts it");
        Names(Event("relation.created", payload: new JsonObject { ["relationship"] = "tags" })).Should().Equal("tagged");
        Names(Event("manual")).Should().Equal("manual_one");

        var disabled = Build("""[ { "op": "update", "type": "flow", "target": "big_deal", "value": { "enabled": false } } ]""", Config);
        TriggerRouter.Match(disabled, Event("row.created", "deals")).Should().BeEmpty();
    }

    private static IEnumerable<string> Names(AppEvent evt) => TriggerRouter.Match(Config, evt).Select(f => f.ApiName);

    [Fact]
    public void Other_plugins_events_and_actions_are_usable_once_offered()
    {
        var withPlatform = Build("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "welcome", "displayName": "Welcome",
                "trigger": { "event": "visitor.registered" },
                "steps": [ { "id": "tag", "action": "visitor-auth.set-attributes@1", "input": { "visitorId": "{{ event.payload.visitorId }}" } } ] } },
              { "op": "create", "type": "flow", "value": { "apiName": "file_it", "displayName": "File it",
                "trigger": { "event": "form.submitted" }, "condition": "event.payload.formName == 'contact'",
                "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "tags", "values": { "name": "{{ event.payload.data.name }}" } } } ] } } ]
            """, Config);

        Errors(withPlatform).Should().Equal(["unknown-action"], "no provider offers the visitor action here");
        var offered = ActionCatalog.Keys.Append("visitor-auth.set-attributes@1").ToHashSet();
        ConfigValidator.Validate(withPlatform, actions: offered).Where(i => i.Severity == IssueSeverity.Error).Should().BeEmpty();
        TriggerRouter.Match(withPlatform, Event("form.submitted")).Select(f => f.ApiName).Should().Equal("file_it");

        var onATable = Build("""[ { "op": "update", "type": "flow", "target": "welcome", "value": { "trigger": { "event": "visitor.registered", "tableId": "deals" } } } ]""", withPlatform);
        ConfigValidator.Validate(onATable, actions: offered).Select(i => i.Code).Should().Contain("invalid-trigger");
    }

    [Fact]
    public void A_flow_hash_changes_with_its_definition_only()
    {
        var flow = Config.Flows.Single(f => f.ApiName == "big_deal");
        TriggerRouter.Hash(flow).Should().Be(TriggerRouter.Hash(flow with { }));
        TriggerRouter.Hash(flow).Should().NotBe(TriggerRouter.Hash(flow with { Condition = "row.amount >= 20000" }));
    }

    [Theory]
    [InlineData("""{ "trigger": { "event": "row.exploded", "tableId": "deals" } }""", "invalid-trigger")]
    [InlineData("""{ "trigger": { "event": "row.created" } }""", "invalid-trigger")]
    [InlineData("""{ "trigger": { "event": "manual", "tableId": "deals" } }""", "invalid-trigger")]
    [InlineData("""{ "trigger": { "event": "schedule", "everyMinutes": 1 } }""", "invalid-trigger")]
    [InlineData("""{ "condition": "row.amount >" }""", "invalid-expression")]
    [InlineData("""{ "condition": "secrets.key == 1" }""", "invalid-expression")]
    [InlineData("""{ "steps": [] }""", "invalid-flow")]
    [InlineData("""{ "steps": [ { "id": "Bad-Id", "action": "records.query@1", "input": { "table": "deals" } } ] }""", "invalid-step")]
    [InlineData("""{ "steps": [ { "id": "a", "action": "records.create@9", "input": {} } ] }""", "unknown-action")]
    [InlineData("""{ "steps": [ { "id": "a", "action": "shell.exec@1", "input": {} } ] }""", "unknown-action")]
    [InlineData("""{ "steps": [ { "id": "a", "action": "records.query@1", "input": { "table": "{{ row. }}" } } ] }""", "invalid-expression")]
    [InlineData("""{ "steps": [ { "id": "a", "action": "flow.invoke@1", "input": { "flow": "big_deal" } } ] }""", "invalid-step")]
    [InlineData("""{ "steps": [ { "id": "a", "action": "records.query@1", "input": {} }, { "id": "a", "action": "records.query@1", "input": {} } ] }""", "duplicate-step")]
    public void Invalid_flows_are_reported(string patch, string code)
    {
        Errors(Build($$"""[ { "op": "update", "type": "flow", "target": "manual_one", "value": {{patch}} } ]""", Config)).Should().Contain(code);
    }

    [Fact]
    public void A_patched_trigger_drops_what_the_patch_clears()
    {
        var changed = Build("""[ { "op": "update", "type": "flow", "target": "status_changed", "value": { "trigger": { "changedFields": null } } } ]""", Config);

        var trigger = changed.Flows.Single(f => f.ApiName == "status_changed").Trigger;
        trigger.ChangedFields.Should().BeEmpty();
        trigger.TableId.Should().NotBeNull("the rest of the trigger stays");
    }

    [Fact]
    public void Every_catalog_action_is_versioned_and_described()
    {
        ActionCatalog.All.Should().OnlyContain(a => a.Major >= 1 && a.Description.Length > 10 && a.InputSchema["type"]!.GetValue<string>() == "object");
        ActionCatalog.Keys.Should().Contain(["records.create@1", "dcms.email.send@1", "flow.invoke@1", "visitor.lookup@1"]);
        ActionCatalog.All.Select(ActionCatalog.Key).Should().OnlyHaveUniqueItems();
    }
}
