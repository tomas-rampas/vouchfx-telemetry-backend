using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>
/// Parses an NDJSON batch into <see cref="TelemetryEvent"/> instances,
/// enforcing the allowlist contract.
/// </summary>
/// <remarks>
/// Schema version 1 events are parsed in <em>strict</em> mode:
/// any property not declared on <see cref="TelemetryEvent"/> is rejected
/// (unknown fields are an indication that client-side allowlist enforcement
/// has been bypassed).  Schema version &gt;1 events are parsed in <em>lenient</em>
/// mode so that a newer engine can send additional metrics without breaking the
/// backend.
/// </remarks>
public static class AllowlistParser
{
    // CA1869: cache JsonSerializerOptions instances — allocation per-call is flagged.
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions LenientOptions = new();

    /// <summary>
    /// Parses <paramref name="lines"/> into a <see cref="ParseResult"/>.
    /// </summary>
    /// <param name="lines">Non-empty content lines produced by <see cref="NdjsonReader.SplitLines"/>.</param>
    /// <param name="maxLines">Maximum number of lines accepted per batch.</param>
    public static ParseResult Parse(IReadOnlyList<string> lines, int maxLines)
    {
        if (lines.Count == 0)
        {
            return new ParseResult.Empty();
        }

        if (lines.Count > maxLines)
        {
            return new ParseResult.TooManyLines(lines.Count, maxLines);
        }

        var events = new List<TelemetryEvent>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("schemaVersion", out var svElement))
                {
                    return new ParseResult.Bad($"Line {i + 1}: missing schemaVersion");
                }

                if (svElement.ValueKind != JsonValueKind.Number || !svElement.TryGetInt32(out var sv))
                {
                    return new ParseResult.Bad($"Line {i + 1}: schemaVersion is not an integer");
                }

                if (sv < 1)
                {
                    return new ParseResult.Bad($"Line {i + 1}: schemaVersion must be >= 1, got {sv}");
                }

                // DELIBERATE forward-compatibility: lenient parsing of schema versions > 1 is
                // intentional. An at-least-once engine client emitting a future schema version
                // must not be rejected forever; it is SAFE because deserialisation targets the
                // typed TelemetryEvent allowlist, so an unknown/forbidden field has no property
                // to bind to and can never be persisted.
                var opts = sv == 1 ? StrictOptions : LenientOptions;
                var evt = JsonSerializer.Deserialize<TelemetryEvent>(line, opts)
                    ?? throw new JsonException("Deserialisation returned null");
                events.Add(evt);
            }
            catch (JsonException ex)
            {
                return new ParseResult.Bad($"Line {i + 1}: {ex.Message}");
            }
        }

        return new ParseResult.Ok(events);
    }
}
