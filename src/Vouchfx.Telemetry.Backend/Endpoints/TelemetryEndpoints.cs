using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Vouchfx.Telemetry.Backend.Configuration;
using Vouchfx.Telemetry.Backend.Ingestion;
using Vouchfx.Telemetry.Backend.Persistence;
using Vouchfx.Telemetry.Backend.Security;

namespace Vouchfx.Telemetry.Backend.Endpoints;

/// <summary>
/// Registers the telemetry ingest endpoints on the application route builder.
/// </summary>
public static partial class TelemetryEndpoints
{
    // Static classes cannot be used as ILogger<T> type arguments; this nested
    // non-static type serves as the logger category marker.
    private sealed class IngestLogCategory { }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexKeyRegex();

    // CA1848: use LoggerMessage source-generated delegates for all log calls.
    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected malformed batch: {Reason}")]
    private static partial void LogMalformedBatch(ILogger<IngestLogCategory> logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingested batch {IdempotencyKey}: {NewRows} new row(s), {TotalEvents} event(s).")]
    private static partial void LogIngested(ILogger<IngestLogCategory> logger, string idempotencyKey, int newRows, int totalEvents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient storage fault ingesting batch {IdempotencyKey}.")]
    private static partial void LogTransientFault(ILogger<IngestLogCategory> logger, Exception ex, string idempotencyKey);

    /// <summary>
    /// Maps <c>POST /v1/telemetry</c> with Bearer auth and rate-limiting applied.
    /// </summary>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/telemetry", HandleIngestAsync)
            .AddEndpointFilter<BearerAuthEndpointFilter>()
            .RequireRateLimiting("bearer-token");

        return app;
    }

    private static async Task<IResult> HandleIngestAsync(
        HttpContext httpContext,
        IOptions<TelemetryOptions> options,
        ITelemetryRepository repository,
        ILogger<IngestLogCategory> logger,
        CancellationToken ct)
    {
        // 1. Content-Type: must be application/x-ndjson (ignore charset params).
        var contentType = httpContext.Request.ContentType ?? string.Empty;
        var mediaType = contentType.Split(';')[0].Trim();
        if (!string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        // 2. Idempotency-Key: required, exactly 64 lowercase hex chars.
        // The client-supplied Idempotency-Key is authoritative: it is the SHA-256 of the
        // exact request body the client sends. The server validates its 64-hex format and
        // uses it as the dedup key but does NOT recompute it, keeping the backend decoupled
        // from the client's hashing algorithm. IdempotencyKey.Compute is retained as the
        // contract-parity test lock (ContractParityTests) only.
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(idempotencyKey) || !HexKeyRegex().IsMatch(idempotencyKey))
        {
            return Results.BadRequest("Invalid or missing Idempotency-Key header.");
        }

        // 3. Read body (Kestrel enforces MaxRequestBodySize; oversized bodies throw BadHttpRequestException).
        string body;
        try
        {
            using var reader = new StreamReader(httpContext.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync(ct);
        }
        catch (BadHttpRequestException ex)
        {
            // Only map 413 when Kestrel itself signals body-too-large; other
            // BadHttpRequestException status codes (e.g. 400 malformed request line)
            // must not be mis-reported as 413.
            return ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge)
                : Results.BadRequest("Request could not be processed.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Results.BadRequest("Request body is empty.");
        }

        // 4. Split and parse.
        var lines = NdjsonReader.SplitLines(body);
        var opts = options.Value;
        var parseResult = AllowlistParser.Parse(lines, opts.MaxBatchLines);

        return parseResult switch
        {
            ParseResult.Empty => Results.BadRequest("No events in body."),
            ParseResult.TooManyLines => Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge),
            ParseResult.Bad bad => await LogAndBadRequestAsync(bad, logger),
            ParseResult.Ok ok => await IngestOkAsync(ok.Events, idempotencyKey, repository, logger, ct),
            _ => throw new InvalidOperationException($"Unexpected ParseResult type: {parseResult.GetType().Name}")
        };
    }

    private static Task<IResult> LogAndBadRequestAsync(ParseResult.Bad bad, ILogger<IngestLogCategory> logger)
    {
        // Log the parse reason server-side only; never surface it to the caller.
        LogMalformedBatch(logger, bad.Reason);
        return Task.FromResult(Results.BadRequest("Malformed event data."));
    }

    private static async Task<IResult> IngestOkAsync(
        IReadOnlyList<Contracts.TelemetryEvent> events,
        string idempotencyKey,
        ITelemetryRepository repository,
        ILogger<IngestLogCategory> logger,
        CancellationToken ct)
    {
        try
        {
            var newRows = await repository.IngestAsync(events, idempotencyKey, ct);
            LogIngested(logger, idempotencyKey, newRows, events.Count);
            return Results.Ok();
        }
        catch (TransientStorageException ex)
        {
            LogTransientFault(logger, ex, idempotencyKey);
            return new ServiceUnavailableResult();
        }
    }
}

/// <summary>
/// Returns HTTP 503 with a <c>Retry-After: 30</c> header and no body.
/// </summary>
internal sealed class ServiceUnavailableResult : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "30";
        return Task.CompletedTask;
    }
}
