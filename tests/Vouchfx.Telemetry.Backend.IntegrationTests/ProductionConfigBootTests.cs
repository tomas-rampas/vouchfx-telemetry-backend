// CA1707: underscore-separated names are the xUnit naming convention for test methods.
#pragma warning disable CA1707

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// End-to-end boot test that exercises the production configuration path:
/// the <c>ConnectionStrings__Telemetry</c> environment variable (double-underscore)
/// must be normalised to the config key <c>ConnectionStrings:Telemetry</c> by the
/// .NET environment-variable provider, and read via
/// <c>builder.Configuration.GetConnectionString("Telemetry")</c>.
/// </summary>
/// <remarks>
/// This test lives in the "ProductionBoot" collection (its own <see cref="PostgresFixture"/>
/// instance) so that the process-level env-var mutations in <see cref="InitializeAsync"/>
/// are never visible to the "Postgres" collection tests that run concurrently.
/// </remarks>
[Collection("ProductionBoot")]
public sealed class ProductionConfigBootTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Environment variable names as injected by the Bicep/Container Apps IaC.
    private const string ConnectionStringEnvVar = "ConnectionStrings__Telemetry";
    private const string IngestTokensEnvVar = "VOUCHFX_TELEMETRY_INGEST_TOKENS";

    // Stable bearer token for this isolated test.
    private const string ValidToken = "prod-boot-test-bearer-token";

    // Saved values restored in DisposeAsync regardless of test outcome.
    private string? _savedConnectionString;
    private string? _savedIngestTokens;

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        // Save whatever was in the environment before this test suite runs.
        _savedConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        _savedIngestTokens = Environment.GetEnvironmentVariable(IngestTokensEnvVar);

        // Inject the IaC-style env vars before any WebApplicationFactory is created.
        // The .NET env-var config provider normalises ConnectionStrings__Telemetry →
        // ConnectionStrings:Telemetry, which GetConnectionString("Telemetry") reads.
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, fixture.ConnectionString);
        Environment.SetEnvironmentVariable(IngestTokensEnvVar, ValidToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        // Always restore, even if the test threw.
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _savedConnectionString);
        Environment.SetEnvironmentVariable(IngestTokensEnvVar, _savedIngestTokens);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Boots the full application in Production mode with only env vars (no UseSetting),
    /// confirming that:
    /// <list type="bullet">
    ///   <item><description><c>GET /readyz</c> returns 200 (NpgsqlTelemetryRepository is wired and the DB is reachable).</description></item>
    ///   <item><description><c>POST /v1/telemetry</c> returns 200 and the row is persisted (full HTTP→config→DB path).</description></item>
    /// </list>
    /// This test would have failed against the pre-fix code because
    /// <c>["ConnectionStrings__Telemetry"]</c> always returned null, causing the
    /// Production fail-fast to throw <see cref="InvalidOperationException"/> on startup.
    /// </summary>
    [Fact]
    public async Task ProductionEnvVar_ConnectionStrings__Telemetry_BootsApp_ReadyzAnd_IngestWork()
    {
        // UseEnvironment("Production") exercises the production fail-fast path in Program.cs
        // (app.Environment.IsProduction() → true).  The connection string comes exclusively
        // from the process env var set above — not from UseSetting, which would bypass the
        // __ → : normalisation that is the subject of this test.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = factory.CreateClient();

        // 1. /readyz → 200: NpgsqlTelemetryRepository.IsReadyAsync succeeded → DB is reachable.
        var readyzResponse = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, readyzResponse.StatusCode);

        // 2. POST /v1/telemetry → 200: full HTTP → config → repository → DB path is wired.
        var installId = Guid.NewGuid();
        var idempotencyKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var eventLine = BuildEventLine(installId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new StringContent(eventLine, Encoding.UTF8, "application/x-ndjson");

        var ingestResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ingestResponse.StatusCode);

        // 3. Verify the row was actually written to the DB (proves a real write, not a stub).
        // Query directly via the fixture's DataSource — the WebApplicationFactory and the
        // fixture both point at the same Testcontainers postgres for this collection.
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM telemetry_event WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }

    private static string BuildEventLine(Guid installId)
    {
        // Build a minimal valid NDJSON event line with a unique installId so the row
        // count assertion is reliable even if the test is re-run against the same DB.
        var ts = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var id = installId.ToString("D", CultureInfo.InvariantCulture);
        return
            $$"""{"schemaVersion":1,"timestamp":"{{ts}}","installId":"{{id}}","toolVersion":"1.0.0","engineVersion":"1.0.0","dotnetVersion":".NET 8.0","runCount":1,"scenarioCount":1,"stepVerdicts":{"pass":1,"fail":0,"envError":0,"inconclusive":0},"scenarioVerdicts":{"pass":1,"fail":0,"envError":0,"inconclusive":0},"stepFamilies":{"http":1},"stepProviders":{"http.rest":1},"startupMs":100,"timeToFirstTestMs":200}""";
    }
}
