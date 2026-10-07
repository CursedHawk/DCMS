using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Whether a configuration means something publishable. Pure, and complete in one pass: it
/// reports every issue rather than the first, because the caller — often the assistant — fixes
/// them in one change set. Rules that only make sense against what is live (api names fixed
/// once published, type changes limited to widenings) apply when <c>published</c> is given.
/// </summary>
public static partial class ConfigValidator
{
    public const int MaxTables = 100;
    public const int MaxFieldsPerTable = 200;
    public const int MaxRelationships = 300;
    public const int MaxViews = 300;
    public const int MaxChoiceSets = 100;
    public const int MaxChoiceOptions = 500;
    public const int MaxIndexFields = 5;
    public const int MaxDisplayName = 120;
    public const int MaxTextLength = 100_000;
    public const int MaxStepInputChars = 16_000;

    /// <summary>Names every record has, which a field or relationship may not take.</summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "created_at", "updated_at", "created_by", "updated_by", "version", "owner_visitor_id", "deleted_at",
    };

    /// <summary>A published field may change type only along these edges: every old value is still valid.</summary>
    private static readonly HashSet<(FieldType From, FieldType To)> Widenings =
    [
        (FieldType.Integer, FieldType.Decimal),
        (FieldType.Text, FieldType.LongText),
        (FieldType.Email, FieldType.Text),
        (FieldType.Url, FieldType.Text),
        (FieldType.Email, FieldType.LongText),
        (FieldType.Url, FieldType.LongText),
        (FieldType.Choice, FieldType.Text),
        (FieldType.Date, FieldType.DateTime),
    ];

    private static readonly FieldType[] TextTypes = [FieldType.Text, FieldType.LongText, FieldType.Email, FieldType.Url];
    private static readonly FieldType[] NotUnique = [FieldType.LongText, FieldType.Json, FieldType.MultiChoice, FieldType.Boolean];
    private static readonly FieldType[] NotPrimary = [FieldType.LongText, FieldType.Json, FieldType.MultiChoice, FieldType.Boolean, FieldType.Media];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex ApiNamePattern();

    /// <param name="actions">The action keys flows may use; the built-in ones when null (see <see cref="Automation.ActionCatalog.WithProvidersAsync"/>).</param>
    public static IReadOnlyList<ConfigIssue> Validate(AppConfig config, AppConfig? published = null, IReadOnlySet<string>? actions = null)
    {
        var issues = new List<ConfigIssue>();
        void Error(string code, string path, string message) => issues.Add(new(IssueSeverity.Error, code, path, message));
        void Warn(string code, string path, string message) => issues.Add(new(IssueSeverity.Warning, code, path, message));

        var tables = config.Tables.DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var choiceSets = config.ChoiceSets.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        string TableName(Guid id) => tables.TryGetValue(id, out var t) ? t.ApiName : id.ToString();

        CheckIds(config, Error);
        Limit(config.Tables.Count, MaxTables, "tables", Error);
        Limit(config.Relationships.Count, MaxRelationships, "relationships", Error);
        Limit(config.Views.Count, MaxViews, "views", Error);
        Limit(config.ChoiceSets.Count, MaxChoiceSets, "choiceSets", Error);

        // ---- choice sets
        foreach (var set in config.ChoiceSets)
        {
            var path = $"choiceSets.{set.ApiName}";
            Name(set.ApiName, path, Error);
            Label(set.DisplayName, path, Error);
            Limit(set.Options.Count, MaxChoiceOptions, path, Error);
            if (set.Options.Count == 0)
            {
                Error("empty-choice-set", path, "A choice set needs at least one option.");
            }
            foreach (var dup in set.Options.GroupBy(o => o.Value).Where(g => g.Count() > 1))
            {
                Error("duplicate-choice-value", path, $"Option value '{dup.Key}' appears more than once.");
            }
            if (set.Options.Any(o => string.IsNullOrWhiteSpace(o.Value) || string.IsNullOrWhiteSpace(o.Label)))
            {
                Error("invalid-choice-option", path, "Every option needs a value and a label.");
            }
        }
        Duplicates(config.ChoiceSets.Select(c => c.ApiName), "choiceSets", "choice set", Error);

        // ---- tables, fields, indexes
        Duplicates(config.Tables.Select(t => t.ApiName), "tables", "table", Error);
        foreach (var table in config.Tables)
        {
            var path = table.ApiName;
            Name(table.ApiName, path, Error);
            Label(table.DisplayName, path, Error);
            Limit(table.Fields.Count, MaxFieldsPerTable, $"{path}.fields", Error);

            foreach (var field in table.Fields)
            {
                CheckField(table, field, choiceSets, Error);
            }

            if (table.PrimaryFieldId is { } primary
                && table.Fields.FirstOrDefault(f => f.Id == primary) is var pf
                && (pf is null || NotPrimary.Contains(pf.Type)))
            {
                Error("invalid-primary-field", path,
                    pf is null ? "The primary field is not a field of this table." : $"A {Camel(pf.Type)} field cannot name records; pick a short text, number or date field.");
            }
            if (table.PrimaryFieldId is null && table.Fields.Count > 0)
            {
                Warn("no-primary-field", path, "No primary field: records will be shown by id in lists and lookups.");
            }
            foreach (var field in table.Fields.Where(f => f.Type is FieldType.Text or FieldType.Integer))
            {
                if (ReferencedTable(config, field.ApiName) is { } target)
                {
                    Warn("reference-as-text", $"{path}.{field.ApiName}",
                        $"{table.ApiName}.{field.ApiName} looks like a reference to {target.ApiName}. Make it a relationship instead "
                        + $"(sourceTableId {table.ApiName}, targetTableId {target.ApiName}): values are then checked, deletes are handled, and the related record can be fetched.");
                }
            }

            Duplicates(table.Indexes.Select(i => i.ApiName), $"{path}.indexes", "index", Error);
            foreach (var index in table.Indexes)
            {
                var indexPath = $"{path}.{index.ApiName}";
                Name(index.ApiName, indexPath, Error);
                if (index.FieldIds.Count is 0 or > MaxIndexFields)
                {
                    Error("invalid-index", indexPath, $"An index covers 1 to {MaxIndexFields} fields.");
                }
                if (index.FieldIds.Distinct().Count() != index.FieldIds.Count)
                {
                    Error("invalid-index", indexPath, "An index names a field twice.");
                }
                foreach (var fieldId in index.FieldIds)
                {
                    var field = table.Fields.FirstOrDefault(f => f.Id == fieldId);
                    if (field is null)
                    {
                        Error("invalid-index", indexPath, $"Field {fieldId} is not a field of {table.ApiName}.");
                    }
                    else if (index.Unique && NotUnique.Contains(field.Type))
                    {
                        Error("invalid-index", indexPath, $"A {Camel(field.Type)} field cannot be part of a unique index.");
                    }
                }
            }
        }

        // ---- relationships
        foreach (var relationship in config.Relationships)
        {
            var path = $"{TableName(relationship.SourceTableId)}.{relationship.ApiName}";
            Name(relationship.ApiName, path, Error);
            if (!tables.ContainsKey(relationship.SourceTableId))
            {
                Error("unknown-table", path, $"Source table {relationship.SourceTableId} does not exist.");
            }
            if (!tables.ContainsKey(relationship.TargetTableId))
            {
                Error("unknown-table", path, $"Target table {relationship.TargetTableId} does not exist.");
            }
            if (relationship.InverseApiName is { } inverse)
            {
                Name(inverse, $"{TableName(relationship.TargetTableId)}.{inverse}", Error);
            }
            if (relationship.Kind == RelationshipKind.ManyToMany)
            {
                if (relationship.InverseApiName is null)
                {
                    Error("invalid-relationship", path, "A many-to-many relationship needs an inverseApiName: both tables list each other.");
                }
                if (relationship.Required)
                {
                    Error("invalid-relationship", path, "A many-to-many relationship cannot be required.");
                }
            }
            if (relationship.Required && relationship.OnDelete == DeleteBehavior.SetNull)
            {
                Error("invalid-relationship", path, "A required lookup cannot be cleared when its target is deleted; use restrict or cascade.");
            }
        }

        // ---- one name space per table: fields, lookups, and what other tables call it from their side
        foreach (var table in config.Tables)
        {
            var names = table.Fields.Select(f => f.ApiName)
                .Concat(config.Relationships.Where(r => r.SourceTableId == table.Id).Select(r => r.ApiName))
                .Concat(config.Relationships.Where(r => r.TargetTableId == table.Id && r.InverseApiName is not null).Select(r => r.InverseApiName!))
                .ToList();
            Duplicates(names, table.ApiName, "field or relationship", Error);
            foreach (var reserved in names.Where(ReservedNames.Contains).Distinct())
            {
                Error("reserved-api-name", $"{table.ApiName}.{reserved}", $"'{reserved}' is a name every record already has.");
            }
        }

        // ---- views
        foreach (var view in config.Views)
        {
            var path = $"{TableName(view.TableId)}.{view.ApiName}";
            Name(view.ApiName, path, Error);
            Label(view.DisplayName, path, Error);
            if (!tables.TryGetValue(view.TableId, out var table))
            {
                Error("unknown-table", path, $"Table {view.TableId} does not exist.");
                continue;
            }
            var lookups = config.Relationships
                .Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany)
                .Select(r => r.Id).ToHashSet();
            foreach (var column in view.Columns.Where(c => table.Fields.All(f => f.Id != c) && !lookups.Contains(c)))
            {
                Error("invalid-view", path, $"Column {column} is not a field or lookup of {table.ApiName}.");
            }
            foreach (var sort in view.Sort)
            {
                var field = table.Fields.FirstOrDefault(f => f.Id == sort.FieldId);
                if (field is null)
                {
                    Error("invalid-view", path, $"Sort field {sort.FieldId} is not a field of {table.ApiName}.");
                }
                else if (!field.Sortable)
                {
                    Warn("unsortable-field", path, $"{table.ApiName}.{field.ApiName} is not marked sortable; the public API will not offer this order.");
                }
            }
        }
        foreach (var group in config.Views.GroupBy(v => v.TableId))
        {
            Duplicates(group.Select(v => v.ApiName), $"{TableName(group.Key)}.views", "view", Error);
            if (group.Count(v => v.IsDefault) > 1)
            {
                Error("invalid-view", $"{TableName(group.Key)}.views", "A table has one default view.");
            }
        }

        CheckFlows(config, tables, actions ?? Automation.ActionCatalog.Keys, Error);

        if (published is not null)
        {
            CheckAgainstPublished(config, published, Error, Warn);
        }

        return issues
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>What a flow's conditions and inputs may read.</summary>
    public static readonly IReadOnlySet<string> FlowVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "event", "row", "previous", "changedFields", "input", "steps", "run",
    };

    private static readonly string[] RowEvents = ["row.created", "row.updated", "row.deleted"];
    private static readonly string[] RelationEvents = ["relation.created", "relation.deleted"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,40}$")]
    private static partial Regex StepIdPattern();

    [GeneratedRegex("^flow\\.event\\.[a-z][a-z0-9_.-]{0,62}$")]
    private static partial Regex FlowEventPattern();

    /// <summary>The table a <c>…_id</c> field seems to point at: <c>company_id</c> or <c>primary_company_id</c> → <c>companies</c>.</summary>
    private static TableDef? ReferencedTable(AppConfig config, string fieldName)
    {
        if (!fieldName.EndsWith("_id", StringComparison.Ordinal) || fieldName.Length < 4)
        {
            return null;
        }
        var stem = fieldName[..^3];
        var last = stem[(stem.LastIndexOf('_') + 1)..];
        string[] Forms(string s) => [s, s + "s", s + "es", s.EndsWith('y') ? s[..^1] + "ies" : s];
        var names = Forms(stem).Concat(Forms(last)).ToHashSet(StringComparer.Ordinal);
        return config.Tables.FirstOrDefault(t => names.Contains(t.ApiName));
    }

    private static void CheckFlows(AppConfig config, Dictionary<Guid, TableDef> tables, IReadOnlySet<string> actions, Action<string, string, string> error)
    {
        Duplicates(config.Flows.Select(f => f.ApiName), "flows", "flow", error);
        var relationships = config.Relationships.DistinctBy(r => r.Id).ToDictionary(r => r.Id);
        foreach (var flow in config.Flows)
        {
            var path = $"flows.{flow.ApiName}";
            Name(flow.ApiName, path, error);
            Label(flow.DisplayName, path, error);

            var trigger = flow.Trigger;
            var isRow = RowEvents.Contains(trigger.Event);
            var isRelation = RelationEvents.Contains(trigger.Event);
            var known = isRow || isRelation || trigger.Event is "revision.published" or "schedule" or "manual"
                        || FlowEventPattern().IsMatch(trigger.Event) || Automation.PlatformEventBridge.Events.Contains(trigger.Event);
            if (!known)
            {
                error("invalid-trigger", path, $"'{trigger.Event}' is not a trigger: use row.created/updated/deleted, relation.created/deleted, revision.published, flow.event.<name>, visitor.registered, form.submitted, schedule or manual.");
            }
            if (isRow != (trigger.TableId is not null))
            {
                error("invalid-trigger", path, isRow ? "A row trigger names its table." : "Only a row trigger names a table.");
            }
            else if (trigger.TableId is { } tableId && !tables.ContainsKey(tableId))
            {
                error("unknown-table", path, $"The trigger's table {tableId} does not exist.");
            }
            if (trigger.RelationshipId is { } relationshipId
                && (!isRelation || !relationships.TryGetValue(relationshipId, out var link) || link.Kind != RelationshipKind.ManyToMany))
            {
                error("invalid-trigger", path, "Only a relation trigger names a relationship, and it must be a many-to-many one.");
            }
            if (trigger.ChangedFields.Count > 0)
            {
                var members = trigger.TableId is { } t && tables.TryGetValue(t, out var table)
                    ? table.Fields.Select(f => f.Id).Concat(config.Relationships.Where(r => r.SourceTableId == t).Select(r => r.Id)).ToHashSet()
                    : [];
                if (trigger.Event != "row.updated" || trigger.ChangedFields.Any(f => !members.Contains(f)))
                {
                    error("invalid-trigger", path, "changedFields applies to row.updated and names fields or lookups of the trigger's table.");
                }
            }
            if ((trigger.Event == "schedule") != (trigger.EveryMinutes is not null)
                || trigger.EveryMinutes is < Automation.AutomationLimits.MinScheduleMinutes or > Automation.AutomationLimits.MaxScheduleMinutes)
            {
                error("invalid-trigger", path,
                    $"A schedule trigger runs every {Automation.AutomationLimits.MinScheduleMinutes} to {Automation.AutomationLimits.MaxScheduleMinutes} minutes; other triggers have no interval.");
            }

            Expression(flow.Condition, $"{path}.condition", error);
            if (flow.Steps.Count is 0 or > Automation.AutomationLimits.MaxSteps)
            {
                error("invalid-flow", path, $"A flow has 1 to {Automation.AutomationLimits.MaxSteps} steps.");
            }
            foreach (var dup in flow.Steps.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            {
                error("duplicate-step", path, $"Step id '{dup.Key}' is used more than once.");
            }
            foreach (var step in flow.Steps)
            {
                var stepPath = $"{path}.{step.Id}";
                if (!StepIdPattern().IsMatch(step.Id))
                {
                    error("invalid-step", stepPath, $"'{step.Id}' is not a valid step id: lowercase snake_case, so later steps can read steps.{step.Id}.");
                }
                if (!actions.Contains(step.Action))
                {
                    error("unknown-action", stepPath, $"'{step.Action}' is not an action; actions are named with their major version, e.g. records.create@1.");
                }
                Expression(step.Condition, $"{stepPath}.condition", error);
                if (step.Input.ToJsonString().Length > MaxStepInputChars)
                {
                    error("invalid-step", stepPath, $"A step's input is at most {MaxStepInputChars:N0} characters.");
                }
                try
                {
                    foreach (var hole in Automation.Expressions.TemplateExpressions(step.Input))
                    {
                        Expression(hole, $"{stepPath}.input", error);
                    }
                }
                catch (Automation.ExpressionException e)
                {
                    error("invalid-expression", $"{stepPath}.input", e.Message);
                }
                if (step.Action == "flow.invoke@1" && step.Input["flow"] is JsonValue target && target.TryGetValue<string>(out var name)
                    && !name.Contains("{{", StringComparison.Ordinal)
                    && config.Flows.FirstOrDefault(f => f.ApiName == name) is not { Trigger.Event: "manual" })
                {
                    error("invalid-step", stepPath, $"flow.invoke starts a flow with a manual trigger; '{name}' is not one.");
                }
            }
        }
    }

    private static void Expression(string? source, string path, Action<string, string, string> error)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        try
        {
            var unknown = Automation.Expressions.Variables(Automation.Expressions.Parse(source)).Where(v => !FlowVariables.Contains(v)).Distinct().ToList();
            if (unknown.Count > 0)
            {
                error("invalid-expression", path, $"Unknown name(s) {string.Join(", ", unknown)}; an expression may read {string.Join(", ", FlowVariables)}.");
            }
        }
        catch (Automation.ExpressionException e)
        {
            error("invalid-expression", path, e.Message);
        }
    }

    private static void CheckField(TableDef table, FieldDef field, Dictionary<Guid, ChoiceSetDef> choiceSets, Action<string, string, string> error)
    {
        var path = $"{table.ApiName}.{field.ApiName}";
        Name(field.ApiName, path, error);
        Label(field.DisplayName, path, error);

        var isChoice = field.Type is FieldType.Choice or FieldType.MultiChoice;
        if (isChoice && (field.ChoiceSetId is null || !choiceSets.ContainsKey(field.ChoiceSetId.Value)))
        {
            error(field.ChoiceSetId is null ? "choice-set-required" : "unknown-choice-set", path,
                "A choice field needs the choice set its values come from.");
        }
        if (!isChoice && field.ChoiceSetId is not null)
        {
            error("choice-set-not-allowed", path, $"Only choice fields take a choice set, not {Camel(field.Type)}.");
        }
        if (field.MaxLength is { } max && (!TextTypes.Contains(field.Type) || max < 1 || max > MaxTextLength))
        {
            error("invalid-max-length", path, $"maxLength applies to text fields and lies between 1 and {MaxTextLength}.");
        }
        var numeric = field.Type is FieldType.Integer or FieldType.Decimal;
        if ((field.Minimum is not null || field.Maximum is not null) && !numeric)
        {
            error("invalid-bounds", path, "minimum and maximum apply to number fields.");
        }
        if (field.Minimum > field.Maximum)
        {
            error("invalid-bounds", path, "minimum is greater than maximum.");
        }
        if (field.Unique && NotUnique.Contains(field.Type))
        {
            error("invalid-unique", path, $"A {Camel(field.Type)} field cannot be unique.");
        }
        if (field.Default is { } value && DefaultProblem(field, value, choiceSets) is { } problem)
        {
            error("invalid-default", path, problem);
        }
    }

    /// <summary>Why the literal does not fit the field, or null when it does.</summary>
    private static string? DefaultProblem(FieldDef field, JsonNode value, Dictionary<Guid, ChoiceSetDef> choiceSets)
    {
        string? text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        HashSet<string> Options() => field.ChoiceSetId is { } id && choiceSets.TryGetValue(id, out var set)
            ? set.Options.Select(o => o.Value).ToHashSet()
            : [];

        var fits = field.Type switch
        {
            FieldType.Text or FieldType.LongText or FieldType.Email or FieldType.Url =>
                text is not null && (field.MaxLength is null || text.Length <= field.MaxLength),
            FieldType.Integer => value is JsonValue n && n.TryGetValue<decimal>(out var i) && i == decimal.Truncate(i) && InBounds(field, i),
            FieldType.Decimal => value is JsonValue d && d.TryGetValue<decimal>(out var x) && InBounds(field, x),
            FieldType.Boolean => value is JsonValue b && b.TryGetValue<bool>(out _),
            FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.DateTime => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.Choice => text is not null && Options().Contains(text),
            FieldType.MultiChoice => value is JsonArray items && items.All(o => o is JsonValue ov && ov.TryGetValue<string>(out var c) && Options().Contains(c)),
            FieldType.Media => Guid.TryParse(text, out _),
            _ => true,
        };
        return fits ? null : $"The default is not a valid {Camel(field.Type)} value for this field.";
    }

    private static bool InBounds(FieldDef field, decimal value) =>
        (field.Minimum is null || value >= field.Minimum) && (field.Maximum is null || value <= field.Maximum);

    private static void CheckAgainstPublished(AppConfig config, AppConfig published,
        Action<string, string, string> error, Action<string, string, string> warn)
    {
        var liveTables = published.Tables.DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var liveFields = published.Tables.SelectMany(t => t.Fields).DistinctBy(f => f.Id).ToDictionary(f => f.Id);
        var liveRelationships = published.Relationships.DistinctBy(r => r.Id).ToDictionary(r => r.Id);
        var liveChoices = published.ChoiceSets.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        const string Fixed = "Published api names are part of the generated API and cannot change; add a new one and deprecate this.";

        foreach (var table in config.Tables)
        {
            if (liveTables.TryGetValue(table.Id, out var live) && live.ApiName != table.ApiName)
            {
                error("immutable-api-name", table.ApiName, $"Was '{live.ApiName}'. {Fixed}");
            }
            foreach (var field in table.Fields)
            {
                if (!liveFields.TryGetValue(field.Id, out var was))
                {
                    continue;
                }
                var path = $"{table.ApiName}.{field.ApiName}";
                if (was.ApiName != field.ApiName)
                {
                    error("immutable-api-name", path, $"Was '{was.ApiName}'. {Fixed}");
                }
                if (was.Type != field.Type && !Widenings.Contains((was.Type, field.Type)))
                {
                    error("incompatible-type-change", path,
                        $"A published {Camel(was.Type)} field cannot become {Camel(field.Type)}: existing values would not fit. Add a new field instead.");
                }
            }
        }
        foreach (var relationship in config.Relationships)
        {
            if (!liveRelationships.TryGetValue(relationship.Id, out var was))
            {
                continue;
            }
            var path = relationship.ApiName;
            if (was.ApiName != relationship.ApiName || was.InverseApiName != relationship.InverseApiName)
            {
                error("immutable-api-name", path, Fixed);
            }
            if (was.Kind != relationship.Kind || was.SourceTableId != relationship.SourceTableId || was.TargetTableId != relationship.TargetTableId)
            {
                error("incompatible-relationship-change", path,
                    "A published relationship keeps its kind and tables; add a new relationship instead.");
            }
        }
        foreach (var set in config.ChoiceSets)
        {
            if (liveChoices.TryGetValue(set.Id, out var was)
                && was.Options.Select(o => o.Value).Except(set.Options.Select(o => o.Value)).ToList() is { Count: > 0 } removed)
            {
                warn("choice-removed", $"choiceSets.{set.ApiName}",
                    $"Removed option(s) {string.Join(", ", removed)}: records that hold them keep the value but it no longer validates.");
            }
        }
    }

    private static void CheckIds(AppConfig config, Action<string, string, string> error)
    {
        var ids = config.Tables.Select(t => (t.Id, t.ApiName))
            .Concat(config.Tables.SelectMany(t => t.Fields.Select(f => (f.Id, $"{t.ApiName}.{f.ApiName}"))))
            .Concat(config.Tables.SelectMany(t => t.Indexes.Select(i => (i.Id, $"{t.ApiName}.{i.ApiName}"))))
            .Concat(config.Relationships.Select(r => (r.Id, r.ApiName)))
            .Concat(config.ChoiceSets.Select(c => (c.Id, c.ApiName)))
            .Concat(config.Views.Select(v => (v.Id, v.ApiName)));
        foreach (var (id, path) in ids)
        {
            if (id == Guid.Empty)
            {
                error("missing-id", path, "Every resource needs an id.");
            }
        }
        foreach (var dup in ids.Where(x => x.Id != Guid.Empty).GroupBy(x => x.Id).Where(g => g.Count() > 1))
        {
            error("duplicate-id", dup.First().Item2, $"Id {dup.Key} is used by {string.Join(", ", dup.Select(x => x.Item2))}.");
        }
    }

    private static void Name(string apiName, string path, Action<string, string, string> error)
    {
        if (!ApiNamePattern().IsMatch(apiName))
        {
            error("invalid-api-name", path,
                $"'{apiName}' is not a valid api name: lowercase snake_case, starting with a letter, at most 63 characters.");
        }
    }

    private static void Label(string displayName, string path, Action<string, string, string> error)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > MaxDisplayName)
        {
            error("invalid-display-name", path, $"A display name is required and at most {MaxDisplayName} characters.");
        }
    }

    private static void Limit(int count, int max, string path, Action<string, string, string> error)
    {
        if (count > max)
        {
            error("limit-exceeded", path, $"At most {max} allowed; this has {count}.");
        }
    }

    private static void Duplicates(IEnumerable<string> names, string path, string what, Action<string, string, string> error)
    {
        foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            error("duplicate-api-name", $"{path}.{dup.Key}", $"More than one {what} is called '{dup.Key}'.");
        }
    }

    private static string Camel(FieldType type) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}
