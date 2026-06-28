using Npgsql;

namespace Vouchfx.Telemetry.Backend.Background;

/// <summary>
/// Processes unhandled rows in <c>forget_queue</c> (README §5).
/// Each install is deleted atomically in its own transaction.
/// </summary>
internal sealed partial class ForgetQueueDrainer(
    NpgsqlDataSource dataSource,
    ILogger<ForgetQueueDrainer> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {Count} forget request(s).")]
    private static partial void LogProcessingForgets(ILogger<ForgetQueueDrainer> logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forget drained for install {InstallIdPrefix}.")]
    private static partial void LogForgetDrained(ILogger<ForgetQueueDrainer> logger, string installIdPrefix);

    [LoggerMessage(Level = LogLevel.Error, Message = "Forget drain failed for install {InstallIdPrefix}.")]
    private static partial void LogForgetFailed(ILogger<ForgetQueueDrainer> logger, Exception ex, string installIdPrefix);

    /// <summary>Returns the first 8 hex chars of the install ID followed by an ellipsis.</summary>
    private static string RedactInstallId(Guid installId) =>
        installId.ToString("N")[..8] + "…";

    /// <summary>Drains all pending forget requests from the queue in bounded batches.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        const int BatchSize = 500;
        int fetched;
        do
        {
            var pending = await FetchPendingAsync(BatchSize, ct);
            fetched = pending.Count;
            if (fetched == 0) return;

            LogProcessingForgets(logger, fetched);
            foreach (var installId in pending)
            {
                await DrainOneAsync(installId, ct);
            }
        }
        while (fetched == BatchSize); // A full batch may mean more rows remain
    }

    private async Task<IReadOnlyList<Guid>> FetchPendingAsync(int limit, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT install_id FROM forget_queue WHERE processed_at IS NULL ORDER BY requested_at LIMIT $1",
            conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit, DataTypeName = "int4" });
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task DrainOneAsync(Guid installId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // 5a — delete telemetry_event rows (crosses partitions via parent table)
                await using var delEvt = new NpgsqlCommand(
                    "DELETE FROM telemetry_event WHERE install_id = $1", conn, tx);
                delEvt.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
                await delEvt.ExecuteNonQueryAsync(ct);

                // 5b — delete ingest_batch rows
                await using var delBatch = new NpgsqlCommand(
                    "DELETE FROM ingest_batch WHERE install_id = $1", conn, tx);
                delBatch.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
                await delBatch.ExecuteNonQueryAsync(ct);

                // 5c — mark forget_queue row processed
                await using var markDone = new NpgsqlCommand(
                    "UPDATE forget_queue SET processed_at = now() WHERE install_id = $1", conn, tx);
                markDone.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
                await markDone.ExecuteNonQueryAsync(ct);

                await tx.CommitAsync(ct);
                LogForgetDrained(logger, RedactInstallId(installId));
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown signal — stop draining, do not log as a forget failure
        }
        catch (Exception ex)
        {
            // Log and continue — the next cycle will retry unprocessed rows.
            LogForgetFailed(logger, ex, RedactInstallId(installId));
        }
    }
}
