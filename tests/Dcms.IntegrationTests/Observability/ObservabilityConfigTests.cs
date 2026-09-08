using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Observability;

/// <summary>
/// Invariants of the observability stack's own configuration. No containers: these read the
/// files under <c>infra/observability</c> and the compose stack that mounts them.
///
/// <para>Each one guards a fault that was live in production and that nothing else could have
/// noticed — a dashboard renders a wrong graph exactly as confidently as a right one, a session
/// that expires is indistinguishable from a session that was signed out, and a proxy narrating
/// its own health checks looks like a proxy that is working.</para>
/// </summary>
public class ObservabilityConfigTests
{
    /// <summary>
    /// Metric families whose series identity changes when the emitting process or container is
    /// recreated, so a bare selector draws one line per restart instead of one line per thing.
    ///
    /// <para>cAdvisor stamps <c>id</c> (the cgroup path) and <c>image</c> on every container
    /// series; a .NET service's <c>service.instance.id</c> is a fresh GUID per process and
    /// becomes the <c>instance</c> label. Alloy now collapses both (see
    /// <c>prometheus.relabel.cadvisor_identity</c> and the instance rule in
    /// <c>prometheus.relabel.otel_service</c>), which stops NEW duplicates — but historical
    /// series keep their old labels for the whole retention window, so the panels have to
    /// aggregate as well.</para>
    /// </summary>
    private static readonly string[] ChurnProneFamilies =
    [
        "container_", "dotnet_", "process_", "kestrel_", "db_client_",
        "http_server_", "http_client_", "jetstream_", "gnatsd_",
    ];

    private static readonly Regex Aggregation = new(
        @"\b(sum|avg|max|min|count|topk|bottomk|quantile|group|stddev|stdvar|count_values)\s*(by|without)?\s*[\(\{]",
        RegexOptions.Compiled);

