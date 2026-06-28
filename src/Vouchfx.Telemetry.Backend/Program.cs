using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Vouchfx.Telemetry.Backend.Configuration;
using Vouchfx.Telemetry.Backend.Endpoints;
using Vouchfx.Telemetry.Backend.Persistence;
using Vouchfx.Telemetry.Backend.Security;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Options — bound from flat VOUCHFX_TELEMETRY_* env vars (authoritative) ──
// The Bicep/Container Apps deployment injects these as flat env vars; ASP.NET Core's
// env-var provider exposes them under their exact key names. The previous Telemetry:*
// section binding was silently ignored in production because the IaC injected flat keys,
// not colon-separated section keys.
builder.Services.Configure<TelemetryOptions>(opts =>
{
    var cfg = builder.Configuration;

    // Comma-separated bearer tokens — env-var binder does not split arrays, so we split here.
    var rawTokens = cfg["VOUCHFX_TELEMETRY_INGEST_TOKENS"];
    if (!string.IsNullOrWhiteSpace(rawTokens))
    {
        opts.IngestTokens = rawTokens.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();
    }

    if (long.TryParse(cfg["VOUCHFX_TELEMETRY_MAX_BODY_BYTES"],
            System.Globalization.CultureInfo.InvariantCulture, out var maxBodyBytes))
    {
        opts.MaxBodyBytes = maxBodyBytes;
    }

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_MAX_BATCH_LINES"],
            System.Globalization.CultureInfo.InvariantCulture, out var maxBatchLines))
    {
        opts.MaxBatchLines = maxBatchLines;
    }

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_RATE_PERMITS"],
            System.Globalization.CultureInfo.InvariantCulture, out var ratePermits))
    {
        opts.RatePermits = ratePermits;
    }

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS"],
            System.Globalization.CultureInfo.InvariantCulture, out var rateWindowSecs))
    {
        opts.RateWindowSeconds = rateWindowSecs;
    }

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_RETENTION_DAYS"],
            System.Globalization.CultureInfo.InvariantCulture, out var retDays))
        opts.RetentionDays = retDays;

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_PRECREATE_DAYS"],
            System.Globalization.CultureInfo.InvariantCulture, out var preDays))
        opts.PrecreateDays = preDays;

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_DEDUP_RETENTION_DAYS"],
            System.Globalization.CultureInfo.InvariantCulture, out var dedupDays))
        opts.DedupRetentionDays = dedupDays;

    if (int.TryParse(cfg["VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS"],
            System.Globalization.CultureInfo.InvariantCulture, out var jobHours))
        opts.JobIntervalHours = jobHours;
});

builder.Services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();
builder.Services.AddOptions<TelemetryOptions>().ValidateOnStart();

// Read early to decide which repository implementation to register.
// GetConnectionString("Telemetry") reads config key "ConnectionStrings:Telemetry".
// The IaC injects this via env var "ConnectionStrings__Telemetry": the .NET env-var
// provider normalises "__" → ":", so GetConnectionString is the only correct read —
// the literal key "ConnectionStrings__Telemetry" would always return null.
var connectionString = builder.Configuration.GetConnectionString("Telemetry")
    ?? builder.Configuration["VOUCHFX_TELEMETRY_DB_CONNECTION"];

// ── 2. Security ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BearerTokenValidator>();
builder.Services.AddSingleton<BearerAuthEndpointFilter>();

// ── 3. Persistence ───────────────────────────────────────────────────────────────
// TimeProvider must be registered unconditionally — the ingest endpoint uses it for
// timestamp validation even when no database is configured.
builder.Services.AddSingleton(TimeProvider.System);

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(
        _ => Vouchfx.Telemetry.Backend.Persistence.NpgsqlDataSourceFactory.Build(connectionString));
    builder.Services.AddSingleton<ITelemetryRepository,
        Vouchfx.Telemetry.Backend.Persistence.NpgsqlTelemetryRepository>();
    builder.Services.AddTransient<Vouchfx.Telemetry.Backend.Persistence.DbBootstrapper>();
    builder.Services.AddSingleton<Vouchfx.Telemetry.Backend.Background.PartitionManager>();
    builder.Services.AddSingleton<Vouchfx.Telemetry.Backend.Background.ForgetQueueDrainer>();
    builder.Services.AddHostedService<Vouchfx.Telemetry.Backend.Background.RetentionHostedService>();
}
else
{
    builder.Services.AddSingleton<ITelemetryRepository, UnconfiguredTelemetryRepository>();
}

