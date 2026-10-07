using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>
/// The configuration document's pure layer (ADR 0021): change sets, canonical hashing, diffs and
/// validation. No database — everything here is deterministic by construction, which is what
/// lets the revision service and the assistant rely on it.
/// </summary>
public sealed class ConfigurationModelTests
{
    /// <summary>A small CRM built the way the assistant would: one change set, references by api name.</summary>
    private const string Crm = """
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "status", "displayName": "Status",
              "options": [ { "value": "active", "label": "Active" }, { "value": "inactive", "label": "Inactive" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company",
              "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "type": "text", "required": true, "sortable": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "contacts", "displayName": "Contact",
              "primaryFieldId": "email",
              "fields": [
                { "apiName": "email", "displayName": "Email", "type": "email", "unique": true },
                { "apiName": "status", "displayName": "Status", "type": "choice", "choiceSetId": "status", "default": "active" } ],
              "indexes": [ { "apiName": "by_status", "fieldIds": ["status"] } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "kind": "manyToOne",
              "sourceTableId": "contacts", "targetTableId": "companies", "inverseApiName": "contacts" } },
          { "op": "create", "type": "view", "value": { "apiName": "active", "displayName": "Active contacts",
              "tableId": "contacts", "columns": ["email", "company"], "sort": [ { "fieldId": "email" } ] } }
        ]
        """;

    private static IReadOnlyList<ChangeOperation> Ops(string json) =>
        JsonSerializer.Deserialize<List<ChangeOperation>>(json, ConfigJson.Options)!;

    private static AppConfig Build(string json, AppConfig? from = null) =>
        ChangeApplier.Apply(from ?? new AppConfig(), Ops(json));

    private static IEnumerable<string> Errors(AppConfig config, AppConfig? published = null) =>
        ConfigValidator.Validate(config, published).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    [Fact]
    public void A_reference_kept_as_text_is_flagged_so_it_becomes_a_relationship()
    {
        var config = Build("""
            [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "fields": [ { "apiName": "name", "displayName": "Name" } ] } },
              { "op": "create", "type": "table", "value": { "apiName": "activities", "displayName": "Activity", "fields": [ { "apiName": "subject", "displayName": "Subject" } ] } },
              { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "fields": [
                  { "apiName": "company_id", "displayName": "Company" }, { "apiName": "primary_company_id", "displayName": "Primary company" },
                  { "apiName": "activity_id", "displayName": "Activity", "type": "integer" }, { "apiName": "external_id", "displayName": "External" } ] } } ]
            """);
        ConfigValidator.Validate(config).Where(i => i.Code == "reference-as-text").Select(i => i.Path)
            .Should().BeEquivalentTo(["deals.company_id", "deals.primary_company_id", "deals.activity_id"], "external_id names no table");
        ConfigValidator.Validate(config).Where(i => i.Code == "reference-as-text").Should().OnlyContain(i => i.Severity == IssueSeverity.Warning);
    }

    // ------------------------------------------------------------------ applying change sets

    [Fact]
    public void A_change_set_resolves_api_names_to_ids_and_builds_a_valid_app()
    {
        var config = Build(Crm);

        Errors(config).Should().BeEmpty();
        var contacts = config.Tables.Single(t => t.ApiName == "contacts");
        var email = contacts.Fields.Single(f => f.ApiName == "email");
        contacts.PrimaryFieldId.Should().Be(email.Id);
        contacts.Fields.Single(f => f.ApiName == "status").ChoiceSetId.Should().Be(config.ChoiceSets.Single().Id);

        var company = config.Relationships.Single();
        company.SourceTableId.Should().Be(contacts.Id);
        company.TargetTableId.Should().Be(config.Tables.Single(t => t.ApiName == "companies").Id);
        config.Views.Single().Columns.Should().Equal(email.Id, company.Id);
        config.Views.Single().Sort.Single().FieldId.Should().Be(email.Id);
    }

    [Fact]
    public void Update_is_a_merge_patch_and_leaves_the_rest_alone()
    {
        var config = Build("""
            [ { "op": "update", "type": "field", "target": "contacts.email",
                "value": { "displayName": "E-mail", "unique": null } } ]
            """, Build(Crm));

        var email = config.Tables.Single(t => t.ApiName == "contacts").Fields.Single(f => f.ApiName == "email");
        email.DisplayName.Should().Be("E-mail");
        email.Unique.Should().BeFalse("null removes the key, and the default is not unique");
        email.Type.Should().Be(FieldType.Email);
    }

