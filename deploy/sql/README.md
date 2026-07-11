# vouchfx Telemetry Backend — SQL DML Reference

Target: PostgreSQL 16 on Azure Database for PostgreSQL Flexible Server

This document specifies every parameterised SQL statement the C# ingest service
(and its daily maintenance job) will execute against the schema created by
`bootstrap.sql`.  Use it as the canonical reference when building the Npgsql
repository layer.

All statements use `$1`, `$2`, … positional placeholders as Npgsql expects for
prepared statements.  Named parameters (`@name`) are noted where they clarify a
maintenance query.  JSONB columns are passed as `$n::jsonb` (Npgsql serialises
the C# object to JSON string; the cast targets the correct server type).

---

## 1. Partition pre-creation (before every bulk INSERT)

Before inserting a batch of events, the service must ensure that a day partition
exists for every unique date present in the batch.  If the partition does not
exist and the row's timestamp does not match the DEFAULT partition's catch-all
semantics, the INSERT will still succeed (the DEFAULT partition catches it), but
it is cleaner to pre-create explicit partitions so that aged-out rows can later
be dropped as a whole partition rather than scanned row-by-row.

The service collects the distinct `event_timestamp::date` values from the
decoded batch, then executes one call per unique date:

```sql
SELECT ensure_partition($1::date);
-- $1 : string  "YYYY-MM-DD"  e.g. "2026-06-28"
```

`ensure_partition` is idempotent (`CREATE TABLE IF NOT EXISTS` internally), so
calling it for a date that already has a partition is a no-op.

---

## 2. Batch idempotency insert

Execute this as the **first write** in the ingest transaction.  Check the rows
affected; if 0 rows are inserted the batch was already processed — return HTTP
200 immediately without touching `telemetry_event`.

```sql
INSERT INTO ingest_batch (idempotency_key, install_id, line_count)
VALUES ($1, $2, $3)
ON CONFLICT (idempotency_key) DO NOTHING;

-- $1 : char(64)  lowercase SHA-256 hex of the raw request body
-- $2 : uuid      installId from the first line of the batch (nullable)
-- $3 : int       number of event lines in the batch
```

`NpgsqlCommand.ExecuteNonQueryAsync()` returns the affected-row count.  A
return value of 0 means the idempotency key was already recorded; the caller
should short-circuit with 200 OK.

---

## 3. Bulk event insert via unnest

Insert all events in a single round-trip using Npgsql array parameters and
PostgreSQL's `unnest`.  The `received_at` column is omitted — it defaults to
`now()` on the server, recording the ingest timestamp without any client
influence.

```sql
INSERT INTO telemetry_event (
    install_id,
    event_timestamp,
    schema_version,
    tool_version,
    engine_version,
    dotnet_version,
    run_count,
    scenario_count,
    step_pass,
    step_fail,
    step_env_error,
    step_inconclusive,
    scenario_pass,
    scenario_fail,
    scenario_env_error,
    scenario_inconclusive,
    step_families,
    step_providers,
    startup_ms,
    time_to_first_test_ms
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
ON CONFLICT (install_id, event_timestamp, schema_version) DO NOTHING;

-- $1  : Guid[]       install_id
-- $2  : DateTime[]   event_timestamp  (UTC; Npgsql maps DateTime.Utc to timestamptz)
-- $3  : int[]        schema_version
-- $4  : string[]     tool_version
-- $5  : string[]     engine_version
-- $6  : string[]     dotnet_version
-- $7  : int[]        run_count
-- $8  : int[]        scenario_count
-- $9  : int[]        step_pass
-- $10 : int[]        step_fail
-- $11 : int[]        step_env_error
-- $12 : int[]        step_inconclusive
-- $13 : int[]        scenario_pass
-- $14 : int[]        scenario_fail
-- $15 : int[]        scenario_env_error
-- $16 : int[]        scenario_inconclusive
-- $17 : string[]     step_families    (JSON-serialised; cast to jsonb[])
-- $18 : string[]     step_providers   (JSON-serialised; cast to jsonb[])
-- $19 : long[]       startup_ms
-- $20 : long[]       time_to_first_test_ms
```

### Npgsql array binding notes

- Use `NpgsqlParameter` with `Value = array` and `DataTypeName = "uuid[]"` (or
  the corresponding type name) when building the command, or use the Dapper/
  EF Core Npgsql helpers that map CLR arrays automatically.
- For `$17` and `$18`, serialise each `Dictionary<string,int>` to a JSON string
  in C# (`System.Text.Json.JsonSerializer.Serialize`) before building the
  `string[]`; set `DataTypeName = "jsonb[]"`.
- All arrays must be the same length; the `unnest` parallel-unnest behaviour
  (multiple `unnest` calls in the same SELECT list) is guaranteed in PostgreSQL
  9.4+ to expand in lock-step when lengths match, and raises an error on mismatch.

### ON CONFLICT behaviour

Duplicate `(install_id, event_timestamp, schema_version)` rows are silently
discarded.  This is the second dedup layer (after batch idempotency in step 2).
The statement still succeeds; the C# side does not need to distinguish "inserted"
from "skipped" at the row level.

---

## 4. Forget enqueue

Called by the GDPR/forget REST endpoint.  `ON CONFLICT … DO UPDATE` re-opens a
previously processed request (sets `processed_at` back to NULL and refreshes
`requested_at`) so the forget worker will re-process it on the next cycle.

```sql
INSERT INTO forget_queue (install_id)
VALUES ($1)
ON CONFLICT (install_id) DO UPDATE
    SET requested_at = now(),
        processed_at = NULL;

-- $1 : Guid  the install_id to forget
```

---

## 5. Forget drain (per install, single transaction)

The background forget worker polls `forget_queue WHERE processed_at IS NULL`
and, for each pending row, executes the following three statements inside one
explicit transaction.  All three must succeed atomically; on failure the
transaction is rolled back and the `forget_queue` row remains unprocessed for
the next retry cycle.

```sql
-- 5a. Delete all telemetry rows for the install.
--     Crosses partitions automatically via the partitioned parent.
DELETE FROM telemetry_event
WHERE  install_id = $1;

-- 5b. Delete all ingest_batch records linked to the install.
--     Covers the case where the event rows have already been aged
--     out but the batch records remain within the dedup window.
DELETE FROM ingest_batch
WHERE  install_id = $1;

-- 5c. Mark the forget request as processed.
UPDATE forget_queue
SET    processed_at = now()
WHERE  install_id = $1;

-- $1 : Guid  the install_id being forgotten (same value in all three statements)
```

Wrap these three statements in `BEGIN` / `COMMIT` (or Npgsql's
`NpgsqlTransaction`).

---

## 6. Daily maintenance sequence

The daily purge job runs on a schedule (e.g. 02:00 UTC).  It acquires a
PostgreSQL advisory lock before running any mutating maintenance so that
multiple container replicas do not interfere with each other.

### 6a. Advisory lock acquisition

```sql
-- Attempt to acquire the session-level advisory lock.
-- Returns TRUE if the lock was acquired, FALSE if another session
-- already holds it.  The caller must check the return value and
-- skip all maintenance if FALSE.
SELECT pg_try_advisory_lock(152152152);

-- Constant key: 152152152
--   A fixed key to ensure only one replica runs maintenance per cycle.
--   Must NEVER be reused by any other advisory lock in this database.
```

Release at the end of the maintenance run (or when the connection closes):

```sql
SELECT pg_advisory_unlock(152152152);
```

The lock is automatically released if the session terminates abnormally, so
there is no risk of permanent lock starvation.

### 6b. Partition pre-creation

Creates explicit day partitions for the coming `@precreate` days so that
INSERT statements never fall through to the DEFAULT catch-all:

```sql
SELECT ensure_partitions(current_date, current_date + @precreate);

-- @precreate : int  recommended value 7 (one week ahead)
```

### 6c. Drop old partitions

Drops child partitions whose upper bound is on or before the retention cutoff:

```sql
SELECT drop_old_partitions(@retention);

-- @retention : int  recommended value 90 (days)
--              pass as a plain integer literal in the SQL string;
--              the function signature is drop_old_partitions(int).
```

`drop_old_partitions` never drops the DEFAULT partition regardless of the
retention value.  It emits a NOTICE for each dropped partition; redirect
PostgreSQL notices to the application log if visibility is desired.

### 6d. Sweep the DEFAULT partition

Removes aged-out rows from the DEFAULT (catch-all) partition:

```sql
SELECT sweep_default(@retention);

-- @retention : int  same value as step 6c
-- Returns    : bigint  number of rows deleted (log this value)
```

### 6e. Purge old ingest_batch records

Deletes dedup records older than the dedup retention window.  The dedup window
must exceed the client's maximum outbox age (30 days) plus the maximum Polly
back-off window to prevent a retried batch from being processed twice after its
dedup record was purged:

```sql
DELETE FROM ingest_batch
WHERE  received_at < now() - make_interval(days => @dedupRetention);

-- @dedupRetention : int  recommended value 35
--                        (30-day client cap + 5-day back-off headroom)
```

### Full daily maintenance example (C# pseudo-code)

```csharp
await using var conn = await dataSource.OpenConnectionAsync(ct);
await using var tx   = await conn.BeginTransactionAsync(ct);

// Attempt advisory lock
bool acquired = await conn.ExecuteScalarAsync<bool>(
    "SELECT pg_try_advisory_lock(152152152)", transaction: tx);
if (!acquired) { await tx.RollbackAsync(ct); return; }

try
{
    await conn.ExecuteAsync(
        "SELECT ensure_partitions(current_date, current_date + @precreate)",
        new { precreate = 7 }, transaction: tx);

    await conn.ExecuteAsync(
        "SELECT drop_old_partitions(@retention)",
        new { retention = 90 }, transaction: tx);

    long swept = await conn.ExecuteScalarAsync<long>(
        "SELECT sweep_default(@retention)",
        new { retention = 90 }, transaction: tx);
    logger.LogInformation("Swept {Rows} rows from default partition", swept);

    await conn.ExecuteAsync(
        "DELETE FROM ingest_batch WHERE received_at < now() - make_interval(days => @dedupRetention)",
        new { dedupRetention = 35 }, transaction: tx);

    await tx.CommitAsync(ct);
}
catch
{
    await tx.RollbackAsync(ct);
    throw;
}
finally
{
    await conn.ExecuteAsync("SELECT pg_advisory_unlock(152152152)");
}
```

Note: Dapper `@named` parameters are used above for clarity.  When using raw
Npgsql `NpgsqlCommand`, replace them with `$1`, `$2`, … positional parameters
as shown in sections 1–5.

---

## Schema quick-reference

| Table / View | Description |
|---|---|
| `telemetry_event` | Partitioned parent; one row per `TelemetryEvent` received |
| `telemetry_event_default` | DEFAULT catch-all partition for clock-skewed rows |
| `telemetry_event_YYYYMMDD` | Auto-created daily child partitions |
| `ingest_batch` | Batch-level dedup records keyed by SHA-256 of request body |
| `forget_queue` | GDPR forget requests; processed by background worker |
| `v_step_family_daily` | Daily step-family usage counts (unnests `step_families` JSONB) |
| `v_step_provider_daily` | Daily step-provider usage counts (unnests `step_providers` JSONB) |
| `v_run_daily` | Daily rollup with verdict sums, timing percentiles, distinct installs |

| Function | Signature | Description |
|---|---|---|
| `ensure_partition` | `(d date) → void` | Creates one day partition |
| `ensure_partitions` | `(from_date date, to_date date) → void` | Creates partitions for `[from, to)` |
| `drop_old_partitions` | `(retention_days int) → void` | Drops expired day partitions |
| `sweep_default` | `(retention_days int) → bigint` | Deletes aged rows from DEFAULT partition |
