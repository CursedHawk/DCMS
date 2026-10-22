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

    // ------------------------------------------------------------------ row rules

    [Fact]
    public void A_rule_through_an_inverse_navigation_is_a_set_the_planner_hashes_once()
    {
        var parameters = new List<object>();
        var sql = RowRules.Sql(Model, Model.Table("companies")!, new RowRule { Path = ["deals"], Field = "title", Matches = "user.email" },
            new RowSubject(Guid.NewGuid(), "ada@example.com", [], new Dictionary<string, JsonElement>()), "r", Guid.NewGuid(), Guid.NewGuid(),
            (value, _) => { parameters.Add(value); return $"${parameters.Count}"; });

        // Containment (`"Data" @> jsonb_build_object(…)`) cannot be hashed: records × grants, 15 s a page
        // on vps1. A correlated or join-convertible set nested-looped under ORDER BY … LIMIT: 0.7 s.
        sql.Should().NotContain("@>").And.NotContain("r.\"TenantId\"").And.NotContain("EXISTS");
        sql.Should().StartWith("""(r."Id"::text IN (SELECT ra0."Data" ->> $""").And.EndWith(") IS TRUE");
    }

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

        compiled.Where.Should().NotContain(Hostile).And.NotContain("10000").And.NotContain("lead").And.Contain("IN (SELECT t1.\"Id\"::text");
        compiled.Specs.Select(p => p.Value).Should().Contain(Hostile);
        compiled.OrderBy.Should().StartWith("((r.\"Data\" ->> @").And.Contain("::numeric DESC NULLS LAST").And.EndWith("r.\"Id\"");
        (compiled.Limit, compiled.Offset).Should().Be((25, 25));
    }

    [Fact]
    public void A_filter_through_a_lookup_is_a_set_the_planner_hashes_once()
    {
        var compiled = Compile("""{ "filter": { "field": "company.name", "op": "eq", "value": "Acme" } }""");

        // A correlated EXISTS nested-looped on vps1 (deals x companies, 11.9 s a page): no reference
        // to the outer row inside the set, and IS TRUE so it is not planned as a semi-join.
        compiled.Where.Should().NotContain("EXISTS").And.NotContain("= r.\"TenantId\"").And.NotContain("= r.\"InstanceId\"");
        compiled.Where.Should().Contain("((r.\"Data\" ->> @").And.Contain(") IN (SELECT t1.\"Id\"::text FROM apps.records t1").And.EndWith(") IS TRUE");
    }

    [Fact]
    public void Sorts_and_filters_are_written_as_the_field_indexes_are()
    {
        // Text sorts by its indexed prefix, dates compare as their stored ISO text (no casts, which
        // cannot be indexed), and text equality narrows by the prefix and decides by the whole value.
        var sorted = Compile("""{ "sort": [ { "field": "title" } ] }""");
        sorted.OrderBy.Should().StartWith("left((r.\"Data\" ->> @").And.Contain($", {RecordIndexes.PrefixChars}) ASC NULLS LAST");

        var dated = Compile(
            """{ "filter": { "and": [ { "field": "closes", "op": "gte", "value": "2026-01-01" }, { "field": "signed_at", "op": "lt", "value": "2026-10-06T12:00:00+02:00" } ] } }""");
        dated.Where.Should().NotContain("::date").And.NotContain("::timestamptz");
        dated.Specs.Select(p => p.Value).Should().Contain(["2026-01-01", "2026-10-06T10:00:00.000Z"]);

        var titled = Compile("""{ "filter": { "field": "title", "op": "eq", "value": "Big one" } }""");
        titled.Where.Should().Contain("(left((r.\"Data\" ->> @").And.Contain(" AND (r.\"Data\" ->> @");
    }

    [Fact]
    public void Search_is_a_like_with_the_terms_wildcards_escaped()
    {
        var compiled = Compile("""{ "search": "50%_x\\" }""");
        compiled.Where.Should().Contain("lower((r.\"Data\" ->> @").And.Contain(" LIKE ('%' || lower(@").And.NotContain("strpos");
        compiled.Specs.Select(p => p.Value).Should().Contain(@"50\%\_x\\", "the term's own % and _ match only themselves");
    }

    [Fact]
    public void Searchable_fields_or_else_the_primary_field_get_a_search_index()
    {
        var specs = RecordIndexes.For(Model.Config, Guid.NewGuid(), Guid.NewGuid()).Where(s => s.Kind == RecordIndexes.KeyKind.Search).ToList();
        // companies.name is searchable; deals has no searchable field, so its primary field (title).
        specs.Select(s => s.Key).Should().BeEquivalentTo([Model.Table("companies")!.ByName["name"].Key, Deals.ByName["title"].Key]);
        specs.Should().OnlyContain(s => !s.Descending);
    }

    [Fact]
    public void Sortable_and_filterable_fields_get_indexes_up_to_the_cap()
    {
        var config = ChangeApplier.Apply(new AppConfig(), JsonSerializer.Deserialize<List<ChangeOperation>>("""
            [ { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "fields": [
                { "apiName": "title", "displayName": "Title", "sortable": true },
                { "apiName": "amount", "displayName": "Amount", "type": "decimal", "filterable": true },
                { "apiName": "won", "displayName": "Won", "type": "boolean", "sortable": true, "filterable": true },
                { "apiName": "notes", "displayName": "Notes" } ] } } ]
            """, ConfigJson.Options)!);
        var (tenant, instance) = (Guid.NewGuid(), Guid.NewGuid());
        var specs = RecordIndexes.For(config, tenant, instance);
        var wanted = RecordIndexes.Wanted(config);
        specs.Select(s => (s.Kind, s.Descending)).Should().Equal(
            (RecordIndexes.KeyKind.Text, false), (RecordIndexes.KeyKind.Text, true), (RecordIndexes.KeyKind.Number, false));
        wanted.Should().Be(3, "a boolean and an unflagged field get none");
        specs.Select(s => s.Name).Should().OnlyHaveUniqueItems().And.AllSatisfy(n => n.Should().MatchRegex("^ix_da_[0-9a-f]{24}$"));
        RecordIndexes.For(config, tenant, instance).Select(s => s.Name).Should().Equal(specs.Select(s => s.Name), "names are stable");
        RecordIndexes.For(config, Guid.NewGuid(), instance).Select(s => s.Name).Should().NotIntersectWith(specs.Select(s => s.Name),
            "another tenant's same table id is another index");
        specs.Should().OnlyContain(s => s.TenantId == tenant && s.InstanceId == instance);

        var big = config with { Tables = Enumerable.Range(0, 40).Select(i => config.Tables[0] with { Id = Guid.NewGuid() }).ToList() };
        RecordIndexes.For(big, tenant, instance).Should().HaveCount(RecordIndexes.MaxPerApp);
        RecordIndexes.Wanted(big).Should().Be(120);
        ConfigValidator.Validate(config).Should().NotContain(i => i.Code == "index-limit");
        ConfigValidator.Validate(big).Should().Contain(i => i.Code == "index-limit" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Eq_null_means_has_no_value()
    {
        // A flow filtering by a template that came out empty must not fail on a refused value.
        Compile("""{ "filter": { "field": "title", "op": "eq", "value": null } }""").Where
            .Should().Be(Compile("""{ "filter": { "field": "title", "op": "isNull", "value": true } }""").Where);
        Compile("""{ "filter": { "field": "company.name", "op": "ne", "value": null } }""").Where
            .Should().Be(Compile("""{ "filter": { "field": "company.name", "op": "isNull", "value": false } }""").Where);
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
        Compile("""{ "filter": { "field": "company.name", "op": "startsWith", "value": "A" } }""").Where.Should().Contain("IN (SELECT t1.",
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
