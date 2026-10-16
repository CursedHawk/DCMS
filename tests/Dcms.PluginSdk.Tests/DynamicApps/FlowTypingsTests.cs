using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Metadata;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>The TypeScript a flow script's editor is given for ctx (ADR 0025): from the model, the trigger and the steps before it.</summary>
public sealed class FlowTypingsTests
{
    private static readonly AppConfig Config = ChangeApplier.Apply(new AppConfig(), JsonSerializer.Deserialize<List<ChangeOperation>>("""
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "labels", "displayName": "Labels", "options": [ { "value": "a", "label": "A" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deal_stage", "displayName": "Deal */ stage",
              "fields": [ { "apiName": "title", "displayName": "Title" }, { "apiName": "amount", "displayName": "Amount", "type": "decimal" },
                          { "apiName": "won", "displayName": "Won", "type": "boolean" }, { "apiName": "labels", "displayName": "Labels", "type": "multiChoice", "choiceSetId": "labels" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "company", "displayName": "Company", "fields": [ { "apiName": "name", "displayName": "Name" } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "kind": "manyToOne", "sourceTableId": "deal_stage", "targetTableId": "company" } },
          { "op": "create", "type": "flow", "value": { "apiName": "score", "displayName": "Score",
              "trigger": { "event": "row.updated", "tableId": "deal_stage" },
              "steps": [
                { "id": "owner", "action": "records.lookup@1", "input": { "table": "company", "id": "{{ row.company }}" }, "condition": "row.company != null" },
                { "id": "similar", "action": "records.query@1", "input": { "table": "deal_stage" } },
                { "id": "calc", "action": "script.run@1", "input": { "code": "export default () => 1", "args": { "limit": "{{ input.limit }}" } } },
                { "id": "after", "action": "event.publish@1", "input": { "name": "x" } }
              ] } }
        ]
        """, ConfigJson.Options)!);

    private static string Typings(string? at = "calc") =>
        FlowTypings.Generate(Config, Config.Flows.Single(), at, ActionCatalog.All);

    [Fact]
    public void The_trigger_tables_record_is_row_and_previous_is_partial_on_an_update()
    {
        var d = Typings();
        d.Should().Contain("interface DealStageRow {").And.Contain("  amount: number | null;").And.Contain("  won: boolean | null;")
            .And.Contain("  labels: string[] | null;").And.Contain("  company: string | null;").And.Contain("  created_at: string;");
        d.Should().Contain("row: DealStageRow;").And.Contain("previous: Partial<DealStageRow>;");
    }

    [Fact]
    public void Steps_before_the_script_are_typed_from_their_actions_output()
    {
        var d = Typings();
        d.Should().Contain("owner: CompanyRow | null;", "a lookup may find nothing (and a step skipped by its condition is null)")
            .And.Contain("similar: { items: Array<DealStageRow>; total: number; totalCapped: boolean; };")
            .And.Contain("args: { limit: FlowJson; }");
        d.Should().NotContain("calc:", "a step does not see its own output").And.NotContain("after:");
        Typings(at: null).Should().Contain("calc: unknown;").And.Contain("after: { eventName: string; };");
    }

    [Fact]
    public void A_trigger_without_a_record_has_none_and_names_cannot_escape_a_comment()
    {
        var manual = Config.Flows.Single() with { Trigger = new FlowTrigger { Event = "manual" } };
        var d = FlowTypings.Generate(Config, manual, null, ActionCatalog.All);
        d.Should().Contain("row: null;").And.Contain("previous: null;");
        Typings().Should().Contain("Deal * / stage").And.NotContain("Deal */ stage");
    }
}
