using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>
/// The data plane's pure half: how a record is validated and stored (<see cref="RecordCodec"/>)
/// and how a query becomes SQL (<see cref="QueryCompiler"/>). The SQL itself runs in the
/// integration tests; here the point is what is accepted, what is refused, and that nothing a
/// caller sends is ever written into the SQL text.
/// </summary>
public sealed class RecordModelTests
{
    private const string Crm = """
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "stage", "displayName": "Stage",
              "options": [ { "value": "lead", "label": "Lead" }, { "value": "won", "label": "Won" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true, "searchable": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "title",
              "fields": [
                { "apiName": "title", "displayName": "Title", "required": true, "maxLength": 50 },
                { "apiName": "amount", "displayName": "Amount", "type": "decimal", "minimum": 0 },
                { "apiName": "seats", "displayName": "Seats", "type": "integer" },
                { "apiName": "won", "displayName": "Won", "type": "boolean", "default": false },
                { "apiName": "closes", "displayName": "Closes", "type": "date" },
                { "apiName": "signed_at", "displayName": "Signed at", "type": "dateTime" },
                { "apiName": "contact", "displayName": "Contact", "type": "email", "unique": true },
                { "apiName": "stage", "displayName": "Stage", "type": "choice", "choiceSetId": "stage" },
                { "apiName": "tags", "displayName": "Tags", "type": "multiChoice", "choiceSetId": "stage" },
                { "apiName": "score", "displayName": "Score", "type": "integer", "readOnly": true },
                { "apiName": "notes", "displayName": "Notes", "type": "longText", "hiddenFromPublic": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals",
              "targetTableId": "companies", "inverseApiName": "deals" } }
        ]
        """;

    private static readonly RuntimeModel Model = RuntimeModel.Compile(new RevisionDocument(
        new RevisionInfo { Number = 1 },
        ChangeApplier.Apply(new AppConfig(), JsonSerializer.Deserialize<List<ChangeOperation>>(Crm, ConfigJson.Options)!)));

    private static RuntimeTable Deals => Model.Table("deals")!;

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static IReadOnlyDictionary<string, string> Refused(string json, RecordPlane plane = RecordPlane.Admin) =>
        Assert.Throws<RecordValidationException>(() => RecordCodec.Write(Deals, Obj(json), null, plane)).Errors;

    // ------------------------------------------------------------------ writing records

    [Fact]
    public void Values_are_normalized_and_stored_under_field_ids()
    {
        var company = Guid.NewGuid();
        var write = RecordCodec.Write(Deals, Obj($$"""
            { "title": "Big one", "amount": 12.50, "seats": 3, "closes": "2026-12-31", "signed_at": "2026-10-06T12:00:00+02:00",
              "contact": " Ada@Example.com ", "stage": "lead", "tags": ["won", "lead", "won"], "company": "{{company.ToString().ToUpperInvariant()}}",
              "id": "ignored", "created_at": "ignored" }
            """), null, RecordPlane.Admin);

        string Key(string name) => Deals.ByName[name].Key;
        write.Data[Key("contact")]!.GetValue<string>().Should().Be("Ada@Example.com", "trimmed, case kept");
        write.Data[Key("signed_at")]!.GetValue<string>().Should().Be("2026-10-06T10:00:00.000Z", "one fixed-width UTC form");
        write.Data[Key("tags")]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("won", "lead");
        write.Data[Key("company")]!.GetValue<string>().Should().Be(company.ToString());
        write.Data[Key("won")]!.GetValue<bool>().Should().BeFalse("the default fills what a new record leaves out");
        write.Data.Should().NotContainKey("title", "values are keyed by field id, not api name");
        write.Changed.Should().Contain(["title", "amount", "company"]);
    }

