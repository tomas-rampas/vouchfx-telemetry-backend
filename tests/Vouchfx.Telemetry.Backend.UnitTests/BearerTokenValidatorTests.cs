// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707
using Microsoft.Extensions.Options;
using Vouchfx.Telemetry.Backend.Configuration;
using Vouchfx.Telemetry.Backend.Security;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

public sealed class BearerTokenValidatorTests
{
    private static BearerTokenValidator Create(params string[] tokens)
    {
        var options = Options.Create(new TelemetryOptions { IngestTokens = tokens });
        return new BearerTokenValidator(options);
    }

    [Fact]
    public void EmptyTokenSet_AlwaysReturnsFalse()
    {
        var v = Create();
        Assert.False(v.IsValid("Bearer anytoken"));
    }

    [Fact]
    public void ValidToken_ReturnsTrue()
    {
        var v = Create("secret-token");
        Assert.True(v.IsValid("Bearer secret-token"));
    }

    [Fact]
    public void WrongToken_ReturnsFalse()
    {
        var v = Create("secret-token");
        Assert.False(v.IsValid("Bearer wrong-token"));
    }

    [Fact]
    public void NullHeader_ReturnsFalse()
    {
        var v = Create("secret-token");
        Assert.False(v.IsValid(null));
    }

    [Fact]
    public void EmptyHeader_ReturnsFalse()
    {
        var v = Create("secret-token");
        Assert.False(v.IsValid(string.Empty));
    }

    [Fact]
    public void WrongScheme_ReturnsFalse()
    {
        var v = Create("secret-token");
        Assert.False(v.IsValid("Basic c2VjcmV0LXRva2Vu"));
    }

    [Fact]
    public void BearerWithEmptyToken_ReturnsFalse()
    {
        var v = Create("secret-token");
        Assert.False(v.IsValid("Bearer "));
    }

    [Fact]
    public void MultipleTokens_AllAccepted()
    {
        var v = Create("token1", "token2", "token3");
        Assert.True(v.IsValid("Bearer token1"));
        Assert.True(v.IsValid("Bearer token2"));
        Assert.True(v.IsValid("Bearer token3"));
        Assert.False(v.IsValid("Bearer token4"));
    }

    [Theory]
    [InlineData("bearer secret-token")]
    [InlineData("BEARER secret-token")]
    [InlineData("Bearer secret-token")]
    public void CaseInsensitiveScheme_ReturnsTrue(string header)
    {
        var v = Create("secret-token");
        Assert.True(v.IsValid(header));
    }
}
