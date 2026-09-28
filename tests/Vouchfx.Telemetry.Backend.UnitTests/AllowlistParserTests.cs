// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707
using System.Reflection;
using Vouchfx.Telemetry.Backend.Ingestion;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

public sealed class AllowlistParserTests
{
    private static readonly string ValidLine = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line.json")).TrimEnd('\r', '\n');

    // schemaVersion 2, WITH skippedEventLines — required for any schemaVersion-2 test that
    // must reach strict deserialization: a v2 line missing skippedEventLines is refused by
    // the field-presence guard before strict parsing ever runs (see
    // UnknownFieldAtSchemaVersion2_ReturnsBad's history).
    private static readonly string ValidLineV2 = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line-v2.json")).TrimEnd('\r', '\n');

    [Fact]
    public void EmptyLines_ReturnsEmpty()
    {
        var result = AllowlistParser.Parse([], 500);
        Assert.IsType<ParseResult.Empty>(result);
    }

    [Fact]
    public void TooManyLines_ReturnsTooManyLines()
    {
        var lines = Enumerable.Repeat(ValidLine, 6).ToList();
        var result = AllowlistParser.Parse(lines, 5);
        var r = Assert.IsType<ParseResult.TooManyLines>(result);
        Assert.Equal(6, r.Actual);
        Assert.Equal(5, r.Max);
    }

