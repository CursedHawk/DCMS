using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.UnitTests.Audit;

/// <summary>
/// Exercises the EF diff without a database.
///
/// <para>Change tracking is entirely client-side, so a context that never opens a connection
/// tracks adds, edits and deletes exactly as a real one does. That keeps these tests runnable
/// on any machine — the properties under test are about what the capture <i>says</i>, not
/// about what Postgres does with it.</para>
/// </summary>
public sealed class AuditChangeCaptureTests : IDisposable
{
    private readonly CaptureTestContext _db = new();
    private readonly AuditScope _scope = new();
    private readonly RecordingSink _sink = new();
    private readonly AuditRecorder _recorder;
    private readonly AuditChangeCapture _capture;

    public AuditChangeCaptureTests()
    {
        _recorder = new AuditRecorder(
            _scope,
            new AuditServiceIdentity("tests"),
            _sink,
            new TestActor(),
            new TestTenant(),
            new SystemClock(),
            new AuditMetrics(new TestMeterFactory()),
            NullLogger<AuditRecorder>.Instance);

        var redactor = new AuditRedactor().Allow(
            typeof(Widget), "widget", nameof(Widget.Name), deny: [nameof(Widget.Secret)]);

        _capture = new AuditChangeCapture(redactor, _recorder);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_insert_folds_into_the_entry_the_endpoint_declared()
    {
        var declared = Declare("widget.created", "widget");
        _db.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "Left rudder", Size = 3 });

        _capture.Capture(_db);