// ── 4. Rate limiting ─────────────────────────────────────────────────────────
// Per-token limiter: a VALID bearer token gets its own fixed-window budget
// ("tok:<sha256>"), giving per-caller fairness. An INVALID/missing token is
// mapped to the single constant partition "unauthenticated", which bounds the
// total number of partitions to (#configured tokens + 1) and makes the limit
// real for authentication-failure floods — an attacker sending a new random
// token on every request cannot allocate unbounded partitions (memory DoS) and
// cannot bypass the limit by varying the token value.
// A global belt-and-suspenders limiter is also applied across all requests.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("bearer-token", httpContext =>
    {
        var validator = httpContext.RequestServices.GetRequiredService<BearerTokenValidator>();
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        var opts = httpContext.RequestServices
            .GetRequiredService<IOptions<TelemetryOptions>>().Value;

        string partitionKey;
        if (validator.IsValid(authHeader))
        {
            // Valid token: per-token partition for fair per-caller limits.
            var token = authHeader["Bearer ".Length..];
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            partitionKey = "tok:" + Convert.ToHexString(hash);
        }
        else
        {
            // Invalid/missing token: single shared partition — bounds allocations
            // and ensures the limit is real for auth-failure floods.
            partitionKey = "unauthenticated";
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = opts.RatePermits,
                Window = TimeSpan.FromSeconds(opts.RateWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            });
    });

    // Global belt-and-suspenders cap across ALL requests (a generous multiple of
    // the per-token limit so legitimate multi-client scenarios are not impacted).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext =>
        {
            var opts = httpContext.RequestServices
                .GetRequiredService<IOptions<TelemetryOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                "global",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = opts.RatePermits * 10,
                    Window = TimeSpan.FromSeconds(opts.RateWindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                });
        });

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        // Retry-After: prefer the rate-limiter's own remaining-window metadata; fall back
        // to 60 s (the default RateWindowSeconds). This is intentionally longer than the
        // 30 s used by ServiceUnavailableResult for transient storage faults: rate-limit
        // windows must be waited in full so the token bucket replenishes, whereas storage
        // faults typically resolve in seconds and warrant a shorter retry hint.
        context.HttpContext.Response.Headers.RetryAfter =
            context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "60";
        await context.HttpContext.Response.CompleteAsync();
    };
});

// ── 5. Kestrel body-size cap — read from flat key before Build() ──────────────
var tempOpts = new TelemetryOptions();
if (long.TryParse(
        builder.Configuration["VOUCHFX_TELEMETRY_MAX_BODY_BYTES"],
        System.Globalization.CultureInfo.InvariantCulture,
        out var kestrelMaxBody))
{
    tempOpts.MaxBodyBytes = kestrelMaxBody;
}

builder.WebHost.ConfigureKestrel(k =>
    k.Limits.MaxRequestBodySize = tempOpts.MaxBodyBytes);

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Production fail-fast: a Production backend with no DB must not start silently ──
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (app.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "No database connection string is configured (ConnectionStrings__Telemetry or " +
            "VOUCHFX_TELEMETRY_DB_CONNECTION). A Production deployment requires a real " +
            "database; refusing to start to prevent silent data loss.");
    }
    // Development/Test: allow placeholder (unit tests run without DB).
}
else
{
    var bootstrapper = app.Services
        .GetRequiredService<Vouchfx.Telemetry.Backend.Persistence.DbBootstrapper>();
    await bootstrapper.BootstrapAsync(CancellationToken.None);
}

app.UseRateLimiter();

app.MapGet("/", () => "vouchfx-telemetry-backend");

// ── Health probes — unauthenticated, exempt from rate limiting ────────────────
// /healthz: liveness — process is up (no dependency checks).
app.MapGet("/healthz", () => Results.Ok())
    .DisableRateLimiting();

// /readyz: readiness — 200 when the persistence backend is available, 503 when not.
// UnconfiguredTelemetryRepository always returns false, preventing Azure Container
// Apps from routing traffic to a no-op placeholder that silently discards data.
app.MapGet("/readyz", async (ITelemetryRepository repository, CancellationToken ct) =>
{
    var ready = await repository.IsReadyAsync(ct);
    return ready ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).DisableRateLimiting();

app.MapTelemetryEndpoints();

await app.RunAsync();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