    public static TheoryData<string> Dashboards()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "infra", "observability", "grafana", "dashboards"),
                     "*.json", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(RepoRoot(), file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Dashboards))]
    public void No_panel_selects_a_churn_prone_metric_without_aggregating_it(string relativePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));

        var offenders = Panels(doc.RootElement)
            .SelectMany(panel => Targets(panel).Select(target => (panel, target)))
            .Where(pair => IsPrometheus(pair.target, pair.panel))
            .Select(pair => (Title: Title(pair.panel), Expr: Expr(pair.target)))
            .Where(pair => pair.Expr is not null)
            .Where(pair => ChurnProneFamilies.Any(f => pair.Expr!.Contains(f, StringComparison.Ordinal)))
            .Where(pair => !Aggregation.IsMatch(pair.Expr!))
            .ToList();

        // A tripwire rather than a proof: it asks whether the query aggregates AT ALL, not
        // whether every selector inside it is covered. That is deliberate — checking the latter
        // means parsing PromQL, and every panel this ever caught was completely bare.
        offenders.Should().BeEmpty(
            "these panels select a metric whose labels change when the process or container is "
            + "recreated, and draw it without aggregating — so the graph grows an extra line per "
            + "restart, all of them carrying the same legend:\n{0}",
            string.Join("\n", offenders.Select(o => $"  [{o.Title}] {o.Expr}")));
    }

    [Fact]
    public void Alloy_pins_the_labels_that_would_otherwise_churn_per_restart()
    {
        var alloy = File.ReadAllText(
            Path.Combine(RepoRoot(), "infra", "observability", "alloy", "config.alloy"));

        alloy.Should().Contain("prometheus.relabel \"cadvisor_identity\"",
            "without it every container's series is re-minted on each deploy, because cAdvisor's "
            + "`id` and `image` labels both change when a container is recreated");
        alloy.Should().MatchRegex(@"action\s*=\s*""labeldrop""[\s\S]{0,120}regex\s*=\s*""id\|image""");

        // The OTLP half: `instance` comes from service.instance.id, which is a fresh GUID every
        // time a service starts. Pinned to the service name so a restart continues one series.
        alloy.Should().MatchRegex(
            @"target_label\s*=\s*""instance""[\s\S]{0,80}replacement\s*=\s*""\$1""",
            "OTLP metrics need a stable `instance`, or every restart forks a new series set");
    }

    [Fact]
    public void Grafana_trusts_a_header_only_because_nothing_else_can_reach_it()
    {
        var ini = File.ReadAllText(
            Path.Combine(RepoRoot(), "infra", "observability", "grafana", "grafana.ini"));

        var proxyAuth = Regex.Match(ini, @"^\[auth\.proxy\](?<body>[\s\S]*?)(?=^\[|\z)", RegexOptions.Multiline);
        proxyAuth.Success.Should().BeTrue();
        if (!Regex.IsMatch(proxyAuth.Groups["body"].Value, @"^\s*enabled\s*=\s*true\s*$", RegexOptions.Multiline))
        {
            return;
        }

        // Grafana must issue no session of its own while the edge is the session. Its frontend
        // schedules a token rotation from the script-readable `grafana_session_expiry` cookie,
        // and a 401 from that rotation makes the page reload -- which, under auth.proxy, signs
        // the operator straight back in and rotates again, about once a second, forever. There
        // is no exit: the frontend's recovery from a dead session is the one request that always
        // succeeds here. See Dcms.Edge GrafanaSessionCookies, which clears the ones already out
        // in browsers; this keeps new ones from being minted.
        Regex.IsMatch(proxyAuth.Groups["body"].Value, @"^\s*enable_login_token\s*=\s*true\s*$", RegexOptions.Multiline)
            .Should().BeFalse("auth.proxy must not issue a Grafana session; it reload-loops the dashboard");

        // X-WEBAUTH-USER is a bearer credential in header form: whoever can set it on a request
        // that reaches Grafana IS that user. The edge strips it from every inbound request, and
        // Grafana publishes no host port -- those two together are the whole of what makes
        // header auth safe here, and this asserts the half that lives in compose.
        //
        // There is no whitelist to assert instead: it wants an IP, compose assigns them
        // dynamically, and a value pinned today locks every operator out after the next `up`.
        foreach (var composeFile in new[] { "docker-compose.yml", "docker-compose.prod.yml", "docker-compose.vps.yml" })
        {
            var compose = File.ReadAllText(Path.Combine(RepoRoot(), composeFile));
            var grafana = Regex.Match(
                compose, @"^  grafana:\n(?<body>(?:(?:    |\n).*\n)*)", RegexOptions.Multiline);
            if (!grafana.Success) continue;

            grafana.Groups["body"].Value.Should().NotMatchRegex(@"^\s*ports:",
                "{0} publishes a host port for Grafana. With auth.proxy enabled that puts "
                + "X-WEBAUTH-USER within reach of anything on the host, and setting it is the "
                + "whole of being a platform SuperAdmin.", composeFile);
        }
    }

    [Fact]
    public void Forgejo_never_auto_registers_an_account_the_edge_named()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        var forgejo = Regex.Match(
            compose, @"^  forgejo:\n(?<body>(?:(?:    |\n).*\n)*)", RegexOptions.Multiline);
        forgejo.Success.Should().BeTrue();
        var body = forgejo.Groups["body"].Value;

        if (!Regex.IsMatch(body, @"ENABLE_REVERSE_PROXY_AUTHENTICATION:\s*""true"""))
        {
            return;
        }

        // Forgejo usernames are allocated by ForgejoUserSync from the email's local part with a
        // numeric suffix on collision, so "rgolias" and "rgolias-2" can be two different people.
        // With auto-registration on, a header naming an account that does not exist yet creates
        // one -- claiming a name the sync is about to hand somebody else, in a server holding
        // every tenant's site repositories.
        body.Should().MatchRegex(@"ENABLE_REVERSE_PROXY_AUTO_REGISTRATION:\s*""false""",
            "reverse-proxy auth without this creates accounts under names the sync owns");

        // Not 127.0.0.0/8 (Forgejo's default): this container publishes HTTP on the host's
        // loopback, so trusting loopback would let any process on the host be any git user.
        var trusted = Regex.Match(body, @"REVERSE_PROXY_TRUSTED_PROXIES:\s*""(?<v>[^""]*)""");
        trusted.Success.Should().BeTrue("reverse-proxy auth is ignored unless the caller is trusted");
        trusted.Groups["v"].Value.Should().NotContain("127.0.0.",
            "Forgejo publishes HTTP on the host loopback, so trusting it defeats the header");
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void The_docker_socket_proxy_does_not_narrate_every_request(string composeFile)
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), composeFile));

        // Each `docker-socket-proxy*:` service block, up to the next top-level service key.
        foreach (Match service in Regex.Matches(
                     compose, @"^  (?<name>docker-socket-proxy[\w-]*):\n(?<body>(?:(?:    |\n).*\n)*)",
                     RegexOptions.Multiline))
        {
            var name = service.Groups["name"].Value;
            var body = service.Groups["body"].Value;

            var logLevel = Regex.Match(body, @"^\s*LOG_LEVEL:\s*""?(?<level>\w+)""?", RegexOptions.Multiline);
            logLevel.Success.Should().BeTrue(
                "{0} in {1} sets no LOG_LEVEL, so HAProxy defaults to `info` and writes one line "
                + "per proxied request. Its clients poll continuously, and loki.source.docker "
                + "ships every one of those lines — it becomes the largest single writer into "
                + "the Loki retention budget.", name, composeFile);

            new[] { "info", "debug" }.Should().NotContain(logLevel.Groups["level"].Value,
                "at these levels HAProxy emits its per-request access log; `warning` keeps "
                + "denials, backend failures and config errors while dropping the narration");
        }
    }

    private static IEnumerable<JsonElement> Panels(JsonElement root)
    {
        if (!root.TryGetProperty("panels", out var panels)) yield break;
        foreach (var panel in panels.EnumerateArray())
        {
            yield return panel;
            // Rows nest their own panels one level down.
            if (panel.TryGetProperty("panels", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in nested.EnumerateArray()) yield return child;
            }
        }
    }

    private static IEnumerable<JsonElement> Targets(JsonElement panel) =>
        panel.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array
            ? targets.EnumerateArray()
            : [];

    private static string? Expr(JsonElement target) =>
        target.TryGetProperty("expr", out var expr) ? expr.GetString() : null;

    private static string Title(JsonElement panel) =>
        panel.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";

    /// <summary>Loki panels carry LogQL in the same field; only PromQL is in scope here.</summary>
    private static bool IsPrometheus(JsonElement target, JsonElement panel)
    {
        var source = target.TryGetProperty("datasource", out var t) ? t
            : panel.TryGetProperty("datasource", out var p) ? p
            : default;
        return source.ValueKind == JsonValueKind.Object
               && source.TryGetProperty("type", out var type)
               && type.GetString() == "prometheus";
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return directory!.FullName;
    }
}
