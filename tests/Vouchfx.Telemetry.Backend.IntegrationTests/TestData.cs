using System.Security.Cryptography;
using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>Static helpers for building test fixtures.</summary>
internal static class TestData
{
    private static readonly TelemetryVerdictCounts ZeroVerdicts = new()
    {
        Pass = 1,
        Fail = 0,
        EnvError = 0,
        Inconclusive = 0,
    };

    /// <summary>Builds a minimal valid <see cref="TelemetryEvent"/> for use in tests.</summary>
    public static TelemetryEvent MakeEvent(
        Guid? installId = null,
        DateTimeOffset? timestamp = null,
        int schemaVersion = 1) => new()
        {
            SchemaVersion = schemaVersion,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            InstallId = installId ?? Guid.NewGuid(),
            ToolVersion = "1.0.0",
            EngineVersion = "1.0.0",
            DotnetVersion = ".NET 8.0",
            RunCount = 1,
            ScenarioCount = 1,
            StepVerdicts = ZeroVerdicts,
            ScenarioVerdicts = ZeroVerdicts,
            StepFamilies = new Dictionary<string, int> { ["http"] = 1 },
            StepProviders = new Dictionary<string, int> { ["http.rest"] = 1 },
            StartupMs = 100L,
            TimeToFirstTestMs = 200L,
        };

    /// <summary>Returns a random 64-character lowercase hex idempotency key.</summary>
    public static string MakeIdempotencyKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