        _recorder.Pending.Should().ContainSingle("one action produces one record, diff included");
        declared.ResourceId.Should().NotBeNull("a create has no id in its route; the inserted row supplies it");
        declared.ResourceLabel.Should().Be("Left rudder");
        Field(declared, nameof(Widget.Name)).After.Should().Be("Left rudder");
        Field(declared, nameof(Widget.Size)).After.Should().Be(3);
        declared.RedactionVersion.Should().Be(AuditRedactor.Version);
    }

    [Fact]
    public void An_edit_records_both_sides_of_every_changed_field_and_no_others()
    {
        var widget = Tracked(new Widget { Id = Guid.NewGuid(), Name = "Before", Size = 1 });
        var declared = Declare("widget.updated", "widget", widget.Id.ToString());

        widget.Name = "After";

        _capture.Capture(_db);

        var change = Field(declared, nameof(Widget.Name));
        change.Before.Should().Be("Before");
        change.After.Should().Be("After");
        declared.Changes.Should().ContainSingle("Size did not change, so it is not part of the diff");
    }

    [Fact]
    public void A_delete_records_what_the_row_held()
    {
        var widget = Tracked(new Widget { Id = Guid.NewGuid(), Name = "Doomed", Size = 7 });
        var declared = Declare("widget.deleted", "widget", widget.Id.ToString());

        _db.Widgets.Remove(widget);

        _capture.Capture(_db);

        var change = Field(declared, nameof(Widget.Name));
        change.Before.Should().Be("Doomed", "after the delete there is nothing left to read this from");
        change.After.Should().BeNull();
    }

    [Fact]
    public void A_stub_attach_withholds_before_values_instead_of_inventing_them()
    {
        // db.Attach(new X { Id = id }) then IsModified = true: EF's originals are default(T),
        // not what the row held. A diff reading "Name: '' → 'new'" would be specific and wrong.
        var id = Guid.NewGuid();
        var stub = new Widget { Id = id };
        _db.Attach(stub);
        stub.Name = "Renamed";
        _db.Entry(stub).Property(w => w.Name).IsModified = true;

        var declared = Declare("widget.updated", "widget", id.ToString());

        _capture.Capture(_db);

        var change = Field(declared, nameof(Widget.Name));
        change.After.Should().Be("Renamed");
        change.Before.Should().BeNull();
        declared.Metadata.Should().ContainKey("diff_before");
        declared.Metadata!["diff_before"].Should().BeOfType<string>()
            .Which.Should().Contain("stub", "a reader must be able to tell 'was empty' from 'unknown'");
    }

    [Fact]
    public void A_loaded_entity_is_not_mistaken_for_a_stub()
    {
        var widget = Tracked(new Widget { Id = Guid.NewGuid(), Name = "Before", Size = 4 });
        var declared = Declare("widget.updated", "widget", widget.Id.ToString());

        widget.Name = "After";

        _capture.Capture(_db);

        Field(declared, nameof(Widget.Name)).Before.Should().Be("Before");
        (declared.Metadata?.ContainsKey("diff_before") ?? false).Should().BeFalse();
    }

    [Fact]
    public void Secrets_are_flagged_as_changed_without_being_shown()
    {
        var widget = Tracked(new Widget { Id = Guid.NewGuid(), Name = "n", Secret = "old" });
        var declared = Declare("widget.updated", "widget", widget.Id.ToString());

        widget.Secret = "new";

        _capture.Capture(_db);

        var change = Field(declared, nameof(Widget.Secret));
        change.Redacted.Should().BeTrue();
        change.Before.Should().BeNull();
        change.After.Should().BeNull();
    }

    [Fact]
    public void Other_entities_in_the_same_save_ride_along_rather_than_becoming_their_own_records()
    {
        var declared = Declare("widget.updated", "widget");
        _db.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "The widget" });
        _db.Tags.Add(new Tag { Id = Guid.NewGuid(), Label = "blue" });
        _db.Tags.Add(new Tag { Id = Guid.NewGuid(), Label = "heavy" });

        _capture.Capture(_db);

        _recorder.Pending.Should().ContainSingle("a role update is one record, not one per row it touched");
        var related = declared.Metadata!["related"].Should().BeAssignableTo<List<Dictionary<string, object?>>>().Subject;
        related.Should().HaveCount(2);
        related.Should().AllSatisfy(r => r["op"].Should().Be("created"));
    }

    [Fact]
    public void A_tag_is_default_deny_because_nobody_opted_it_in()
    {
        Declare("widget.updated", "widget");
        _db.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "w" });
        _db.Tags.Add(new Tag { Id = Guid.NewGuid(), Label = "confidential-project-name" });

        _capture.Capture(_db);

        var related = _recorder.Pending[0].Metadata!["related"]
            .Should().BeAssignableTo<List<Dictionary<string, object?>>>().Subject;
        var changes = related[0]["changes"].Should().BeAssignableTo<IReadOnlyList<AuditFieldChange>>().Subject;
        changes.Should().AllSatisfy(c => c.Redacted.Should().BeTrue());
        changes.Should().AllSatisfy(c => c.After.Should().BeNull());
    }

    [Fact]
    public void Work_nobody_named_gets_its_own_records()
    {
        _db.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "From a worker" });

        _capture.Capture(_db);

        _recorder.Pending.Should().ContainSingle();
        _recorder.Pending[0].Action.Should().Be("data.widget.created");
        _recorder.Pending[0].ResourceLabel.Should().Be("From a worker");
    }

    [Fact]
    public void A_handlers_own_entry_is_used_when_there_is_no_declaring_endpoint()
    {
        // SiteDeleter's shape: reached both from its endpoint and from a tenant purge, so it
        // records for itself. The diff must attach to that, not open a second record.
        var id = Guid.NewGuid();
        var own = _recorder.Record("site.deleted").For("widget", id);
        var widget = Tracked(new Widget { Id = id, Name = "Doomed" });
        _db.Widgets.Remove(widget);

        _capture.Capture(_db);

        _recorder.Pending.Should().ContainSingle();
        Field(own, nameof(Widget.Name)).Before.Should().Be("Doomed");
    }

    [Fact]
    public void A_save_too_large_to_read_is_summarised_by_type_and_count()
    {
        var declared = Declare("import.run", "widget");
        for (var i = 0; i < AuditChangeCapture.BulkThreshold + 5; i++)
        {
            _db.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = $"w{i}" });
        }

        _capture.Capture(_db);

        _recorder.Pending.Should().ContainSingle(
            "the chain is serialised per tenant; ten thousand records would make the writer the bottleneck");
        declared.Changes.Should().BeNull();
        declared.Metadata!["bulk_rows"].Should().Be(AuditChangeCapture.BulkThreshold + 5);
        declared.Metadata["bulk"].Should().BeAssignableTo<Dictionary<string, object?>>()
            .Which.Should().ContainKey("widget.created");
    }

    [Fact]
    public void Tables_marked_ignored_never_reach_the_log()
    {
        _db.Pings.Add(new Ping { Id = Guid.NewGuid(), Note = "page view #40122" });

        _capture.Capture(_db);

        _recorder.Pending.Should().BeEmpty("telemetry is written on every request; auditing it would bury the actions");
    }

    // ---- helpers ----

    private AuditEntry Declare(string action, string resourceType, string? resourceId = null)
    {
        var entry = _recorder.Record(action);
        entry.ResourceType = resourceType;
        entry.ResourceId = resourceId;
        _scope.Declared = entry;
        return entry;
    }

    private T Tracked<T>(T entity) where T : class
    {
        _db.Attach(entity);
        _db.Entry(entity).State = EntityState.Unchanged;
        return entity;
    }

    private static AuditFieldChange Field(AuditEntry entry, string name)
    {
        entry.Changes.Should().NotBeNull().And.Contain(c => c.Field == name, "the diff should mention {0}", name);
        return entry.Changes!.Single(c => c.Field == name);
    }

    private sealed class RecordingSink : IAuditSink
    {
        public ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestActor : ICurrentActor
    {
        public ActorKind Kind => ActorKind.User;
        public Guid? Id { get; } = Guid.NewGuid();
        public string? Key => null;
        public string? Display => "tester@example.com";
        public bool IsSuperAdmin => false;
        public Guid? OnBehalfOf => null;
        public bool IsAuthenticated => true;
    }

    private sealed class TestTenant : ITenantContext
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
        public string? TenantSlug => "acme";
    }
}

// ---- the model under test ----

internal sealed class Widget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Size { get; set; }
    public string Secret { get; set; } = string.Empty;
}

/// <summary>Deliberately not opted into the redactor: the default-deny case.</summary>
internal sealed class Tag
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

[AuditIgnore]
internal sealed class Ping
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

internal sealed class CaptureTestContext : DbContext
{
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Ping> Pings => Set<Ping>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        // Never opened. The change tracker is client-side, and a connection string is only
        // needed for the provider to build a model.
        options.UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused");
}
