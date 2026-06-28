namespace Vouchfx.Telemetry.Backend.Ingestion;

/// <summary>
/// Splits raw NDJSON text into non-empty content lines.
/// </summary>
public static class NdjsonReader
{
    /// <summary>
    /// Splits <paramref name="body"/> on <c>'\n'</c>, strips trailing <c>'\r'</c> from
    /// each segment (CRLF tolerance), and drops lines that are empty or whitespace-only.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string body)
    {
        return body.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }
}
