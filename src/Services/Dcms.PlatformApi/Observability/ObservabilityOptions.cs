namespace Dcms.PlatformApi.Observability;

/// <summary>
/// Where the telemetry stores live. All internal: none of these publish a host port in
/// production, so the compose network is the only way to reach them and none of these URLs is
/// a secret.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public string PrometheusUrl { get; set; } = "http://prometheus:9090";
    public string LokiUrl { get; set; } = "http://loki:3100";
    public string TempoUrl { get; set; } = "http://tempo:3200";
    public string NatsMonitoringUrl { get; set; } = "http://nats:8222";
    public string VaultUrl { get; set; } = "http://vault:8200";

    /// <summary>
    /// The log-janitor sidecar, which truncates Docker's json log files.
    ///
    /// <para>Empty disables the feature and the console says so. That is the default, and it is
    /// the default because the sidecar is the one component here that holds write access to
    /// <c>/var/lib/docker/containers</c> — a deployment that does not want that capability
    /// should not have to remove anything to be rid of it.</para>
    /// </summary>
    public string LogJanitorUrl { get; set; } = string.Empty;

    /// <summary>Shared secret for the janitor. Compared by the janitor in constant time.</summary>
    public string LogJanitorSecret { get; set; } = string.Empty;

    public bool LogJanitorEnabled =>
        !string.IsNullOrWhiteSpace(LogJanitorUrl) && !string.IsNullOrWhiteSpace(LogJanitorSecret);
}
