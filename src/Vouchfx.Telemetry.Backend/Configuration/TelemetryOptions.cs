namespace Vouchfx.Telemetry.Backend.Configuration;

/// <summary>
/// Configuration options for the telemetry ingest surface.
/// Bind from the "Telemetry" configuration section; individual properties
/// are also overridable via <c>VOUCHFX_TELEMETRY_INGEST_TOKENS</c> env var.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>Bearer tokens authorised to post telemetry batches.</summary>
    public string[] IngestTokens { get; set; } = [];

    /// <summary>Maximum request body size in bytes (default 2 MiB).</summary>
    public long MaxBodyBytes { get; set; } = 2_097_152L;

    /// <summary>Maximum number of NDJSON lines per batch (default 500).</summary>
    public int MaxBatchLines { get; set; } = 500;

    /// <summary>Fixed-window rate-limiter permit count per window (default 120).</summary>
    public int RatePermits { get; set; } = 120;

    /// <summary>Fixed-window rate-limiter window duration in seconds (default 60).</summary>
    public int RateWindowSeconds { get; set; } = 60;

    /// <summary>Telemetry event retention in days (default 90).</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Days ahead to pre-create partitions (default 7).</summary>
    public int PrecreateDays { get; set; } = 7;

    /// <summary>Ingest-batch dedup retention in days (default 35).</summary>
    public int DedupRetentionDays { get; set; } = 35;

    /// <summary>Maintenance job interval in hours (default 24).</summary>
    public int JobIntervalHours { get; set; } = 24;
}
