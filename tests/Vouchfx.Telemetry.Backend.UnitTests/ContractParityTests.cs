using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Telemetry.Backend.Contracts;
using Vouchfx.Telemetry.Backend.Ingestion;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

/// <summary>
/// Proves that the copied contract DTOs are byte-compatible with the engine's wire format
/// (proven against golden fixtures; cross-references engine PR #155 and issue #152).
/// </summary>
public sealed class ContractParityTests
{
    // CA1869: cache the options instance rather than allocating on every test invocation.
    private static readonly JsonSerializerOptions RoundTripOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void DeserializeEventLineMatchesExpectedValues()
    {
        var json = File.ReadAllText(FixturePath("event-line.json"), new UTF8Encoding(false));
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal(1, evt.SchemaVersion);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), evt.InstallId);
        Assert.Equal(new DateTimeOffset(2026, 6, 28, 12, 34, 56, 789, TimeSpan.Zero), evt.Timestamp);
        Assert.Equal("1.0.0", evt.ToolVersion);
        Assert.Equal(".NET 8.0.7", evt.DotnetVersion);
        Assert.Equal(1, evt.RunCount);
        Assert.Equal(2, evt.ScenarioCount);
        Assert.Equal(5, evt.StepVerdicts.Pass);
        Assert.Equal(1, evt.StepVerdicts.Fail);
        Assert.Equal(3, evt.StepFamilies["http"]);
        Assert.Equal(1, evt.StepFamilies["db-assert"]);
        Assert.Equal(1, evt.StepFamilies["custom"]);
        Assert.Equal(3, evt.StepProviders["http.rest"]);
        Assert.Equal(1, evt.StepProviders["db-assert.postgres"]);
        Assert.Equal(1234L, evt.StartupMs);
        Assert.Equal(1500L, evt.TimeToFirstTestMs);
    }

    [Fact]
    public void DeserializeEventLine2MatchesMqPublishFamilies()
    {
        var json = File.ReadAllText(FixturePath("event-line-2.json"), new UTF8Encoding(false));
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal(1, evt.StepFamilies["mq-publish"]);
        Assert.Equal(1, evt.StepProviders["mq-publish.kafka"]);
    }

    [Fact]
    public void DeserializeEventLineV2MatchesExpectedSkippedEventLines()
    {
        var json = File.ReadAllText(FixturePath("event-line-v2.json"), new UTF8Encoding(false));
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal(2, evt.SchemaVersion);
        Assert.Equal(3, evt.SkippedEventLines);
    }

    [Fact]
    public void RoundTripEventLineV2ProducesIdenticalBytes()
    {
        var original = File.ReadAllText(FixturePath("event-line-v2.json"), new UTF8Encoding(false)).TrimEnd('\n');
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(original);
        Assert.NotNull(evt);

        var reserialized = JsonSerializer.Serialize(evt, RoundTripOptions);

        Assert.Equal(original, reserialized);
    }

    [Fact]
    public void RoundTripEventLineV2ZeroSkippedEventLinesProducesIdenticalBytes()
    {
        // skippedEventLines:0 is the common case on real runs (most runs skip nothing);
        // this pins that the zero count round-trips byte-identically too, not just a
        // non-zero one — the engine always writes the field, including 0.
        var original = File.ReadAllText(FixturePath("event-line-v2-zero.json"), new UTF8Encoding(false)).TrimEnd('\n');
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(original);
        Assert.NotNull(evt);

        var reserialized = JsonSerializer.Serialize(evt, RoundTripOptions);

        Assert.Equal(original, reserialized);
    }

    [Fact]
    public void IdempotencyKeyBatchSingleMatchesExpected()
    {
        var keys = ParseExpectedKeys();
        var raw = File.ReadAllText(FixturePath("batch-single.ndjson"), new UTF8Encoding(false));
        var lines = SplitLines(raw);

        var key = IdempotencyKey.Compute(lines);

        Assert.Equal(keys["batch-single"], key);
    }

    [Fact]
    public void IdempotencyKeyBatchTwoMatchesExpected()
    {
        var keys = ParseExpectedKeys();
        var raw = File.ReadAllText(FixturePath("batch-two.ndjson"), new UTF8Encoding(false));
        var lines = SplitLines(raw);

        var key = IdempotencyKey.Compute(lines);

        Assert.Equal(keys["batch-two"], key);
    }

    [Fact]
    public void RoundTripEventLineV1AppendsSkippedEventLinesZero()
    {
        // The DTO now models schemaVersion 2, and the engine always writes
        // skippedEventLines (0 included) — so re-serializing the v1 fixture is NOT
        // byte-identical to its source: it reproduces the v1 bytes with
        // ,"skippedEventLines":0 inserted before the closing brace.
        var original = File.ReadAllText(FixturePath("event-line.json"), new UTF8Encoding(false)).TrimEnd('\n');
        var evt = JsonSerializer.Deserialize<TelemetryEvent>(original);
        Assert.NotNull(evt);

        var expected = original[..^1] + ",\"skippedEventLines\":0}";
        var reserialized = JsonSerializer.Serialize(evt, RoundTripOptions);

        Assert.Equal(expected, reserialized);
    }

    // Split raw NDJSON text on '\n', dropping a trailing empty entry produced by a
    // trailing newline character (the fixtures have no trailing newline, but we defend
    // against one anyway so the helper is correct for either form).
    private static string[] SplitLines(string text)
    {
        var parts = text.Split('\n');
        if (parts.Length > 0 && parts[^1] == string.Empty)
        {
            return parts[..^1];
        }

        return parts;
    }

    // Parse the "key=value" pairs from expected-keys.txt so the fixture is the single
    // source of truth for the expected hash values.
    private static Dictionary<string, string> ParseExpectedKeys()
    {
        var lines = File.ReadAllLines(FixturePath("expected-keys.txt"));
        var dict = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var eqIdx = line.IndexOf('=', StringComparison.Ordinal);
            if (eqIdx >= 0)
            {
                dict[line[..eqIdx]] = line[(eqIdx + 1)..];
            }
        }

        return dict;
    }
}