    [Fact]
    public void Every_bad_value_is_reported_by_field()
    {
        var errors = Refused($$"""
            { "title": "{{new string('x', 60)}}", "amount": -1, "seats": 1.5, "closes": "31/12/2026", "contact": "not-an-email",
              "stage": "lost", "tags": ["nope"], "company": "acme", "colour": "red", "score": 5 }
            """);

        errors.Keys.Should().BeEquivalentTo(["title", "amount", "seats", "closes", "contact", "stage", "tags", "company", "colour", "score"]);
        errors["colour"].Should().Contain("no field");
        errors["score"].Should().Contain("read-only");
    }

    [Fact]
    public void A_body_naming_more_properties_than_the_table_has_is_refused_up_front()
    {
        var flood = "{" + string.Join(",", Enumerable.Range(0, 1_000).Select(i => $"\"k{i}\": 1")) + "}";
        Refused(flood).Keys.Should().Equal("_record");
    }

    [Fact]
    public void Required_fields_must_be_present_on_create_and_cannot_be_cleared()
    {
        Refused("""{ "amount": 1 }""").Keys.Should().BeEquivalentTo(["title"]);

        var existing = RecordCodec.Write(Deals, Obj("""{ "title": "A" }"""), null, RecordPlane.Admin).Data;
        var clear = () => RecordCodec.Write(Deals, Obj("""{ "title": null }"""), existing, RecordPlane.Admin);
        clear.Should().Throw<RecordValidationException>().Which.Errors.Should().ContainKey("title");

        var update = RecordCodec.Write(Deals, Obj("""{ "amount": 5, "version": 3 }"""), existing, RecordPlane.Admin);
        update.Changed.Should().Equal("amount");
        update.ExpectedVersion.Should().Be(3);
    }

    [Fact]
    public void Automations_may_set_read_only_fields_and_the_public_site_never_sees_hidden_ones()
    {
        var write = RecordCodec.Write(Deals, Obj("""{ "title": "A", "score": 9 }"""), null, RecordPlane.System);
        write.Changed.Should().Contain("score");

        Refused("""{ "title": "A", "notes": "x" }""", RecordPlane.Public).Should().ContainKey("notes");
    }

