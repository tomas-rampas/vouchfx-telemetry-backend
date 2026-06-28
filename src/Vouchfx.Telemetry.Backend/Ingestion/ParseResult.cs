using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>
/// Discriminated union representing the outcome of parsing an NDJSON batch.
/// </summary>
public abstract record ParseResult
{
    /// <summary>Parsing succeeded; <see cref="Events"/> holds the deserialised events.</summary>
    public sealed record Ok(IReadOnlyList<TelemetryEvent> Events) : ParseResult;

    /// <summary>
    /// One or more lines failed validation or deserialisation.
    /// <see cref="Reason"/> is for server-side logging only — never surfaced to callers.
    /// </summary>
    public sealed record Bad(string Reason) : ParseResult;

    /// <summary>
    /// The batch exceeded the configured line limit; maps to HTTP 413.
    /// </summary>
    public sealed record TooManyLines(int Actual, int Max) : ParseResult;

    /// <summary>All lines were empty or whitespace-only; maps to HTTP 400.</summary>
    public sealed record Empty : ParseResult;
}
