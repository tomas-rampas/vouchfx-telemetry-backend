using System.Text.Json;
using Npgsql;
using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>PostgreSQL-backed <see cref="ITelemetryRepository"/> using raw Npgsql.</summary>
internal sealed partial class NpgsqlTelemetryRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<NpgsqlTelemetryRepository> logger) : ITelemetryRepository
{
    // Reuse serialiser options; no custom converters needed.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // /readyz cache — avoids amplifying a probe flood to the database.
    // The endpoint is unauthenticated so a misbehaving client could call it at high frequency.
    private volatile bool _readyCachedValue;
    private long _readyExpiryUtcTicks;  // Interlocked-safe 64-bit ticks

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate batch {IdempotencyKey} — skipped.")]
    private static partial void LogDuplicateBatch(ILogger<NpgsqlTelemetryRepository> logger, string idempotencyKey);

    /// <inheritdoc/>
    public async Task<int> IngestAsync(
        IReadOnlyList<TelemetryEvent> events,
        string idempotencyKey,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // Ingest transaction: batch idempotency row + bulk event unnest.
            // The telemetry_event_default partition absorbs any row whose day-partition does
            // not yet exist; RetentionHostedService pre-creates upcoming partitions.
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // Batch-level dedup (README §2).
                // 0 rows affected → duplicate batch → commit and return 0.
                await using var batchCmd = new NpgsqlCommand(
                    "INSERT INTO ingest_batch (idempotency_key, install_id, line_count) " +
                    "VALUES ($1, $2, $3) ON CONFLICT (idempotency_key) DO NOTHING",
                    conn, tx);

                batchCmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = idempotencyKey,
                    DataTypeName = "text"
                });
                batchCmd.Parameters.Add(new NpgsqlParameter
                {
                    // install_id is nullable in ingest_batch
                    Value = events.Count > 0 ? (object)events[0].InstallId : DBNull.Value,
                    DataTypeName = "uuid"
                });
                batchCmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = events.Count,
                    DataTypeName = "int4"
                });

                var batchRows = await batchCmd.ExecuteNonQueryAsync(ct);
                if (batchRows == 0)
                {
                    await tx.CommitAsync(ct);
                    LogDuplicateBatch(logger, idempotencyKey);
                    return 0;
                }

                // Bulk unnest INSERT for all events (README §3).
                var newRows = await ExecuteBulkInsertAsync(events, conn, tx, ct);

                await tx.CommitAsync(ct);
                return newRows;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (NpgsqlException ex)
        {
            throw new TransientStorageException(
                $"Database error during ingest: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new TransientStorageException(
                $"Timeout during ingest: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueForgetAsync(Guid installId, CancellationToken ct)
    {
        // README §4.
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO forget_queue (install_id) VALUES ($1) " +
                "ON CONFLICT (install_id) DO UPDATE SET requested_at = now(), processed_at = NULL",
                conn);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = installId,
                DataTypeName = "uuid"
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException ex)
        {
            throw new TransientStorageException(
                $"Database error enqueuing forget: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new TransientStorageException(
                $"Timeout enqueuing forget: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        // Serve the cached value within the 5-second window so a probe flood does not
        // amplify to the database (the /readyz endpoint is unauthenticated).
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;
        if (nowTicks < Interlocked.Read(ref _readyExpiryUtcTicks))
            return _readyCachedValue;

        bool result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await using var conn = await dataSource.OpenConnectionAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cts.Token);
            result = true;
        }
        catch
        {
            result = false;
        }

        _readyCachedValue = result;
        Interlocked.Exchange(ref _readyExpiryUtcTicks,
            timeProvider.GetUtcNow().AddSeconds(5).UtcTicks);
        return result;
    }

    /// <summary>
    /// Builds and executes the 20-parameter unnest bulk INSERT (README §3).
    /// Returns the number of rows actually inserted (ON CONFLICT DO NOTHING rows excluded).
    /// </summary>
    private static async Task<int> ExecuteBulkInsertAsync(
        IReadOnlyList<TelemetryEvent> events,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        const string Sql =
            """
            INSERT INTO telemetry_event (
                install_id, event_timestamp, schema_version,
                tool_version, engine_version, dotnet_version,
                run_count, scenario_count,
                step_pass, step_fail, step_env_error, step_inconclusive,
                scenario_pass, scenario_fail, scenario_env_error, scenario_inconclusive,
                step_families, step_providers,
                startup_ms, time_to_first_test_ms
            )
            SELECT
                unnest($1::uuid[]),
                unnest($2::timestamptz[]),
                unnest($3::int[]),
                unnest($4::text[]),
                unnest($5::text[]),
                unnest($6::text[]),
                unnest($7::int[]),
                unnest($8::int[]),
                unnest($9::int[]),
                unnest($10::int[]),
                unnest($11::int[]),
                unnest($12::int[]),
                unnest($13::int[]),
                unnest($14::int[]),
                unnest($15::int[]),
                unnest($16::int[]),
                unnest($17::jsonb[]),
                unnest($18::jsonb[]),
                unnest($19::bigint[]),
                unnest($20::bigint[])
            ON CONFLICT (install_id, event_timestamp, schema_version) DO NOTHING
            """;

        // Build the 20 parallel arrays.
        var count = events.Count;

        var installIds = new Guid[count];
        var timestamps = new DateTime[count];  // UTC; maps to timestamptz
        var schemaVersions = new int[count];
        var toolVersions = new string[count];
        var engineVersions = new string[count];
        var dotnetVersions = new string[count];
        var runCounts = new int[count];
        var scenarioCounts = new int[count];
        var stepPass = new int[count];
        var stepFail = new int[count];
        var stepEnvError = new int[count];
        var stepInconclusive = new int[count];
        var scenarioPass = new int[count];
        var scenarioFail = new int[count];
        var scenarioEnvError = new int[count];
        var scenarioInconclusive = new int[count];
        var stepFamiliesJson = new string[count];
        var stepProvidersJson = new string[count];
        var startupMs = new long[count];
        var timeToFirstTestMs = new long[count];

        for (var i = 0; i < count; i++)
        {
            var ev = events[i];
            installIds[i] = ev.InstallId;
            timestamps[i] = ev.Timestamp.UtcDateTime;
            schemaVersions[i] = ev.SchemaVersion;
            toolVersions[i] = ev.ToolVersion;
            engineVersions[i] = ev.EngineVersion;
            dotnetVersions[i] = ev.DotnetVersion;
            runCounts[i] = ev.RunCount;
            scenarioCounts[i] = ev.ScenarioCount;
            stepPass[i] = ev.StepVerdicts.Pass;
            stepFail[i] = ev.StepVerdicts.Fail;
            stepEnvError[i] = ev.StepVerdicts.EnvError;
            stepInconclusive[i] = ev.StepVerdicts.Inconclusive;
            scenarioPass[i] = ev.ScenarioVerdicts.Pass;
            scenarioFail[i] = ev.ScenarioVerdicts.Fail;
            scenarioEnvError[i] = ev.ScenarioVerdicts.EnvError;
            scenarioInconclusive[i] = ev.ScenarioVerdicts.Inconclusive;
            // JSONB columns: serialise IReadOnlyDictionary<string,int> to JSON string
            stepFamiliesJson[i] = JsonSerializer.Serialize(ev.StepFamilies, JsonOpts);
            stepProvidersJson[i] = JsonSerializer.Serialize(ev.StepProviders, JsonOpts);
            startupMs[i] = ev.StartupMs;
            timeToFirstTestMs[i] = ev.TimeToFirstTestMs;
        }

        await using var cmd = new NpgsqlCommand(Sql, conn, tx);

        cmd.Parameters.Add(new NpgsqlParameter { Value = installIds, DataTypeName = "uuid[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = timestamps, DataTypeName = "timestamptz[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = schemaVersions, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toolVersions, DataTypeName = "text[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = engineVersions, DataTypeName = "text[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dotnetVersions, DataTypeName = "text[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = runCounts, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scenarioCounts, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepPass, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepFail, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepEnvError, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepInconclusive, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scenarioPass, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scenarioFail, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scenarioEnvError, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scenarioInconclusive, DataTypeName = "int4[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepFamiliesJson, DataTypeName = "jsonb[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = stepProvidersJson, DataTypeName = "jsonb[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = startupMs, DataTypeName = "int8[]" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = timeToFirstTestMs, DataTypeName = "int8[]" });

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
