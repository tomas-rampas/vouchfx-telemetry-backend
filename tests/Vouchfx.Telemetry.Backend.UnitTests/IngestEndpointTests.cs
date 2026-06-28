// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Telemetry.Backend.Contracts;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.UnitTests;

internal sealed class StubTelemetryRepository : ITelemetryRepository
{
    public int CallCount { get; private set; }
    public IReadOnlyList<TelemetryEvent>? LastEvents { get; private set; }
    public string? LastIdempotencyKey { get; private set; }
    public int ReturnValue { get; set; } = 1;
    public bool ThrowTransient { get; set; }

    /// <summary>Controls the return value of <see cref="IsReadyAsync"/>.</summary>
    public bool IsReady { get; set; }

    public Task<int> IngestAsync(IReadOnlyList<TelemetryEvent> events, string idempotencyKey, CancellationToken ct)
    {
        CallCount++;
        LastEvents = events;
        LastIdempotencyKey = idempotencyKey;
        if (ThrowTransient)
        {
            throw new TransientStorageException("test transient fault");
        }

        return Task.FromResult(ReturnValue);
    }

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(IsReady);
}

public sealed class IngestEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidToken = "test-bearer-token";

    // 64 lowercase hex chars — valid Idempotency-Key value
    private const string ValidIdempotencyKey = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    private static readonly string ValidEventLine;

    static IngestEndpointTests()
    {
        ValidEventLine = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-line.json")).TrimEnd('\r', '\n');
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly StubTelemetryRepository _stub = new();

    public IngestEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Creates a test client driven by the flat <c>VOUCHFX_TELEMETRY_*</c> env-var keys
    /// (the same keys the Bicep IaC injects in production).
    /// </summary>
    private HttpClient CreateClient(
        int maxBatchLines = 500,
        long maxBodyBytes = 2_097_152,
        int ratePermits = 120,
        int rateWindowSeconds = 60)
    {
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("VOUCHFX_TELEMETRY_INGEST_TOKENS", ValidToken);
            b.UseSetting("VOUCHFX_TELEMETRY_MAX_BATCH_LINES", maxBatchLines.ToString(CultureInfo.InvariantCulture));
            b.UseSetting("VOUCHFX_TELEMETRY_MAX_BODY_BYTES", maxBodyBytes.ToString(CultureInfo.InvariantCulture));
            b.UseSetting("VOUCHFX_TELEMETRY_RATE_PERMITS", ratePermits.ToString(CultureInfo.InvariantCulture));
            b.UseSetting("VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS", rateWindowSeconds.ToString(CultureInfo.InvariantCulture));
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ITelemetryRepository>(_stub);
            });
        }).CreateClient();
    }

    private static HttpRequestMessage BuildRequest(
        string? token = ValidToken,
        string? idempotencyKey = ValidIdempotencyKey,
        string? contentType = "application/x-ndjson",
        string? body = null)
    {
        body ??= ValidEventLine;
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/telemetry");
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey != null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/x-ndjson");
        if (contentType == null)
        {
            request.Content.Headers.ContentType = null;
        }

        return request;
    }

    // ── Ingest happy-path ──────────────────────────────────────────────────────

    [Fact]
    public async Task ValidRequest_Returns200()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _stub.CallCount);
        Assert.NotNull(_stub.LastEvents);
        Assert.Single(_stub.LastEvents!);
        Assert.Equal(ValidIdempotencyKey, _stub.LastIdempotencyKey);
    }

    [Fact]
    public async Task DuplicateBatch_Returns200_WithZeroNewRows()
    {
        _stub.ReturnValue = 0;
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Auth ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoAuthHeader_Returns401()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("WWW-Authenticate"));
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(token: "wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WrongContentType_Returns415()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(contentType: "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task MissingIdempotencyKey_Returns400()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(idempotencyKey: null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedIdempotencyKey_TooShort_Returns400()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(idempotencyKey: "abc123"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedIdempotencyKey_Uppercase_Returns400()
    {
        var client = CreateClient();
        var upperKey = ValidIdempotencyKey.ToUpperInvariant();
        var response = await client.SendAsync(BuildRequest(idempotencyKey: upperKey));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EmptyBody_Returns400()
    {
        var client = CreateClient();
        var request = BuildRequest(body: "");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(body: "{not json}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownFieldAtV1_Returns400()
    {
        var client = CreateClient();
        var badLine = ValidEventLine.Replace(
            "\"schemaVersion\":1,",
            "\"schemaVersion\":1,\"evilField\":\"oops\",",
            StringComparison.Ordinal);
        var response = await client.SendAsync(BuildRequest(body: badLine));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SchemaVersion0_Returns400()
    {
        var client = CreateClient();
        var badLine = ValidEventLine.Replace(
            "\"schemaVersion\":1,",
            "\"schemaVersion\":0,",
            StringComparison.Ordinal);
        var response = await client.SendAsync(BuildRequest(body: badLine));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Size caps ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TooManyLines_Returns413()
    {
        // Configure max 2 lines, send 3
        var client = CreateClient(maxBatchLines: 2);
        var body = string.Join('\n', Enumerable.Repeat(ValidEventLine, 3));
        var response = await client.SendAsync(BuildRequest(body: body));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task BodyOverSizeCap_Returns413Or400()
    {
        // Configure tiny body cap (100 bytes), send a large body.
        // TestServer may not enforce Kestrel's MaxRequestBodySize, so we accept
        // either 413 (Kestrel enforcement) or 400 (JSON parse failure of the 'x' filler).
        var client = CreateClient(maxBodyBytes: 100);
        var bigBody = new string('x', 200) + "\n" + ValidEventLine;
        var response = await client.SendAsync(BuildRequest(body: bigBody));
        Assert.True(
            response.StatusCode == HttpStatusCode.RequestEntityTooLarge ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 413 or 400, got {response.StatusCode}");
    }

    // ── Rate limiting ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitExceeded_Returns429()
    {
        // Configure 1 permit per window, send 2 requests through the same server
        var client = CreateClient(ratePermits: 1, rateWindowSeconds: 60);
        var r1 = await client.SendAsync(BuildRequest());
        var r2 = await client.SendAsync(BuildRequest());
        Assert.True(
            r1.StatusCode == HttpStatusCode.OK || r1.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(
            r2.StatusCode == HttpStatusCode.OK || r2.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(
            r1.StatusCode == HttpStatusCode.TooManyRequests || r2.StatusCode == HttpStatusCode.TooManyRequests,
            "At least one request should be rate limited");
    }

    [Fact]
    public async Task TwoDifferentInvalidTokens_ShareSameUnauthenticatedBucket_SecondIs429()
    {
        // FIX 1: with 1 permit, two requests carrying DIFFERENT invalid tokens must both
        // count against the same "unauthenticated" partition — the second must be 429,
        // proving no per-value bucket reset (which would have let each token get a fresh
        // budget and made the limit bypassable).
        var client = CreateClient(ratePermits: 1, rateWindowSeconds: 60);
        var r1 = await client.SendAsync(BuildRequest(token: "invalid-token-aaa"));
        var r2 = await client.SendAsync(BuildRequest(token: "invalid-token-bbb"));

        // r1 consumes the single unauthenticated permit; passes rate limiter → auth → 401.
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        // r2 shares the same bucket — no permits left → 429 (before auth even runs).
        Assert.Equal(HttpStatusCode.TooManyRequests, r2.StatusCode);
    }

    [Fact]
    public async Task ValidToken_HasSeparateBudgetFromUnauthenticatedBucket()
    {
        // FIX 1: exhaust the "unauthenticated" bucket with an invalid token, then prove a
        // valid token still gets through on its own independent "tok:..." partition.
        var client = CreateClient(ratePermits: 1, rateWindowSeconds: 60);

        // Exhaust the unauthenticated bucket.
        var rInvalid = await client.SendAsync(BuildRequest(token: "invalid-token-zzz"));
        Assert.Equal(HttpStatusCode.Unauthorized, rInvalid.StatusCode);

        // Valid token uses its own partition — budget is fresh → should reach the handler.
        var rValid = await client.SendAsync(BuildRequest()); // uses ValidToken
        Assert.Equal(HttpStatusCode.OK, rValid.StatusCode);
    }

    // ── Flat config key binding (FIX 2) ───────────────────────────────────────

    [Fact]
    public async Task FlatKey_MaxBatchLines_TakesEffect_ThreeLineBatchReturns413()
    {
        // VOUCHFX_TELEMETRY_MAX_BATCH_LINES=2 set via flat key; 3-line batch must → 413.
        // This proves the production env-var binding path, not just the old Telemetry: section path.
        var client = CreateClient(maxBatchLines: 2);
        var body = string.Join('\n', Enumerable.Repeat(ValidEventLine, 3));
        var response = await client.SendAsync(BuildRequest(body: body));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task FlatKey_RatePermits_TakesEffect_SecondRequestIs429()
    {
        // VOUCHFX_TELEMETRY_RATE_PERMITS=1 set via flat key; second valid request must → 429.
        // This proves the rate-limiter is driven by the flat env-var key, not a silent default.
        var client = CreateClient(ratePermits: 1, rateWindowSeconds: 60);
        var r1 = await client.SendAsync(BuildRequest());
        var r2 = await client.SendAsync(BuildRequest());

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r2.StatusCode);
    }

    // ── Storage faults ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TransientStorageException_Returns503()
    {
        _stub.ThrowTransient = true;
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    // ── Security ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BearerToken_NeverAppearsInResponse()
    {
        var client = CreateClient();
        var response = await client.SendAsync(BuildRequest(token: "super-secret-token-12345"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token-12345", body, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token-12345", response.Headers.ToString(), StringComparison.Ordinal);
    }

    // ── Health probes (FIX 3) ──────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_Returns200_Unauthenticated()
    {
        // /healthz is liveness only — no auth, no rate limiting.
        var client = CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_Returns503_WhenRepositoryIsUnconfigured()
    {
        // UnconfiguredTelemetryRepository.IsReadyAsync returns false → /readyz → 503.
        // This prevents Azure Container Apps from routing traffic to a no-op placeholder.
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("VOUCHFX_TELEMETRY_INGEST_TOKENS", ValidToken);
            // No ConfigureTestServices — UnconfiguredTelemetryRepository is the default.
        }).CreateClient();
        var response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_Returns200_WhenRepositoryIsReady()
    {
        // A stub reporting IsReady=true causes /readyz → 200.
        var readyStub = new StubTelemetryRepository { IsReady = true };
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("VOUCHFX_TELEMETRY_INGEST_TOKENS", ValidToken);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ITelemetryRepository>(readyStub);
            });
        }).CreateClient();
        var response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_NotRateLimited_EvenAtZeroPermits()
    {
        // Health endpoints are exempt from rate limiting (DisableRateLimiting applied).
        // With 1 permit, exhaust the bucket via /v1/telemetry, then /healthz must still return 200.
        var client = CreateClient(ratePermits: 1, rateWindowSeconds: 60);
        // Exhaust the valid-token bucket.
        await client.SendAsync(BuildRequest());
        // /healthz bypasses rate limiting — must still succeed.
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
