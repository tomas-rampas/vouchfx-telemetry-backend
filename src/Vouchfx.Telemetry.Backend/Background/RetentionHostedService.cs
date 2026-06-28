using Microsoft.Extensions.Options;
using Npgsql;
using Vouchfx.Telemetry.Backend.Configuration;

namespace Vouchfx.Telemetry.Backend.Background;

/// <summary>
/// Runs partition maintenance + forget drain on a configurable interval.
/// Uses a PostgreSQL advisory lock so only one replica mutates data per cycle.
/// </summary>
internal sealed partial class RetentionHostedService : IHostedService, IAsyncDisposable
{
    private readonly IOptions<TelemetryOptions> _options;
    private readonly PartitionManager _partitionManager;
    private readonly ForgetQueueDrainer _forgetQueueDrainer;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionHostedService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private int _disposed;

    [LoggerMessage(Level = LogLevel.Error, Message = "RetentionHostedService faulted during shutdown.")]
    private static partial void LogShutdownFault(ILogger<RetentionHostedService> logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not attempt advisory lock acquisition.")]
    private static partial void LogLockAcquisitionFailed(ILogger<RetentionHostedService> logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Maintenance advisory lock not acquired — another replica is running.")]
    private static partial void LogLockNotAcquired(ILogger<RetentionHostedService> logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance step '{Step}' failed.")]
    private static partial void LogStepFailed(ILogger<RetentionHostedService> logger, Exception ex, string step);

    /// <summary>Initialises the service with its dependencies.</summary>
    public RetentionHostedService(
        IOptions<TelemetryOptions> options,
        PartitionManager partitionManager,
        ForgetQueueDrainer forgetQueueDrainer,
        NpgsqlDataSource dataSource,
        TimeProvider timeProvider,
        ILogger<RetentionHostedService> logger)
    {
        _options = options;
        _partitionManager = partitionManager;
        _forgetQueueDrainer = forgetQueueDrainer;
        _dataSource = dataSource;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _backgroundTask = ExecuteAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null || _backgroundTask is null) return;
        await _cts.CancelAsync();
        try
        {
            await _backgroundTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogShutdownFault(_logger, ex);
        }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = _options.Value;

        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(opts.JobIntervalHours), _timeProvider);

        try
        {
            // First cycle runs immediately at startup — inside the guard so any
            // unexpected fault is logged rather than silently killing the loop.
            await RunMaintenanceCycleAsync(opts, ct);

            while (await timer.WaitForNextTickAsync(ct))
            {
                await RunMaintenanceCycleAsync(opts, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // An unexpected fault escaped all per-step guards — log so it is visible.
            LogShutdownFault(_logger, ex);
        }
    }

    private async Task RunMaintenanceCycleAsync(TelemetryOptions opts, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var lockHandle = new PgAdvisoryLock(_dataSource);
        bool acquired;
        try
        {
            acquired = await lockHandle.TryAcquireAsync(ct);
        }
        catch (Exception ex)
        {
            LogLockAcquisitionFailed(_logger, ex);
            await lockHandle.DisposeAsync();
            return;
        }

        if (!acquired)
        {
            LogLockNotAcquired(_logger);
            await lockHandle.DisposeAsync();
            return;
        }

        try
        {
            await RunStepsAsync(opts, ct);
        }
        finally
        {
            await lockHandle.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs each maintenance step independently; logs and swallows per-step failures so
    /// one failing step does not block the remaining steps.
    /// </summary>
    private async Task RunStepsAsync(TelemetryOptions opts, CancellationToken ct)
    {
        await RunStep("EnsurePartitionsAhead",
            () => _partitionManager.EnsurePartitionsAheadAsync(opts.PrecreateDays, ct),
            ct);

        await RunStep("DropOldPartitions",
            () => _partitionManager.DropOldPartitionsAsync(opts.RetentionDays, ct),
            ct);

        await RunStep("SweepDefault",
            () => _partitionManager.SweepDefaultAsync(opts.RetentionDays, ct),
            ct);

        await RunStep("ForgetDrain",
            () => _forgetQueueDrainer.DrainAsync(ct),
            ct);

        await RunStep("PurgeIngestBatch", async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM ingest_batch WHERE received_at < now() - make_interval(days => $1)",
                conn);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = opts.DedupRetentionDays,
                DataTypeName = "int4"
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    private async Task RunStep(string name, Func<Task> action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation; don't swallow it.
        }
        catch (Exception ex)
        {
            LogStepFailed(_logger, ex, name);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}
