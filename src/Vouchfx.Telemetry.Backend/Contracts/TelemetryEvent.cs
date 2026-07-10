// Vouchfx.Telemetry.Backend — TelemetryEvent contract DTO.
//
// Copied verbatim from Vouchfx.Engine.Telemetry/TelemetryEvent.cs (engine PR #155, issue #152).
// Namespace changed from Vouchfx.Engine.Telemetry → Vouchfx.Telemetry.Backend.Contracts.
// Byte-compatible with the engine's wire format; proven by ContractParityTests.
// DO NOT modify property names, types, [JsonPropertyName] values, required modifiers, or
// property order — any change breaks the wire contract and the golden-fixture parity test.

using System.Text.Json.Serialization;

namespace Vouchfx.Telemetry.Backend.Contracts;

/// <summary>
/// The complete, privacy-allowlisted telemetry payload (S10-G-04).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This record is an allowlist, not a denylist.</strong>  Its public
/// properties below are the <em>entire</em> set of values vouchfx will ever
/// transmit when telemetry is enabled.  Everything a customer's tests touch —
/// step contents, captured values, secret references/values, SUT addresses/URLs,
/// container image names, scenario names, step ids, provider observations — has
/// <em>no place to live</em> on this record and therefore can never be sent.  This
/// physical absence is the "provably never sent" guarantee, defended by the
/// allowlist-reflection and denylist-serialisation gates in the test project.
/// </para>
/// <para>
/// Every value here is either an environment/version fact (tool / engine / runtime
/// version) or a non-identifying AGGREGATE COUNT or TIMING derived from the event
/// stream.  Counts are keyed by step <em>family</em> (the intent, e.g. <c>http</c>)
/// and <em>family.provider</em> (the technology, e.g. <c>http.rest</c>) — both are
/// closed Core taxonomy tokens, with any custom/non-Core provider's step bucketed under
/// the constant <c>"custom"</c> key (so an author-chosen step kind id is never emitted).
/// The keys describe WHICH built-in step kinds ran (and how many custom-provider steps
/// ran), never the data those steps carried.
/// </para>
/// </remarks>
public sealed record TelemetryEvent
{
    /// <summary>
    /// The telemetry payload schema version.  Lets a future backend evolve the
    /// shape without misreading older events.  Distinct from the engine / event-stream
    /// versions: it versions THIS allowlist.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// UTC timestamp at which this telemetry event was built (the end of the run).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The opaque, randomly-generated install identifier (a GUID, minted only when
    /// the user opts in).  It identifies the INSTALL, never the user, the machine, or
    /// any test content; deleting it (via <c>telemetry disable</c>) severs the link.
    /// </summary>
    [JsonPropertyName("installId")]
    public required Guid InstallId { get; init; }

    /// <summary>The vouchfx tool (CLI) informational version, e.g. <c>"1.0.0"</c>.</summary>
    [JsonPropertyName("toolVersion")]
    public required string ToolVersion { get; init; }

    /// <summary>The vouchfx engine assembly informational version.</summary>
    [JsonPropertyName("engineVersion")]
    public required string EngineVersion { get; init; }

    /// <summary>
    /// The .NET runtime description the tool ran on, e.g.
    /// <c>".NET 8.0.7"</c> (from <c>RuntimeInformation.FrameworkDescription</c>).
    /// </summary>
    [JsonPropertyName("dotnetVersion")]
    public required string DotnetVersion { get; init; }

    /// <summary>
    /// The number of suite runs this event represents.  Always <c>1</c> in v1 (one
    /// event per <c>vouchfx run</c>); present so a backend can aggregate without a
    /// schema change.
    /// </summary>
    [JsonPropertyName("runCount")]
    public required int RunCount { get; init; }

    /// <summary>The number of scenarios that executed in the run.</summary>
    [JsonPropertyName("scenarioCount")]
    public required int ScenarioCount { get; init; }

    /// <summary>
    /// Per-verdict STEP counts across the whole run (pass / fail / envError /
    /// inconclusive).  Counts only — never which step, never its data.
    /// </summary>
    [JsonPropertyName("stepVerdicts")]
    public required TelemetryVerdictCounts StepVerdicts { get; init; }

    /// <summary>
    /// Per-verdict SCENARIO counts across the whole run (pass / fail / envError /
    /// inconclusive).  Counts only — never which scenario, never its name.
    /// </summary>
    [JsonPropertyName("scenarioVerdicts")]
    public required TelemetryVerdictCounts ScenarioVerdicts { get; init; }

    /// <summary>
    /// Step counts keyed by step FAMILY (the intent token, e.g. <c>"http"</c>,
    /// <c>"db-assert"</c>), e.g. <c>{"http":3,"db-assert":1}</c>.  The keys are drawn
    /// ONLY from the frozen built-in Core family taxonomy; any custom/non-Core
    /// provider's step is counted under the <c>"custom"</c> bucket, so an author-chosen
    /// family id is never a key here — never customer data.
    /// </summary>
    [JsonPropertyName("stepFamilies")]
    public required IReadOnlyDictionary<string, int> StepFamilies { get; init; }

    /// <summary>
    /// Step counts keyed by FAMILY.PROVIDER (the technology token, e.g.
    /// <c>"http.rest"</c>, <c>"db-assert.postgres"</c>), e.g.
    /// <c>{"http.rest":3}</c>.  The keys are drawn ONLY from the frozen built-in Core
    /// provider taxonomy; any custom/non-Core provider's step is counted under the
    /// <c>"custom"</c> bucket, so an author-chosen provider id is never a key here —
    /// never customer data.
    /// </summary>
    [JsonPropertyName("stepProviders")]
    public required IReadOnlyDictionary<string, int> StepProviders { get; init; }

    /// <summary>
    /// Wall-clock milliseconds from the run starting to the first scenario starting
    /// (topology + engine startup).  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("startupMs")]
    public required long StartupMs { get; init; }

    /// <summary>
    /// Wall-clock milliseconds from the run starting to the first step completing
    /// (time-to-first-test).  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("timeToFirstTestMs")]
    public required long TimeToFirstTestMs { get; init; }
}
