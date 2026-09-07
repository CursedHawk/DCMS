extern alias EdgeApp;

using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using EdgeCerts = EdgeApp::Dcms.Edge.Certificates;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// The ceiling that stops a managed certificate spending Let's Encrypt's weekly budget.
///
/// <para><b>Why this is worth a database test rather than a unit test.</b> The guard is a query
/// over an attempt ledger, and the properties that matter are all about which rows it counts:
/// successes weekly, refusals hourly, and each identifier set on its own budget. Every one of
/// those is expressed in the <c>Where</c> clause, so a mock of the ledger would test nothing but
/// the mock.</para>
///
/// <para>What this protects: a single certificate now covers every hostname the platform serves,
/// so five failed duplicate issuances in a week take TLS away from all of them at once, for a
/// week, with no way to hurry it. See <c>docs/adr/0011-wildcard-tls-dns01.md</c>.</para>
/// </summary>
public sealed class ManagedCertificateGuardTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private ServiceProvider services = null!;

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] PlatformIdentifiers =
        ["highgeek.eu", "*.highgeek.eu", "*.dcms.highgeek.eu"];

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();

        var collection = new ServiceCollection();
        collection.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));
        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [DockerFact]
    public async Task Allows_an_order_when_nothing_has_been_spent()
    {
        var (db, managed) = await SeedAsync();

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeTrue();
        budget.IssuancesRemaining.Should().Be(3);
    }

    [DockerFact]
    public async Task Refuses_a_fourth_issuance_of_the_same_identifiers_inside_a_week()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: true, Now.AddDays(-6));
        await RecordAsync(db, managed, succeeded: true, Now.AddDays(-3));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-2));

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeFalse();
        budget.IssuancesRemaining.Should().Be(0);
        // Named so an operator can see when it lifts, rather than being told only "no".
        budget.RetryAfter.Should().Be(Now.AddDays(-6).AddDays(7));
        budget.Reason.Should().Contain("7 days");
    }

    [DockerFact]
    public async Task Allows_an_order_again_once_the_oldest_issuance_ages_past_a_week()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: true, Now.AddDays(-8));
        await RecordAsync(db, managed, succeeded: true, Now.AddDays(-3));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-2));

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeTrue("the eight-day-old issuance is outside the rolling window");
    }

    /// <summary>
    /// Refusals decay hourly, not weekly, because that is the limit they actually protect —
    /// Let's Encrypt allows five failed authorizations per identifier per hour. Counting them
    /// against the weekly budget instead would mean three transient DNS failures on the day the
    /// platform wildcard is first ordered locked its main certificate out for a week.
    /// </summary>
    [DockerFact]
    public async Task Holds_off_after_three_refusals_in_an_hour_but_not_for_the_week()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-40));
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-20));
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-5));

        var blocked = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        blocked.Allowed.Should().BeFalse();
        blocked.Reason.Should().Contain("hour");
        // Still three issuances in hand: refusals cost the hourly budget, not the weekly one.
        blocked.IssuancesRemaining.Should().Be(3);

        var later = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now.AddHours(1).AddMinutes(1), TestContext.Current.CancellationToken);

        later.Allowed.Should().BeTrue("an hour of refusals must not become a week of them");
    }

    /// <summary>
    /// Editing the identifiers genuinely starts a new budget at the CA, because the limit is
    /// counted against the exact set. Merely reordering them does not — see the unit test for
    /// the key; this asserts the ledger agrees.
    /// </summary>
    [DockerFact]
    public async Task Gives_a_different_set_of_identifiers_its_own_budget()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-3));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-2));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-1));

        (await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken))
            .Allowed.Should().BeFalse();

        managed.Identifiers = ["highgeek.eu", "*.highgeek.eu", "*.dcms.highgeek.eu", "extra.example"];
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeTrue();
    }

    [DockerFact]
    public async Task Counts_the_same_identifiers_written_in_a_different_order_against_one_budget()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-3));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-2));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-1));

        // Reordered, not changed. If this reset the budget it would be a way to spend the CA's
        // real five-per-week limit while the guard reported nothing had been spent.
        managed.Identifiers = ["*.dcms.highgeek.eu", "*.highgeek.eu", "highgeek.eu"];
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeFalse();
    }

    [DockerFact]
    public async Task Does_not_count_another_certificates_attempts()
    {
        var (db, managed) = await SeedAsync();
        var other = new EdgeManagedCertificate { Name = "Other", Identifiers = ["other.example"] };
        db.ManagedCertificates.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RecordAsync(db, other, succeeded: true, Now.AddHours(-3));
        await RecordAsync(db, other, succeeded: true, Now.AddHours(-2));
        await RecordAsync(db, other, succeeded: true, Now.AddHours(-1));

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeTrue();
        budget.IssuancesRemaining.Should().Be(3);
    }

    /// <summary>
    /// The ledger is what a restart cannot clear, which is the entire reason it is not
    /// <c>ConsecutiveFailures</c>: the edge's TLS preflight clears that counter on every process
    /// start, so a crash-looping container would re-order on every boot.
    /// </summary>
    [DockerFact]
    public async Task Survives_the_backoff_clearing_that_runs_on_every_restart()
    {
        var (db, managed) = await SeedAsync();
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-3));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-2));
        await RecordAsync(db, managed, succeeded: true, Now.AddHours(-1));

        var store = new EdgeCerts.CertificateStore(
            services, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EdgeCerts.CertificateStore>.Instance);

        await store.ClearBackoffForOutstandingWorkAsync(TestContext.Current.CancellationToken);

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeFalse("a restart must not hand back a spent weekly budget");
    }

    /// <summary>
    /// The failure that is recorded and charged to nothing: no Cloudflare token, so no order was
    /// ever placed and the CA spent none of its failed-authorization budget on us.
    ///
    /// <para>These attempts used not to be written down at all, which was right about the budget
    /// and wrong about everything else — the console showed no expiry, no error and no history,
    /// so "renew now" reported success and visibly did nothing.</para>
    /// </summary>
    [DockerFact]
    public async Task Does_not_charge_the_hourly_ceiling_for_orders_that_never_reached_the_ca()
    {
        var (db, managed) = await SeedAsync();
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-30));
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-20));
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-10));
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-5));

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeTrue(
            "nothing was sent to the CA, so there is nothing for a ceiling to protect");
        budget.IssuancesRemaining.Should().Be(3);
    }

    /// <summary>
    /// The other half of the same property: a real refusal still counts, and a mixture of the two
    /// counts only the refusals. Without this, the column added to stop over-counting would be
    /// free to under-count instead.
    /// </summary>
    [DockerFact]
    public async Task Still_charges_refusals_that_did_reach_the_ca()
    {
        var (db, managed) = await SeedAsync();
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-40));
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-30));
        await RecordUnavailableAsync(db, managed, Now.AddMinutes(-25));
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-20));
        await RecordAsync(db, managed, succeeded: false, Now.AddMinutes(-10));

        var budget = await EdgeCerts.ManagedCertificateGuard.EvaluateAsync(
            db, managed, Options(), Now, TestContext.Current.CancellationToken);

        budget.Allowed.Should().BeFalse();
        // The oldest CHARGED failure, not the oldest row: an uncounted attempt must not be able
        // to drag the retry time earlier either.
        budget.RetryAfter.Should().Be(Now.AddMinutes(-30).AddHours(1));
    }

    private static EdgeCerts.CertificateOptions Options() => new()
    {
        ManagedIssuancesPerWeek = 3,
        ManagedFailuresPerHour = 3,
    };

    private async Task<(EdgeDbContext Db, EdgeManagedCertificate Managed)> SeedAsync()
    {
        var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        var managed = new EdgeManagedCertificate
        {
            Name = "Platform wildcard",
            Identifiers = PlatformIdentifiers,
        };
        db.ManagedCertificates.Add(managed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (db, managed);
    }

    private static Task RecordAsync(
        EdgeDbContext db, EdgeManagedCertificate managed, bool succeeded, DateTimeOffset at)
        => EdgeCerts.ManagedCertificateGuard.RecordAttemptAsync(
            db, managed, succeeded, succeeded ? null : "DNS problem: NXDOMAIN", at,
            TestContext.Current.CancellationToken);

    private static Task RecordUnavailableAsync(
        EdgeDbContext db, EdgeManagedCertificate managed, DateTimeOffset at)
        => EdgeCerts.ManagedCertificateGuard.RecordAttemptAsync(
            db, managed, succeeded: false,
            "Edge:Dns:Cloudflare:ApiToken is not set, so no DNS-01 challenge can be published.",
            at, TestContext.Current.CancellationToken, reachedCa: false);
}
