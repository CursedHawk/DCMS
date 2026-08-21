extern alias AdminApiApp;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// Builds admin-api once for the whole class. Standing the host up costs about ten seconds,
/// and there are twenty cases here.
/// </summary>
public sealed class AdminApiHostFixture : IDisposable
{
    public WebApplicationFactory<AdminApiApp::Program> Factory { get; } =
        new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:Migrate", "false");
            builder.UseSetting("Tenancy:ApplyRls", "false");
            // Deliberately unreachable. Nothing here needs a database — and the one test that
            // touches SaveChanges needs it to fail, after the interceptor has had its turn.
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Port=1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    public void Dispose()
    {
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Proves EF actually finds the audit interceptors, for every context.
///
/// <para>They are registered once, as <c>IInterceptor</c>, and EF Core is supposed to pick that
/// up for every context registered through <c>AddDbContext</c> — which is why eleven
/// <c>Add*Data</c> extensions needed no edit. <b>The failure mode if that ever stops holding is
/// silence.</b> No exception, no warning; changes simply stop being recorded, and nobody finds
/// out until the first time someone goes looking for a record that was never written. That is
/// the entire reason this test exists.</para>
///
/// <para>Container-free, and not by weakening the claim: <c>SavingChanges</c> fires before EF
/// opens a connection, so a context pointed at nothing still runs the interceptor and can be
/// observed doing it. The save then fails, which this exploits twice over — the second test
/// checks that a failed save takes its audit rows back down with it.</para>
/// </summary>
public sealed class AuditInterceptorDiscoveryTests(AdminApiHostFixture fixture)
    : IClassFixture<AdminApiHostFixture>
{
    public static TheoryData<Type> BusinessContexts() =>
    [
        typeof(TenancyDbContext), typeof(CmsDbContext), typeof(MediaDbContext), typeof(SitesDbContext),
        typeof(FormsDbContext), typeof(ChatDbContext), typeof(VisitorsDbContext), typeof(AiDbContext),
        typeof(SearchDbContext), typeof(AnalyticsDbContext),
    ];

    [Theory]
    [MemberData(nameof(BusinessContexts))]
    public async Task Saving_through_any_business_context_drains_the_audit_buffer(Type contextType)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        // Something has to be pending for EF to run a save at all: it short-circuits an empty
        // one before any interceptor is consulted. (Which is also why a handler that records
        // an entry and changes nothing else keeps the non-atomic end-of-request write — there
        // is no transaction to join.) Any mapped entity will do; this one every context has.
        context.Add(new AuditOutboxMessage
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{}",
        });

        recorder.Record("test.action").For("thing", Guid.NewGuid());
        recorder.Pending.Should().ContainSingle();

        // Fails on connect, long after SavingChanges has run.
        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        recorder.Pending.Should().BeEmpty(
            "{0} must enlist buffered records into its own transaction; if it does not, every change "
            + "it commits is committed with no record of it", contextType.Name);
    }

    [Theory]
    [MemberData(nameof(BusinessContexts))]
    public void Every_business_context_maps_the_audit_outbox(Type contextType)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        context.Model.FindEntityType(typeof(AuditOutboxMessage)).Should().NotBeNull(
            "the interceptor writes the record through the saving context, so a context that does "
            + "not map the outbox silently falls back to a separate, non-atomic write");
    }

    [Fact]
    public async Task A_save_that_fails_does_not_leave_its_audit_rows_behind()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        context.Tenants.Add(new Tenant { Id = Guid.NewGuid().ToString(), Identifier = "doomed" });
        recorder.Record("test.action").For("thing", Guid.NewGuid());

        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        context.ChangeTracker.Entries<AuditOutboxMessage>().Should().BeEmpty(
            "nothing committed, so nothing should be recorded — and rows left Added here would be "
            + "written by whatever saved next, describing work that never happened");
    }
}
