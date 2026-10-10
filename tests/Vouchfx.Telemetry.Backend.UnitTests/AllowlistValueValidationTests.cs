// CA1707: underscore-separated names are the xUnit naming convention.
#pragma warning disable CA1707
using System.Text.Json.Nodes;
using Vouchfx.Telemetry.Backend.Ingestion;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

public sealed class AllowlistValueValidationTests
{
    private static readonly string ValidLine = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line-v2.json")).TrimEnd('\r', '\n');

    [Theory]
    [InlineData("[1]")]
    [InlineData("1")]
    [InlineData("\"x\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void NonObjectRoot_ReturnsBadWithoutThrowing(string line)
    {
        var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([line], 500));
        Assert.Equal("Line 1: event must be a JSON object", bad.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DeserializationFailure_ReasonDoesNotEchoInput(int schemaVersion)
    {
        var secretKey = "jane.doe@example.com" + new string('x', 100_000);
        var node = JsonNode.Parse(ValidLine)!.AsObject();
        node["schemaVersion"] = schemaVersion;
        if (schemaVersion == 1) node.Remove("skippedEventLines");
        // Invalid map value makes STJ include a customer-chosen key in its JsonException.Path.
        node["stepFamilies"] = new JsonObject { [secretKey] = "not-a-count" };
        var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([node.ToJsonString()], 500));
        Assert.Equal("Line 1: invalid JSON or event shape", bad.Reason);
        Assert.DoesNotContain("jane.doe", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedLiteral_ReasonDoesNotEchoRawLine()
    {
        const string Line = "{\"schemaVersion\":2,\"timestamp\":nope-secret@example.com}";
        var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([Line], 500));
        Assert.Equal("Line 1: invalid JSON or event shape", bad.Reason);
    }

    [Theory]
    [InlineData("stepFamilies", "jane.doe@example.com")]
    [InlineData("stepFamilies", "HTTP")]
    [InlineData("stepFamilies", "my-provider")]
    [InlineData("stepProviders", "rpc.json-rpc")]
    [InlineData("stepProviders", "http.customer")]
    [InlineData("stepProviders", "jane.doe@example.com")]
    public void UnknownTaxonomyKey_ReturnsBadForEverySchemaVersion(string field, string key)
    {
        for (var version = 1; version <= 3; version++)
        {
            var node = JsonNode.Parse(ValidLine)!.AsObject();
            node["schemaVersion"] = version;
            if (version == 1) node.Remove("skippedEventLines");
            node[field] = new JsonObject { [key] = 1 };
            var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([node.ToJsonString()], 500));
            Assert.Contains("unknown taxonomy key", bad.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(key, bad.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("runCount")]
    [InlineData("scenarioCount")]
    [InlineData("stepVerdicts.pass")]
    [InlineData("stepVerdicts.fail")]
    [InlineData("stepVerdicts.envError")]
    [InlineData("stepVerdicts.inconclusive")]
    [InlineData("scenarioVerdicts.pass")]
    [InlineData("scenarioVerdicts.fail")]
    [InlineData("scenarioVerdicts.envError")]
    [InlineData("scenarioVerdicts.inconclusive")]
    [InlineData("stepFamilies.http")]
    [InlineData("stepProviders.http.rest")]
    [InlineData("startupMs")]
    [InlineData("timeToFirstTestMs")]
    [InlineData("skippedEventLines")]
    public void NegativeCountOrTiming_ReturnsBad(string field)
    {
        var node = JsonNode.Parse(ValidLine)!.AsObject();
        var separator = field.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0) node[field] = -1;
        else node[field[..separator]]![field[(separator + 1)..]] = -1;
        var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([node.ToJsonString()], 500));
        Assert.Contains("non-negative", bad.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("toolVersion")]
    [InlineData("engineVersion")]
    [InlineData("dotnetVersion")]
    [InlineData("stepVerdicts")]
    [InlineData("scenarioVerdicts")]
    [InlineData("stepFamilies")]
    [InlineData("stepProviders")]
    public void NullRequiredValue_ReturnsBad(string field)
    {
        var node = JsonNode.Parse(ValidLine)!.AsObject();
        node[field] = null;
        var bad = Assert.IsType<ParseResult.Bad>(AllowlistParser.Parse([node.ToJsonString()], 500));
        Assert.Contains("required values must not be null", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroCountsAndTimings_EmptyMaps_AreAccepted()
    {
        var node = JsonNode.Parse(ValidLine)!.AsObject();
        foreach (var field in new[] { "runCount", "scenarioCount", "startupMs", "timeToFirstTestMs", "skippedEventLines" })
            node[field] = 0;
        foreach (var field in new[] { "stepVerdicts", "scenarioVerdicts" })
            node[field] = new JsonObject { ["pass"] = 0, ["fail"] = 0, ["envError"] = 0, ["inconclusive"] = 0 };
        node["stepFamilies"] = new JsonObject();
        node["stepProviders"] = new JsonObject();
        Assert.IsType<ParseResult.Ok>(AllowlistParser.Parse([node.ToJsonString()], 500));
    }
}
