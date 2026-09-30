using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>
/// Parses an NDJSON batch into <see cref="TelemetryEvent"/> instances,
/// enforcing the allowlist contract.
/// </summary>
/// <remarks>
/// Every schema version at or below <see cref="HighestKnownSchemaVersion"/> is parsed in
/// <em>strict</em> mode: any property not declared on <see cref="TelemetryEvent"/> is
/// rejected (unknown fields are an indication that client-side allowlist enforcement has
/// been bypassed), AND every field listed in <see cref="FieldIntroducedAtVersion"/> must
/// match its line's version exactly — absent before its introduced version, present from
/// its introduced version onward. "Strict for a known version" means the engine's exact
/// shape for that version, not merely "no stray fields": the engine marks a versioned field
/// <c>required</c> from the version that introduces it, so this parser refuses its absence
/// there too, not just its presence earlier.
/// <para>
/// Schema versions above <see cref="HighestKnownSchemaVersion"/> are parsed in
/// <em>lenient</em> mode: unknown fields are dropped and no field-presence rule applies, so
/// a future engine can send additional metrics without breaking the backend.
/// </para>
/// </remarks>
public static class AllowlistParser
{
    /// <summary>
    /// The highest schema version this backend has a strict, named binding for. Versions at
    /// or below this are parsed strictly; versions above it are parsed leniently for
    /// forward compatibility.
    /// </summary>
    public const int HighestKnownSchemaVersion = 2;

    /// <summary>
    /// Fields that exist on <see cref="TelemetryEvent"/> but were introduced at a later
    /// schema version than 1 — the engine marks each one <c>required</c> starting at its
    /// introduced version, so a known-version line must match exactly: present from that
    /// version onward, absent before it. Strict parsing and this presence rule apply only
    /// at schemaVersion &lt;= <see cref="HighestKnownSchemaVersion"/>: adding a field at a
    /// new version N requires BOTH a new entry here AND raising
    /// <see cref="HighestKnownSchemaVersion"/> to N. An entry alone is only half enforced:
    /// every known version, all below N, refuses the field, but version N never requires it,
    /// because version N is still parsed leniently.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> FieldIntroducedAtVersion =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["skippedEventLines"] = 2,
        };

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

                // Versioned fields exist on TelemetryEvent for every known version, so strict
                // mode's UnmappedMemberHandling.Disallow alone cannot refuse a field at a
                // version earlier than the one that introduces it, nor require its presence
                // from that version on — the DTO declares the member regardless of version.
                // Check each one explicitly, only for known versions (lenient versions have
                // no presence rule at all).
                if (sv <= HighestKnownSchemaVersion)
                {
                    foreach (var (fieldName, introducedAt) in FieldIntroducedAtVersion)
                    {
                        var present = doc.RootElement.TryGetProperty(fieldName, out _);
                        if (introducedAt > sv && present)
                        {
                            return new ParseResult.Bad(
                                $"Line {i + 1}: field '{fieldName}' was introduced at schemaVersion {introducedAt}, not valid at schemaVersion {sv}");
                        }

                        if (introducedAt <= sv && !present)
                        {
                            return new ParseResult.Bad(
                                $"Line {i + 1}: field '{fieldName}' is required from schemaVersion {introducedAt}, missing at schemaVersion {sv}");
                        }
                    }
                }

                // DELIBERATE forward-compatibility: lenient parsing of schema versions above
                // HighestKnownSchemaVersion is intentional. An at-least-once engine client
                // emitting a future schema version must not be rejected forever; it is SAFE
                // because deserialisation targets the typed TelemetryEvent allowlist, so an
                // unknown/forbidden field has no property to bind to and can never be persisted.
                var opts = sv <= HighestKnownSchemaVersion ? StrictOptions : LenientOptions;
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
