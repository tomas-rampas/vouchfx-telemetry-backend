// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707
using Vouchfx.Telemetry.Backend.Ingestion;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

public sealed class AllowlistParserTests
{
    private static readonly string ValidLine = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line.json")).TrimEnd('\r', '\n');

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
    public void MalformedJson_ReturnsBad()
    {
        var result = AllowlistParser.Parse(["{not json}"], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void MissingSchemaVersion_ReturnsBad()
    {
        var result = AllowlistParser.Parse(["{\"timestamp\":\"2026-01-01T00:00:00Z\"}"], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void SchemaVersionZero_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":0,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void SchemaVersionNegative_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":-1,", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void SchemaVersionNotInteger_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":\"one\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void MissingRequiredField_ReturnsBad()
    {
        var line = ValidLine.Replace(",\"toolVersion\":\"1.0.0\"", string.Empty, StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void UnknownFieldAtSchemaVersion1_ReturnsBad()
    {
        var line = ValidLine.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"unknownField\":\"surprise\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        Assert.IsType<ParseResult.Bad>(result);
    }

    [Fact]
    public void UnknownFieldAtSchemaVersion2_Accepted()
    {
        // Switch to schemaVersion=2 (lenient mode) and add an unknown field.
        var line = ValidLine
            .Replace("\"schemaVersion\":1,", "\"schemaVersion\":2,", StringComparison.Ordinal)
            .Replace("\"schemaVersion\":2,", "\"schemaVersion\":2,\"futureField\":\"ignored\",", StringComparison.Ordinal);
        var result = AllowlistParser.Parse([line], 500);
        var ok = Assert.IsType<ParseResult.Ok>(result);
        Assert.Single(ok.Events);
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
