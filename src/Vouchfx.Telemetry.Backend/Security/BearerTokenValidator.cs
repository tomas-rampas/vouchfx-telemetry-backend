using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vouchfx.Telemetry.Backend.Configuration;

namespace Vouchfx.Telemetry.Backend.Security;

/// <summary>
/// Validates Bearer tokens for ingest requests.
/// All comparisons use constant-time equality to prevent timing oracles.
/// The token value is never logged or echoed.
/// </summary>
public sealed class BearerTokenValidator
{
    // Pre-compute UTF-8 bytes of each configured token once at startup.
    private readonly byte[][] _tokenBytes;

    /// <summary>
    /// Initialises the validator from the configured <see cref="TelemetryOptions.IngestTokens"/>.
    /// </summary>
    public BearerTokenValidator(IOptions<TelemetryOptions> options)
    {
        _tokenBytes = options.Value.IngestTokens
            .Select(t => Encoding.UTF8.GetBytes(t))
            .ToArray();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="authorizationHeaderValue"/>
    /// carries a valid "Bearer &lt;token&gt;" header that matches one of the configured tokens.
    /// </summary>
    /// <remarks>
    /// Iterates ALL configured tokens without short-circuiting so the total comparison
    /// time does not leak whether a partial match was found.
    /// </remarks>
    public bool IsValid(string? authorizationHeaderValue)
    {
        if (_tokenBytes.Length == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(authorizationHeaderValue))
        {
            return false;
        }

        if (!authorizationHeaderValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorizationHeaderValue["Bearer ".Length..];
        if (token.Length == 0)
        {
            return false;
        }

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var matched = false;

        // CRITICAL: iterate ALL tokens without early exit on match to prevent a
        // count/timing oracle that would reveal how many tokens are configured and
        // whether a near-miss occurred.
        foreach (var knownBytes in _tokenBytes)
        {
            if (CryptographicOperations.FixedTimeEquals(tokenBytes, knownBytes))
            {
                matched = true;
            }
        }

        return matched;
    }
}