    [Theory]
    [InlineData("""[ { "op": "update", "type": "table", "target": "contacts", "value": { "id": "8f0b9a4e-1111-4b1a-9d1e-000000000001" } } ]""", "id never changes")]
    [InlineData("""[ { "op": "update", "type": "table", "target": "contacts", "value": { "fields": [] } } ]""", "field and index operations")]
    [InlineData("""[ { "op": "delete", "type": "field", "target": "contacts.nope" } ]""", "no field 'contacts.nope'")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "x", "displayName": "X", "requried": true } } ]""", "requried")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "x", "displayName": "X", "type": "money" } } ]""", "type")]
    [InlineData("""[ { "op": "rename", "type": "table", "target": "contacts" } ]""", "op must be one of")]
    [InlineData("""[ { "op": "create", "type": "view", "value": { "apiName": "v", "displayName": "V", "tableId": "contacts", "columns": ["nope"] } } ]""", "has no field 'nope'")]
    public void A_bad_operation_is_refused_naming_the_operation(string json, string expected)
    {
        var act = () => Build(json, Build(Crm));

        act.Should().Throw<ContractValidationException>()
            .Which.Message.Should().StartWith("Operation 1 (").And.Contain(expected);
    }

    [Fact]
    public void A_later_operation_failing_names_its_position()
    {
        var act = () => Build("""
            [ { "op": "update", "type": "settings", "value": { "description": "CRM" } },
              { "op": "delete", "type": "table", "target": "nope" } ]
            """, Build(Crm));

        act.Should().Throw<ContractValidationException>().Which.Message.Should().StartWith("Operation 2 (delete table nope)");
    }

    [Fact]
    public void Deleting_a_field_removes_it_from_indexes_views_and_the_primary_field()
    {
        var config = Build("""
            [ { "op": "delete", "type": "field", "target": "contacts.email" },
              { "op": "delete", "type": "field", "target": "contacts.status" } ]
            """, Build(Crm));

        var contacts = config.Tables.Single(t => t.ApiName == "contacts");
        contacts.Fields.Should().BeEmpty();
        contacts.PrimaryFieldId.Should().BeNull();
        contacts.Indexes.Should().BeEmpty("an index left with no fields goes too");
        config.Views.Single().Columns.Should().Equal(config.Relationships.Single().Id);
        config.Views.Single().Sort.Should().BeEmpty();
    }

    [Fact]
    public void Deleting_a_table_takes_its_views_and_every_relationship_touching_it()
    {
        var config = Build("""[ { "op": "delete", "type": "table", "target": "companies" } ]""", Build(Crm));

        config.Relationships.Should().BeEmpty();
        config.Views.Should().ContainSingle("the view belongs to contacts, which stays");
        config.Views.Single().Columns.Should().HaveCount(1, "the lookup to companies went with the relationship");
        Errors(config).Should().BeEmpty();
    }

    [Fact]
    public void A_caller_supplied_id_is_kept_and_a_reused_one_refused()
    {
        var id = Guid.NewGuid();
        var config = Build($$"""[ { "op": "create", "type": "choiceSet", "value": { "id": "{{id}}", "apiName": "s", "displayName": "S", "options": [ { "value": "a", "label": "A" } ] } } ]""");
        config.ChoiceSets.Single().Id.Should().Be(id);

        var again = () => Build($$"""[ { "op": "create", "type": "table", "value": { "id": "{{id}}", "apiName": "t", "displayName": "T" } } ]""", config);
        again.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("already in use");
    }

    // ------------------------------------------------------------------ bounded work

    private static string BigTable(string apiName, int fields) =>
        $$"""[ { "op": "create", "type": "table", "value": { "apiName": "{{apiName}}", "displayName": "Big", "fields": [ {{string.Join(",",
            Enumerable.Range(0, fields).Select(i => $$"""{ "apiName": "f{{i}}", "displayName": "Field {{i}}" }"""))}} ] } } ]""";

    [Fact]
    public void Assigning_thousands_of_ids_in_one_operation_is_linear()
    {
        // Each new id used to be checked by walking the whole document: 5,000 inline fields took
        // 12.5 million node visits. A regression here is a request that pins a CPU.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var config = Build(BigTable("big", 5_000));
        watch.Stop();

        config.Tables.Single().Fields.Should().HaveCount(5_000);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_change_set_or_a_document_beyond_the_size_cap_is_refused()
    {
        var tooBig = () => Build(BigTable("big", 25_000));
        tooBig.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("at most");

        // Each half fits; together they would not.
        var half = Build(BigTable("first", 15_000));
        var both = () => Build(BigTable("second", 15_000), half);
        both.Should().Throw<ContractValidationException>().Which.Message.Should().Contain("would exceed");
    }

    // ------------------------------------------------------------------ canonical form and hash

    [Fact]
    public void The_hash_ignores_key_order_but_not_content()
    {
        var config = Build(Crm);
        var shuffled = ConfigJson.Parse(Reversed(ConfigJson.ToNode(config))!.ToJsonString());

        ConfigJson.Hash(shuffled).Should().Be(ConfigJson.Hash(config));
        ConfigJson.Canonical(shuffled).Should().Be(ConfigJson.Canonical(config));
        ConfigJson.Hash(config with { Settings = new AppSettings { Description = "x" } }).Should().NotBe(ConfigJson.Hash(config));
        ConfigJson.Hash(new AppConfig()).Should().Be(ConfigJson.EmptyHash).And.MatchRegex("^[0-9a-f]{64}$");
    }

    private static JsonNode? Reversed(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Reverse().Select(p => KeyValuePair.Create(p.Key, Reversed(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Reversed).ToArray()),
        _ => node?.DeepClone(),
    };

    // ------------------------------------------------------------------ diff

    [Fact]
    public void The_diff_lists_creates_updates_and_deletes_by_path_in_a_stable_order()
    {
        var before = Build(Crm);
        var after = Build("""
            [ { "op": "create", "type": "field", "target": "companies", "value": { "apiName": "revenue", "displayName": "Revenue", "type": "decimal" } },
              { "op": "update", "type": "table", "target": "companies", "value": { "displayName": "Organisation" } },
              { "op": "delete", "type": "view", "target": "contacts.active" } ]
            """, before);

        var changes = ConfigDiff.Between(before, after);

        changes.Select(c => $"{c.Op} {c.ResourceType} {c.Path}").Should().Equal(
            "update table companies",
            "create field companies.revenue",
            "delete view contacts.active");
        changes.Single(c => c.Op == "delete").Destructive.Should().BeTrue();
        changes.Where(c => c.Op != "delete").Should().OnlyContain(c => !c.Destructive);
        ConfigDiff.Between(after, after).Should().BeEmpty();
    }

    [Fact]
    public void Reordering_fields_is_a_change_to_the_table()
    {
        var before = Build(Crm);
        var contacts = before.Tables.Single(t => t.ApiName == "contacts");
        var after = before with
        {
            Tables = before.Tables.Select(t => t.Id == contacts.Id ? t with { Fields = t.Fields.Reverse().ToList() } : t).ToList(),
        };

        ConfigDiff.Between(before, after).Should().ContainSingle()
            .Which.Should().Match<ConfigChange>(c => c.Op == "update" && c.Path == "contacts");
    }

    [Theory]
    [InlineData("""{ "type": "text" }""", true)]       // email -> text: a type change
    [InlineData("""{ "required": true }""", true)]     // a new constraint on rows that exist
    [InlineData("""{ "displayName": "Mail" }""", false)]
    public void Tightening_a_field_is_destructive(string patch, bool destructive)
    {
        var before = Build(Crm);
        var after = Build($$"""[ { "op": "update", "type": "field", "target": "contacts.email", "value": {{patch}} } ]""", before);

        ConfigDiff.Between(before, after).Single().Destructive.Should().Be(destructive);
    }

    [Fact]
    public void A_new_required_field_is_destructive_only_on_a_table_that_already_existed()
    {
        var before = Build(Crm);
        var after = Build("""
            [ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "phone", "displayName": "Phone", "required": true } },
              { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
                  "fields": [ { "apiName": "title", "displayName": "Title", "required": true } ] } } ]
            """, before);

        var changes = ConfigDiff.Between(before, after);
        changes.Single(c => c.Path == "contacts.phone").Destructive.Should().BeTrue();
        changes.Single(c => c.Path == "deals.title").Destructive.Should().BeFalse();
        changes.Should().NotContain(c => c.ResourceType == "table" && c.Op == "update",
            "adding a field is the field's change, not its table's");
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("""[ { "op": "create", "type": "table", "value": { "apiName": "contacts", "displayName": "Again" } } ]""", "duplicate-api-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "email", "displayName": "Again" } } ]""", "duplicate-api-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "companies", "value": { "apiName": "contacts", "displayName": "Clash with the inverse" } } ]""", "duplicate-api-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "created_at", "displayName": "C" } } ]""", "reserved-api-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "Bad-Name", "displayName": "B" } } ]""", "invalid-api-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "x", "displayName": "" } } ]""", "invalid-display-name")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "kind", "displayName": "K", "type": "choice" } } ]""", "choice-set-required")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "n", "displayName": "N", "type": "integer", "default": 1.5 } } ]""", "invalid-default")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "n", "displayName": "N", "type": "integer", "minimum": 5, "maximum": 1 } } ]""", "invalid-bounds")]
    [InlineData("""[ { "op": "create", "type": "field", "target": "contacts", "value": { "apiName": "n", "displayName": "N", "type": "json", "unique": true } } ]""", "invalid-unique")]
    [InlineData("""[ { "op": "update", "type": "field", "target": "contacts.status", "value": { "default": "archived" } } ]""", "invalid-default")]
    [InlineData("""[ { "op": "create", "type": "relationship", "value": { "apiName": "tags", "kind": "manyToMany", "sourceTableId": "contacts", "targetTableId": "companies" } } ]""", "invalid-relationship")]
    [InlineData("""[ { "op": "create", "type": "relationship", "value": { "apiName": "x", "sourceTableId": "contacts", "targetTableId": "1b8f4f5e-0000-4000-8000-000000000000" } } ]""", "unknown-table")]
    [InlineData("""[ { "op": "create", "type": "relationship", "value": { "apiName": "owner", "sourceTableId": "contacts", "targetTableId": "companies", "required": true, "onDelete": "setNull" } } ]""", "invalid-relationship")]
    [InlineData("""[ { "op": "create", "type": "index", "target": "contacts", "value": { "apiName": "wide", "fieldIds": ["email", "status", "email"] } } ]""", "invalid-index")]
    [InlineData("""[ { "op": "update", "type": "table", "target": "contacts", "value": { "primaryFieldId": "1b8f4f5e-0000-4000-8000-000000000000" } } ]""", "invalid-primary-field")]
    [InlineData("""[ { "op": "create", "type": "view", "value": { "apiName": "active", "displayName": "Dup", "tableId": "contacts" } } ]""", "duplicate-api-name")]
    public void Invalid_configurations_are_reported(string change, string code)
    {
        Errors(Build(change, Build(Crm))).Should().Contain(code);
    }

    [Fact]
    public void Every_issue_is_reported_at_once()
    {
        var config = Build("""
            [ { "op": "create", "type": "table", "value": { "apiName": "Bad", "displayName": "",
                "fields": [ { "apiName": "id", "displayName": "Id" }, { "apiName": "id", "displayName": "Again" } ] } } ]
            """);

        Errors(config).Should().Contain(["invalid-api-name", "invalid-display-name", "reserved-api-name", "duplicate-api-name"]);
    }

    [Fact]
    public void Published_api_names_are_fixed_and_types_only_widen()
    {
        var published = Build(Crm);

        Errors(Build("""[ { "op": "update", "type": "table", "target": "contacts", "value": { "apiName": "people" } } ]""", published), published)
            .Should().Contain("immutable-api-name");
        Errors(Build("""[ { "op": "update", "type": "field", "target": "contacts.email", "value": { "type": "integer" } } ]""", published), published)
            .Should().Contain("incompatible-type-change");
        Errors(Build("""[ { "op": "update", "type": "field", "target": "contacts.email", "value": { "type": "text" } } ]""", published), published)
            .Should().BeEmpty("every email is a valid text");
        Errors(Build("""[ { "op": "update", "type": "relationship", "target": "contacts.company", "value": { "kind": "manyToMany" } } ]""", published), published)
            .Should().Contain("incompatible-relationship-change");

        // Unpublished resources are free to change.
        var draft = Build("""[ { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal" } } ]""", published);
        Errors(Build("""[ { "op": "update", "type": "table", "target": "deals", "value": { "apiName": "opportunities" } } ]""", draft), published)
            .Should().BeEmpty();
    }

    [Fact]
    public void Removing_a_published_choice_is_a_warning_not_an_error()
    {
        var published = Build(Crm);
        var draft = Build("""
            [ { "op": "update", "type": "field", "target": "contacts.status", "value": { "default": null } },
              { "op": "update", "type": "choiceSet", "target": "status", "value": { "options": [ { "value": "active", "label": "Active" } ] } } ]
            """, published);

        var issues = ConfigValidator.Validate(draft, published);
        issues.Should().ContainSingle(i => i.Code == "choice-removed").Which.Severity.Should().Be(IssueSeverity.Warning);
        issues.Should().NotContain(i => i.Severity == IssueSeverity.Error);
    }
}
