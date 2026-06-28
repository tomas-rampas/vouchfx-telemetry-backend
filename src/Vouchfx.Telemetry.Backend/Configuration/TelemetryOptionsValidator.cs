using Microsoft.Extensions.Options;

namespace Vouchfx.Telemetry.Backend.Configuration;

/// <summary>
/// Validates <see cref="TelemetryOptions"/> at startup to prevent silent misconfiguration.
/// Negative/zero retention or interval values cause data loss or silent timer crashes.
/// </summary>
internal sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        var failures = new List<string>();

        if (options.RetentionDays < 1)
            failures.Add($"{nameof(TelemetryOptions.RetentionDays)} must be >= 1 (got {options.RetentionDays}); a value <= 0 moves the drop_old_partitions cutoff into the future and would delete active data.");

        if (options.DedupRetentionDays < 1)
            failures.Add($"{nameof(TelemetryOptions.DedupRetentionDays)} must be >= 1 (got {options.DedupRetentionDays}).");

        if (options.JobIntervalHours < 1)
            failures.Add($"{nameof(TelemetryOptions.JobIntervalHours)} must be >= 1 (got {options.JobIntervalHours}); a value <= 0 throws in PeriodicTimer and silently stops all maintenance.");

        if (options.PrecreateDays < 0)
            failures.Add($"{nameof(TelemetryOptions.PrecreateDays)} must be >= 0 (got {options.PrecreateDays}).");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
