using System.Security.Cryptography;
using System.Text;

namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>
/// Computes the idempotency key for an NDJSON batch, mirroring the engine's
/// <c>DrainingTelemetrySink.ComputeIdempotencyKey</c> algorithm exactly
/// (engine PR #155, issue #152 Phase B).
/// </summary>
/// <remarks>
/// Key = lowercase SHA-256 hex of the UTF-8 bytes of the batch lines joined by
/// <c>'\n'</c>.  Stable for identical content so a re-sent batch carries the same
/// key and the backend can de-duplicate it.
/// </remarks>
public static class IdempotencyKey
{
    /// <summary>
    /// Computes a lowercase SHA-256 hex idempotency key for the given batch lines.
    /// </summary>
    /// <param name="lines">The NDJSON lines that form the batch.</param>
    /// <returns>Lowercase hex SHA-256 of the UTF-8 encoding of the lines joined by <c>'\n'</c>.</returns>
    public static string Compute(IReadOnlyList<string> lines)
    {
        var body = string.Join('\n', lines);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
