// Vouchfx.Telemetry.Backend — TelemetryVerdictCounts contract DTO.
//
// Copied verbatim from Vouchfx.Engine.Telemetry/TelemetryEvent.cs (engine PR #155, issue #152).
// Namespace changed from Vouchfx.Engine.Telemetry → Vouchfx.Telemetry.Backend.Contracts.
// Byte-compatible with the engine's wire format; proven by ContractParityTests.
// DO NOT modify property names, types, [JsonPropertyName] values, or property order.

using System.Text.Json.Serialization;

namespace Vouchfx.Telemetry.Backend.Contracts;

/// <summary>
/// The four §12.1 verdict counts, reused for both the step-level and scenario-level
/// aggregates on a <see cref="TelemetryEvent"/>.
/// </summary>
/// <remarks>
/// This mirrors the four-outcome taxonomy (Pass / Fail / EnvError / Inconclusive)
/// exactly, kept separate everywhere — a count, never a label of WHAT passed or
/// failed.
/// </remarks>
public sealed record TelemetryVerdictCounts
{
    /// <summary>Number of items (steps or scenarios) that passed.</summary>
    [JsonPropertyName("pass")]
    public int Pass { get; init; }

    /// <summary>Number of items that failed their assertions.</summary>
    [JsonPropertyName("fail")]
    public int Fail { get; init; }

    /// <summary>Number of items that ended in an environment error.</summary>
    [JsonPropertyName("envError")]
    public int EnvError { get; init; }

    /// <summary>Number of items whose outcome was inconclusive.</summary>
    [JsonPropertyName("inconclusive")]
    public int Inconclusive { get; init; }
}
