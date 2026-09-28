// Vouchfx.Telemetry.Backend — TelemetryEvent contract DTO.
//
// Copied verbatim from Vouchfx.Engine.Telemetry/TelemetryEvent.cs (engine PR #155, issue #152;
// SkippedEventLines added for engine issue #588 / backend issue #30).
// Namespace changed from Vouchfx.Engine.Telemetry → Vouchfx.Telemetry.Backend.Contracts.
// Byte-compatible with the engine's wire format; proven by ContractParityTests.
// DO NOT modify property names, types, [JsonPropertyName] values, required modifiers, or
// property order — any change breaks the wire contract and the golden-fixture parity test.
//
// The member XML docs below describe the CURRENT engine's semantics (vouchfx#568 for
// startupMs/timeToFirstTestMs). A row from an OLDER engine was measured differently;
// docs/wire-contract.md states how.
//
// ONE DELIBERATE DIFFERENCE from the engine's record: SkippedEventLines is `required` on the
// engine (every engine that emits schemaVersion 2 always populates it), but plain (not
// `required`) here. This DTO also binds schemaVersion 1 lines, which pre-date this field
// entirely (it did not exist before schemaVersion 2) and never carry it — a C# `required`
// member would fail every schemaVersion 1 line's deserialisation. The optionality is a
// DTO-level accommodation only, driven by version 1's absence, not a relaxation of version 2:
// AllowlistParser's field-to-introduced-version table (FieldIntroducedAtVersion) requires
// the field's presence at schemaVersion 2 exactly as strictly as the engine's `required`
// keyword does — a schemaVersion 2 line missing it is refused, never defaulted.

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
    /// (for a scenario that runs, that includes topology and engine startup — a
    /// scenario refused before it ran instead stamps its scenario-started at refusal
    /// time, possibly before any topology comes up).  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("startupMs")]
    public required long StartupMs { get; init; }

    /// <summary>
    /// Wall-clock milliseconds from the run starting to the earliest step-completed
    /// line in the archived event stream (time-to-first-test).  The archive is
    /// reconstructed after each scenario's script returns and stamps every step line
    /// with that one shared batch timestamp, so this spans the whole first scenario's
    /// steps rather than its first step alone.  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("timeToFirstTestMs")]
    public required long TimeToFirstTestMs { get; init; }

    /// <summary>
    /// The number of event-stream lines <c>TelemetryEventBuilder</c> could not
    /// read while building this event — an envelope that failed to parse, or a typed
    /// read (scenario-started/scenario-completed/step-started/step-completed) the
    /// builder's own tolerance guard refused.  Counted once per line (issue #588).  A
    /// non-identifying count: it says HOW MANY lines were unreadable, never which line
    /// or what it contained.
    /// </summary>
    [JsonPropertyName("skippedEventLines")]
    public int SkippedEventLines { get; init; }
}
