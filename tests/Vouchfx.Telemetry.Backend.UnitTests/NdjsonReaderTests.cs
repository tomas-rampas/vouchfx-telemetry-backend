// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707
using Vouchfx.Telemetry.Backend.Ingestion;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

public sealed class NdjsonReaderTests
{
    [Fact]
    public void SplitLines_SingleLine_ReturnsOneLine()
    {
        var lines = NdjsonReader.SplitLines("{\"a\":1}");
        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void SplitLines_TrailingNewline_Dropped()
    {
        var lines = NdjsonReader.SplitLines("{\"a\":1}\n");
        Assert.Single(lines);
    }

    [Fact]
    public void SplitLines_TwoLines_ReturnsBoth()
    {
        var lines = NdjsonReader.SplitLines("{\"a\":1}\n{\"b\":2}");
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void SplitLines_CrlfLineEndings_StripsCarriageReturn()
    {
        var lines = NdjsonReader.SplitLines("{\"a\":1}\r\n{\"b\":2}\r\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("{\"a\":1}", lines[0]);
        Assert.Equal("{\"b\":2}", lines[1]);
    }

    [Fact]
    public void SplitLines_BlankLines_Dropped()
    {
        var lines = NdjsonReader.SplitLines("\n\n{\"a\":1}\n\n");
        Assert.Single(lines);
    }

    [Fact]
    public void SplitLines_WhitespaceOnlyLines_Dropped()
    {
        var lines = NdjsonReader.SplitLines("   \n{\"a\":1}\n   ");
        Assert.Single(lines);
    }
}