    [Fact]
    public void ValidLine_ReturnsOkWithOneEvent()
    {
        var result = AllowlistParser.Parse([ValidLine], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Single(ok.Events);
        Assert.Equal(1, ok.Events[0].SchemaVersion);
    }

    [Fact]
    public void ValidSchemaVersion1Line_SkippedEventLinesDefaultsToZero()
    {
        // schemaVersion 1 never carries skippedEventLines (issue #30); the DTO must
        // still bind it to 0 rather than leaving it unset.
        var result = AllowlistParser.Parse([ValidLine], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Equal(0, ok.Events[0].SkippedEventLines);
    }

    [Fact]
    public void MalformedJson_ReturnsBad()
    {
        var result = AllowlistParser.Parse(["{not json}"], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        // Unambiguous by construction: JsonDocument.Parse is the FIRST operation on the
        // line, so nothing schemaVersion-related can have run yet — proven by the reason
        // never mentioning schemaVersion, not just by the result being Bad.
        Assert.DoesNotContain("schemaVersion", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSchemaVersion_ReturnsBad()
    {
        var result = AllowlistParser.Parse(["{\"timestamp\":\"2026-01-01T00:00:00Z\"}"], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("missing schemaVersion", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionZero_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":0,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("schemaVersion must be >= 1", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionNegative_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":-1,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("schemaVersion must be >= 1", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionNotInteger_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":\"one\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("schemaVersion is not an integer", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredField_ReturnsBad()
    {
        var line = ValidLine.Replace(",\"toolVersion\":\"1.0.0\"", string.Empty, StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        // Proves this reached STJ's required-member check (not the field-presence guard,
        // which only knows about skippedEventLines): the reason names the missing member.
        Assert.Contains("toolVersion", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFieldAtSchemaVersion1_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"unknownField\":\"surprise\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        // Proves Disallow caught it (names the unmapped property), not some other guard.
        Assert.Contains("unknownField", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedEventLinesAtSchemaVersion1_ReturnsBad()
    {
        // skippedEventLines is introduced at schemaVersion 2 (FieldIntroducedAtVersion);
        // a version 1 line carrying it is refused by the field-presence guard, not by
        // UnmappedMemberHandling.Disallow — the DTO declares the member for every version.
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"skippedEventLines\":2,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        // Pins the guard's own wording, not just "some Bad reason" — if Disallow had caught
        // it instead this would say "could not be mapped", not "introduced at".
        Assert.Contains("field 'skippedEventLines' was introduced at schemaVersion 2, not valid at schemaVersion 1", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedEventLinesAtSchemaVersion0_ReturnsBad()
    {
        // schemaVersion 0 is refused by the "schemaVersion must be >= 1" guard, which runs
        // BEFORE the field-presence guard and before strict/lenient mode is chosen —
        // unaffected by HighestKnownSchemaVersion. Pin both that it is Bad AND the reason:
        // a version-0 line carrying skippedEventLines is refused for THAT reason (not
        // treated as a field-presence violation, and not accepted leniently).
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":0,\"skippedEventLines\":2,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("schemaVersion must be >= 1", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentCaseSkippedEventLinesKeyAtSchemaVersion1_ReturnsBad()
    {
        // "SkippedEventLines" (capital S) does not match the explicit
        // TryGetProperty("skippedEventLines") guard (case-sensitive exact match), so this
        // falls through to strict deserialization. Neither StrictOptions nor LenientOptions
        // sets PropertyNameCaseInsensitive, so System.Text.Json's case-sensitive default
        // applies there too: the property does not bind to [JsonPropertyName("skippedEventLines")]
        // and UnmappedMemberHandling.Disallow refuses it as a genuinely unmapped field.
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"SkippedEventLines\":2,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        // Discriminates the two possible causes of Bad here: the field-presence guard's own
        // message only ever says "skippedEventLines" (lowercase, from its own table key),
        // never "SkippedEventLines" — so this reason can only come from Disallow echoing the
        // JSON property text verbatim. If a future change made the guard case-insensitive
        // and it caught this first, this line's Bad would come from the guard instead and
        // this assertion would fail — exactly the wrong-reason-pass class of bug.
        Assert.Contains("SkippedEventLines", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedSkippedEventLinesKeyAtSchemaVersion1_ReturnsBad()
    {
        // The property name is written with its leading 's' as the JSON unicode escape for
        // code point U+0073 — logically identical to "skippedEventLines", but not that
        // literal byte sequence on the wire. JsonElement.TryGetProperty unescapes before comparing
        // property names, so AllowlistParser.Parse's field-presence guard must still catch
        // this (it cannot rely on a literal substring match).
        //
        // The backslash is built from its code point rather than typed as a literal `\u`
        // sequence in this file's own source, so the escape reaches JsonDocument.Parse
        // intact instead of being pre-decoded into a plain 's' by whatever wrote this file.
        var backslash = ((char)0x5C).ToString();
        var escapedKey = backslash + "u0073kippedEventLines";
        var line = ValidLine.Replace(
            "\"schemaVersion\":1,",
            $"\"schemaVersion\":1,\"{escapedKey}\":2,",
            StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("field 'skippedEventLines' was introduced at schemaVersion 2, not valid at schemaVersion 1", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFieldAtSchemaVersion2_ReturnsBad()
    {
        // Reverses the pre-#30 premise: schemaVersion 2 is now a KNOWN version (strict
        // parsing), so an unknown field at v2 is refused just like at v1.
        //
        // MUST be built from ValidLineV2 (carries skippedEventLines), not ValidLine with
        // schemaVersion overwritten to 2: a v2 line WITHOUT skippedEventLines is refused by
        // the field-presence guard before strict parsing ever runs, so such a line is
        // refused whether or not version 2 is parsed strictly. Built that way, this test
        // stayed green under a mutant that parsed version 2 leniently. Asserting that the
        // reason names futureField pins that this test exercises Disallow, not the presence
        // guard.
        var line = ValidLineV2.Replace(
            "\"schemaVersion\":2,", "\"schemaVersion\":2,\"futureField\":\"ignored\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("futureField", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedEventLinesMissingAtSchemaVersion2_ReturnsBad()
    {
        // Rewritten (was ValidSchemaVersion2Line_WithoutSkippedEventLines_..._DefaultsToZero,
        // which accepted this and defaulted to 0). The engine marks skippedEventLines
        // `required` from schemaVersion 2, so every version-2 line the engine actually sends
        // carries it, 0 included; a version-2 line without it no longer defaults — it is
        // refused, because "strict for a known version" means the engine's exact shape.
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":2,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var bad = Assert.IsType<ParseResult.Bad>(result);
        Assert.Contains("field 'skippedEventLines' is required from schemaVersion 2, missing at schemaVersion 2", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidSchemaVersion2Line_WithSkippedEventLines_ParsesValue()
    {
        var line = ValidLine
            .Replace("\"schemaVersion\":1,", "\"schemaVersion\":2,\"skippedEventLines\":3,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Equal(3, ok.Events[0].SkippedEventLines);
    }

    [Fact]
    public void UnknownFieldAtSchemaVersion3_Accepted()
    {
        // schemaVersion 3 is above the highest version this backend knows (2), so
        // forward-compatible lenient parsing still applies there.
        var line = ValidLine
            .Replace("\"schemaVersion\":1,", "\"schemaVersion\":3,", StringComparison.Ordinal)
            .Replace("\"schemaVersion\":3,", "\"schemaVersion\":3,\"futureField\":\"ignored\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Single(ok.Events);
    }

    [Fact]
    public void SchemaVersion3WithoutSkippedEventLines_AcceptedStoresZero()
    {
        // schemaVersion 3 is above HighestKnownSchemaVersion, so no field-presence rule
        // applies there (lenient mode): omitting skippedEventLines is accepted, unlike at
        // schemaVersion 2, and it stores 0.
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":3,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Equal(0, ok.Events[0].SkippedEventLines);
    }

    [Fact]
    public void FieldIntroducedAtVersion_NeverExceedsHighestKnownSchemaVersion()
    {
        // Guards the exact drift the maintenance note on FieldIntroducedAtVersion warns
        // about: strict parsing and the presence rule only run at
        // sv <= HighestKnownSchemaVersion, so an entry whose version exceeds it is only half
        // enforced: every known version refuses the field, but its own version never
        // requires it until someone separately raises HighestKnownSchemaVersion too.
        var field = typeof(AllowlistParser).GetField(
            "FieldIntroducedAtVersion", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FieldIntroducedAtVersion field not found via reflection.");
        var table = (IReadOnlyDictionary<string, int>)field.GetValue(null)!;

        Assert.All(table, kvp => Assert.True(
            kvp.Value <= AllowlistParser.HighestKnownSchemaVersion,
            $"'{kvp.Key}' is introduced at schemaVersion {kvp.Value}, which exceeds " +
            $"HighestKnownSchemaVersion ({AllowlistParser.HighestKnownSchemaVersion}) and " +
            "would therefore never be enforced."));
    }

    [Fact]
    public void TwoValidLines_ReturnsOkWithTwoEvents()
    {
        var line2 = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line-2.json")).TrimEnd('\r', '\n');
        var result = AllowlistParser.Parse([ValidLine, line2], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Equal(2, ok.Events.Count);
    }
}