    [Fact]
    public void Unique_keys_ignore_email_case_and_skip_empty_values()
    {
        var a = RecordCodec.Write(Deals, Obj("""{ "title": "A", "contact": "Ada@Example.com" }"""), null, RecordPlane.Admin).Data;
        var b = RecordCodec.Write(Deals, Obj("""{ "title": "B", "contact": "ada@example.COM" }"""), null, RecordPlane.Admin).Data;
        var c = RecordCodec.Write(Deals, Obj("""{ "title": "C" }"""), null, RecordPlane.Admin).Data;

        RecordCodec.UniqueKeys(Deals, a).Should().Equal(RecordCodec.UniqueKeys(Deals, b));
        RecordCodec.UniqueKeys(Deals, c).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ compiling queries

    private static QueryCompiler.Compiled Compile(string query, RecordPlane plane = RecordPlane.Admin) =>
        new QueryCompiler(Model, Deals, plane).Compile(
            JsonSerializer.Deserialize<RecordQuery>(query, ConfigJson.Options)!, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void A_query_compiles_to_sql_with_every_value_as_a_parameter()
    {
        const string Hostile = "'); DROP TABLE apps.records; --";
        var compiled = Compile($$"""
            { "filter": { "and": [
                { "field": "stage", "op": "in", "value": ["lead", "won"] },
                { "field": "amount", "op": "gte", "value": 10000 },
                { "field": "title", "op": "contains", "value": "{{Hostile}}" },
                { "or": [ { "field": "company.name", "op": "startsWith", "value": "Ac" }, { "not": { "field": "closes", "op": "isNull", "value": true } } ] } ] },
              "sort": [ { "field": "amount", "direction": "desc" } ], "search": "big", "page": 2, "pageSize": 25 }
            """);

        compiled.Where.Should().NotContain(Hostile).And.NotContain("10000").And.NotContain("lead").And.Contain("EXISTS");
        compiled.Specs.Select(p => p.Value).Should().Contain(Hostile);
        compiled.OrderBy.Should().StartWith("(r.\"Data\" ->> @").And.Contain("::numeric DESC NULLS LAST").And.EndWith("r.\"Id\"");
        (compiled.Limit, compiled.Offset).Should().Be((25, 25));
    }

    [Theory]
    [InlineData("""{ "filter": { "field": "nope", "op": "eq", "value": 1 } }""", "no field 'nope'")]
    [InlineData("""{ "filter": { "field": "amount", "op": "contains", "value": "1" } }""", "does not support 'contains'")]
    [InlineData("""{ "filter": { "field": "won", "op": "gt", "value": true } }""", "does not support 'gt'")]
    [InlineData("""{ "filter": { "field": "amount", "op": "gt", "value": "lots" } }""", "not a valid value")]
    [InlineData("""{ "filter": { "field": "closes", "op": "eq", "value": "tomorrow" } }""", "not a valid value")]
    [InlineData("""{ "filter": { "field": "company", "op": "eq", "value": "acme" } }""", "not a valid value")]
    [InlineData("""{ "filter": { "field": "company.name.x", "op": "eq", "value": "a" } }""", "one lookup at most")]
    [InlineData("""{ "filter": { "field": "title.name", "op": "eq", "value": "a" } }""", "not a lookup")]
    [InlineData("""{ "filter": { "field": "stage", "op": "in", "value": [] } }""", "list of 1 to")]
    [InlineData("""{ "filter": { "field": "title", "and": [] } }""", "exactly one of")]
    [InlineData("""{ "filter": { "and": [] } }""", "at least one")]
    [InlineData("""{ "pageSize": 1000 }""", "pageSize")]
    [InlineData("""{ "page": 5000, "pageSize": 100 }""", "narrow the query")]
    [InlineData("""{ "sort": [ { "field": "tags" } ] }""", "cannot be sorted")]
    [InlineData("""{ "select": ["nope"] }""", "no field 'nope'")]
    [InlineData("""{ "expand": ["title"] }""", "only lookups expand")]
    public void An_invalid_query_is_refused_with_the_reason(string query, string reason)
    {
        var act = () => Compile(query);
        act.Should().Throw<ContractValidationException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void Nesting_and_predicate_counts_are_capped()
    {
        var deep = """{ "field": "won", "op": "eq", "value": true }""";
        for (var i = 0; i < QueryCompiler.MaxDepth; i++)
        {
            deep = $$"""{ "not": {{deep}} }""";
        }
        var tooDeep = () => Compile($$"""{ "filter": {{deep}} }""");
        tooDeep.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("nest");

        var many = string.Join(",", Enumerable.Repeat("""{ "field": "won", "op": "eq", "value": true }""", QueryCompiler.MaxPredicates + 1));
        var tooMany = () => Compile($$"""{ "filter": { "or": [ {{many}} ] } }""");
        tooMany.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("at most");
    }

    [Fact]
    public void The_public_site_cannot_filter_through_a_lookup_into_a_table_it_may_not_read()
    {
        // deals.company points at companies, which is private: a match would leak its rows.
        var act = () => Compile("""{ "filter": { "field": "company.name", "op": "startsWith", "value": "A" } }""", RecordPlane.Public);
        act.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("no field 'company.name'");
        Compile("""{ "filter": { "field": "company.name", "op": "startsWith", "value": "A" } }""").Where.Should().Contain("EXISTS",
            "members in the admin may");
    }

    [Fact]
    public void The_public_site_cannot_filter_on_what_it_cannot_see()
    {
        var act = () => Compile("""{ "filter": { "field": "notes", "op": "isNull", "value": false } }""", RecordPlane.Public);
        act.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("no field 'notes'");
        var who = () => Compile("""{ "sort": [ { "field": "created_by" } ] }""", RecordPlane.Public);
        who.Should().Throw<ContractValidationException>();
    }
}
