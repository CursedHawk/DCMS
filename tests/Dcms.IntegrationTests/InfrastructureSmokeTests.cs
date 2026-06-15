using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests;

public class InfrastructureSmokeTests
{
    [DockerFact]
    public async Task Postgres_container_starts_and_accepts_connections()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build();

        await postgres.StartAsync(TestContext.Current.CancellationToken);

        var result = await postgres.ExecScriptAsync("SELECT 1;", TestContext.Current.CancellationToken);
        result.ExitCode.Should().Be(0L);
    }
}
