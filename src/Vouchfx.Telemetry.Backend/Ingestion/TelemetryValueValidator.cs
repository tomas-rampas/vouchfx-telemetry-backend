using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>Validates values inside the typed privacy allowlist, including future schema lines.</summary>
internal static class TelemetryValueValidator
{
    // Frozen v1 Core taxonomy, mirrored from the engine's TelemetryEventBuilder.
    // New Core tokens require a deliberate backend allowlist update. Customer-chosen
    // provider names must arrive bucketed as "custom", never as their original names.
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal)
    {
        "http", "db-assert", "mq-publish", "mq-expect", "cache-assert", "mail-expect",
        "webhook-listen", "metrics-assert", "storage-assert", "trace-expect", "script", "custom",
    };

    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "http.rest", "http.soap", "db-assert.postgres", "db-assert.mysql", "db-assert.sqlserver",
        "db-assert.mongodb", "db-assert.dynamodb", "mq-publish.kafka", "mq-publish.rabbitmq",
        "mq-publish.nats", "mq-publish.azureservicebus", "mq-publish.redis", "mq-expect.kafka",
        "mq-expect.rabbitmq", "mq-expect.nats", "mq-expect.azureservicebus", "mq-expect.redis",
        "cache-assert.redis", "cache-assert.elasticsearch", "mail-expect.smtp", "webhook-listen.http",
        "metrics-assert.prometheus", "storage-assert.s3", "trace-expect.otlp", "script.csharp", "custom",
    };

    /// <summary>Returns a fixed, privacy-safe refusal reason, or null for a valid event.</summary>
    public static string? Validate(TelemetryEvent evt)
    {
        // C# required enforces presence during deserialization, not non-nullness.
        if (evt.ToolVersion is null || evt.EngineVersion is null || evt.DotnetVersion is null ||
            evt.StepVerdicts is null || evt.ScenarioVerdicts is null ||
            evt.StepFamilies is null || evt.StepProviders is null)
        {
            return "required values must not be null";
        }

        if (evt.StepFamilies.Keys.Any(key => !Families.Contains(key)) ||
            evt.StepProviders.Keys.Any(key => !Providers.Contains(key)))
        {
            return "usage map contains an unknown taxonomy key";
        }

        // The engine clamps wall-clock timing deltas at zero (DiffMsClamped), so
        // negative timings are invalid even when the host clock moves backwards.
        if (evt.RunCount < 0 || evt.ScenarioCount < 0 || evt.SkippedEventLines < 0 ||
            evt.StartupMs < 0 || evt.TimeToFirstTestMs < 0 ||
            HasNegativeVerdict(evt.StepVerdicts) || HasNegativeVerdict(evt.ScenarioVerdicts) ||
            evt.StepFamilies.Values.Any(count => count < 0) || evt.StepProviders.Values.Any(count => count < 0))
        {
            return "counts and timings must be non-negative";
        }

        return null;
    }

    private static bool HasNegativeVerdict(TelemetryVerdictCounts counts) =>
        counts.Pass < 0 || counts.Fail < 0 || counts.EnvError < 0 || counts.Inconclusive < 0;
}
