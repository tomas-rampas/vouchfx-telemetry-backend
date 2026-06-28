# Architecture & System Design

S12-G-01 / Issue #152 Phase B

This document describes the system architecture, component interactions, database schema, and Azure infrastructure topology of the vouchfx telemetry backend.

## Overview

The backend is a lean, stateless **ASP.NET Core 8 minimal-API service** that:

1. **Ingests** opt-in telemetry batches from the frozen vouchfx engine client
2. **Parses** NDJSON event lines and enforces the privacy-allowlist contract
3. **Stores** events in a PostgreSQL database under daily RANGE partitions
4. **Maintains** data via a background job: partition pre-creation, aged-partition drop, and forget-queue drain
5. **Serves** dashboard views so the telemetry team can inspect aggregate counts and timing distributions

## Five-Component Architecture

### 1. Ingest Endpoint (`TelemetryEndpoints.cs`)

**Responsibility:** HTTP request handling, validation, and routing.

**Flow:**
- Validates `Content-Type: application/x-ndjson`
- Validates `Idempotency-Key` header (64 lowercase hex chars)
- Reads the request body (bounded by `Kestrel.Limits.MaxRequestBodySize`)
- Extracts NDJSON lines and counts them
- Rejects if line count exceeds `MaxBatchLines` (default 500)
- Passes lines to the **Allowlist Parser** for validation
- Returns 2xx on success, 4xx/5xx on error

**Key class:** `TelemetryEndpoints.HandleIngestAsync(…)`

### 2. Allowlist Parser (`AllowlistParser.cs`)

**Responsibility:** Deserialize and validate the allowlist contract.

**Logic:**
- Iterates each NDJSON line
- Extracts `schemaVersion` field; treats missing/non-integer as parse error
- Schema version 1: strict deserialization (rejects unknown fields via `UnmappedMemberHandling.Disallow`)
- Schema version >1: lenient deserialization (unknown fields tolerated but never stored)
- Returns a `ParseResult` (union type): `Empty`, `TooManyLines`, `Bad`, or `Ok`
- The `TelemetryEvent` record is the allowlist; only its declared properties can bind

**Key class:** `AllowlistParser.Parse(lines, maxLines)`

### 3. Npgsql Repository (`NpgsqlTelemetryRepository.cs`)

**Responsibility:** Transactional database persistence.

**Operations:**

#### Ingest
1. Attempts `INSERT INTO ingest_batch (idempotency_key, install_id, line_count) VALUES (…) ON CONFLICT DO NOTHING`
2. If 0 rows inserted: batch is a duplicate; return 200 immediately
3. Pre-creates daily partitions for each unique `event_timestamp::date` in the batch
4. Bulk-inserts all events via `unnest()` with `ON CONFLICT (install_id, event_timestamp, schema_version) DO NOTHING`
5. Returns the number of new rows inserted

#### Forget
- Enqueues a forget request: `INSERT INTO forget_queue (install_id) VALUES (…) ON CONFLICT DO UPDATE SET requested_at = now(), processed_at = NULL`

#### Ready Check
- Executes a simple query to confirm the database is reachable; used by the `/readyz` probe

**Key class:** `NpgsqlTelemetryRepository`

### 4. Partition Manager (`PartitionManager.cs`)

**Responsibility:** Daily partition lifecycle management (no manual administration required).

**Operations (all called by the background job):**

#### EnsurePartitionsAhead
- Pre-creates explicit day partitions for the next `PrecreateDays` days (default 7)
- Ensures explicit partitions exist before data arrives, preventing DEFAULT partition pollution
- Calls `SELECT ensure_partitions(current_date, current_date + @precreate)`

#### DropOldPartitions
- Drops child partitions whose upper bound is on or before the retention cutoff
- Calls `SELECT drop_old_partitions(@retentionDays)`
- Never drops the DEFAULT partition (belt-and-suspenders safety)

#### SweepDefault
- Deletes aged rows from the DEFAULT catch-all partition (for clients with broken clocks)
- Calls `SELECT sweep_default(@retentionDays)`, which returns the row count deleted
- Rows are deleted if their `event_timestamp` is older than the retention window

**Key class:** `PartitionManager`

### 5. Forget Queue Drainer (`ForgetQueueDrainer.cs`)

**Responsibility:** Process pending deletion requests.

**Operation:**
- Queries `SELECT install_id FROM forget_queue WHERE processed_at IS NULL`
- For each row, within a transaction:
  1. `DELETE FROM telemetry_event WHERE install_id = $1` (crosses partitions automatically)
  2. `DELETE FROM ingest_batch WHERE install_id = $1` (cleans up dedup records)
  3. `UPDATE forget_queue SET processed_at = now() WHERE install_id = $1`
- Deletion is best-effort one-shot; the backend job is a backstop for client-side failures

**Key class:** `ForgetQueueDrainer`

## PostgreSQL Schema

Target: **PostgreSQL 16** on Azure Database for PostgreSQL Flexible Server

### Core Tables

#### `telemetry_event` (Partitioned Parent)

RANGE-partitioned by `event_timestamp` (daily partitions). Stores one row per `TelemetryEvent` received.

```sql
CREATE TABLE telemetry_event (
    -- Identity & dedup
    install_id              uuid        NOT NULL,
    event_timestamp         timestamptz NOT NULL,   -- partition key
    schema_version          int         NOT NULL,
    -- Server-side audit
    received_at             timestamptz NOT NULL DEFAULT now(),
    -- Versions
    tool_version            text        NOT NULL,
    engine_version          text        NOT NULL,
    dotnet_version          text        NOT NULL,
    -- Counts
    run_count               int         NOT NULL,
    scenario_count          int         NOT NULL,
    step_pass               int         NOT NULL,
    step_fail               int         NOT NULL,
    step_env_error          int         NOT NULL,
    step_inconclusive       int         NOT NULL,
    scenario_pass           int         NOT NULL,
    scenario_fail           int         NOT NULL,
    scenario_env_error      int         NOT NULL,
    scenario_inconclusive   int         NOT NULL,
    -- Usage maps
    step_families           jsonb       NOT NULL,
    step_providers          jsonb       NOT NULL,
    -- Timings (milliseconds)
    startup_ms              bigint      NOT NULL,
    time_to_first_test_ms   bigint      NOT NULL,
    
    PRIMARY KEY (install_id, event_timestamp, schema_version)
)
PARTITION BY RANGE (event_timestamp);
```

**Partitioning strategy:**
- Daily child partitions (e.g. `telemetry_event_20260628`) are created automatically via `ensure_partition(d date)`
- DEFAULT catch-all partition absorbs rows with clock-skewed or out-of-window timestamps
- Partition upper bounds align with UTC midnight (each partition spans one calendar day in UTC)
- Old partitions are dropped on the daily job when their upper bound is ≥90 days old (configurable)

**Why `event_timestamp` is the partition key (not `received_at`):**
- PostgreSQL requires the partition key to appear in every unique constraint on a partitioned table
- The natural dedup key is `(install_id, event_timestamp, schema_version)` — the time-unique identity of a run
- Using `event_timestamp` (the client-reported run-end time) makes this triple a legal PRIMARY KEY
- A re-sent batch lands in the same partition as the original, guaranteeing the PK constraint fires and the duplicate is skipped

**Indexes:** None explicit (PRIMARY KEY is indexed). Additional indexes (e.g. on `received_at` for partition purge scans) are not needed at pilot scale.

#### `ingest_batch` (NOT Partitioned)

Batch-level idempotency records. Primary key is the SHA-256 hex of the request body.

```sql
CREATE TABLE ingest_batch (
    idempotency_key char(64)    NOT NULL,
    install_id      uuid        NULL,
    line_count      int         NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (idempotency_key)
);

CREATE INDEX idx_ingest_batch_received_at
    ON ingest_batch (received_at);
CREATE INDEX idx_ingest_batch_install_id
    ON ingest_batch (install_id)
    WHERE install_id IS NOT NULL;
```

**Retention:** Aged-out records (older than `DedupRetentionDays`, default 35 days) are purged on the daily job. The 35-day window accounts for the client's 30-day outbox cap plus Polly back-off headroom.

#### `forget_queue`

GDPR deletion requests queued by the `/v1/telemetry/forget` endpoint.

```sql
CREATE TABLE forget_queue (
    install_id   uuid        NOT NULL PRIMARY KEY,
    requested_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz NULL,
);

CREATE INDEX idx_forget_queue_unprocessed
    ON forget_queue (install_id)
    WHERE processed_at IS NULL;
```

**Flow:**
- New forget request: `INSERT … ON CONFLICT DO UPDATE SET requested_at = now(), processed_at = NULL`
- Daily job queries `WHERE processed_at IS NULL` and processes each
- On success, sets `processed_at = now()`
- If the job crashes mid-deletion, `processed_at` remains NULL and the next cycle retries

### Functions

#### `ensure_partition(d date) → void`

Creates a single-day child partition for day `d`. Table name is derived solely from the date parameter via `to_char(d, 'YYYYMMDD')` — **no untrusted input reaches the DDL, DDL injection is structurally impossible**.

```sql
CREATE TABLE IF NOT EXISTS telemetry_event_<YYYYMMDD>
    PARTITION OF telemetry_event
    FOR VALUES FROM ('<date>') TO ('<date+1>');
```

Idempotent: running it twice on the same date is a no-op.

#### `ensure_partitions(from_date date, to_date date) → void`

Calls `ensure_partition` for each day in `[from_date, to_date)`. Used by the daily job to pre-create 7 days ahead.

#### `drop_old_partitions(retention_days int) → void`

Drops every direct child partition of `telemetry_event` whose upper bound satisfies `upper_bound ≤ current_date - retention_days`.

**Identification:** Uses `pg_get_expr(c.relpartbound, c.oid)` to extract the upper-bound timestamp from the partition bound expression, then compares it to the cutoff. Robust across any partition naming scheme (not dependent on the YYYYMMDD suffix).

**Safety:** Explicitly excludes the DEFAULT partition via `pg_partitioned_table.partdefid`.

#### `sweep_default(retention_days int) → bigint`

Deletes rows from the DEFAULT partition whose `event_timestamp < now() - make_interval(days => retention_days)`. Returns the row count deleted. Targets clients with broken clocks that produce timestamps outside the explicit partition range.

### Dashboard Views (S12-E-05)

Read-only views for the telemetry team to inspect aggregate statistics without direct table access.

#### `v_step_family_daily`

Daily usage count per step family:

```sql
SELECT
    date_trunc('day', event_timestamp) AS day,
    kv.key                             AS family,
    SUM(kv.value::int)                 AS n
FROM telemetry_event,
     LATERAL jsonb_each_text(step_families) AS kv
GROUP BY 1, 2;
```

Example output:
```
day        | family       | n
-----------|--------------|---
2026-06-28 | http         | 1250
2026-06-28 | db-assert    | 450
2026-06-28 | mq-publish   | 200
```

#### `v_step_provider_daily`

Daily usage count per step provider (technology):

```sql
SELECT
    date_trunc('day', event_timestamp) AS day,
    kv.key                             AS provider,
    SUM(kv.value::int)                 AS n
FROM telemetry_event,
     LATERAL jsonb_each_text(step_providers) AS kv
GROUP BY 1, 2;
```

#### `v_run_daily`

Daily rollup: run/scenario counts, verdict sums, timing percentiles, distinct installs:

```sql
SELECT
    date_trunc('day', event_timestamp)                            AS day,
    COUNT(*)                                                       AS events,
    SUM(run_count)                                                 AS runs,
    SUM(scenario_count)                                            AS scenarios,
    SUM(step_pass)                                                 AS step_pass,
    SUM(step_fail)                                                 AS step_fail,
    SUM(step_env_error)                                            AS step_env_error,
    SUM(step_inconclusive)                                         AS step_inconclusive,
    SUM(scenario_pass)                                             AS scenario_pass,
    SUM(scenario_fail)                                             AS scenario_fail,
    SUM(scenario_env_error)                                        AS scenario_env_error,
    SUM(scenario_inconclusive)                                     AS scenario_inconclusive,
    percentile_cont(0.5)  WITHIN GROUP (ORDER BY startup_ms)       AS startup_ms_p50,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY startup_ms)       AS startup_ms_p95,
    percentile_cont(0.5)  WITHIN GROUP (ORDER BY time_to_first_test_ms) AS ttft_ms_p50,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY time_to_first_test_ms) AS ttft_ms_p95,
    COUNT(DISTINCT install_id)                                     AS distinct_installs
FROM telemetry_event
GROUP BY 1;
```

## Background Maintenance Job

**Implementation:** `RetentionHostedService` (ASP.NET Core `IHostedService`)

**Interval:** Configurable via `VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS` (default 24; runs immediately at startup, then every 24 hours)

**Concurrency control:** PostgreSQL advisory lock (constant key `152152152`, derived from issue #152). Only one Container App replica runs maintenance per cycle. If the lock cannot be acquired, the cycle is skipped and the next replica waits.

**Sequence per cycle:**
1. Acquire advisory lock (if it fails, skip this cycle)
2. Run steps (independently; per-step failures are logged but do not block remaining steps):
   - `EnsurePartitionsAhead(PrecreateDays)` — pre-create 7 days ahead
   - `DropOldPartitions(RetentionDays)` — drop partitions ≥90 days old
   - `SweepDefault(RetentionDays)` — delete aged rows from DEFAULT partition
   - `ForgetDrain()` — process forget_queue
   - `PurgeIngestBatch(DedupRetentionDays)` — purge dedup records ≥35 days old
3. Release advisory lock

**Replica requirements:**
- **minReplicas = 1** (mandatory). The job is an in-process timer; if all replicas are scaled to zero, the timer never fires and old partitions are never dropped.
- **Container Apps Consumption workload:** ~$15–20/month for a single idle replica

## Azure Infrastructure Topology

Deployed via Bicep IaC (`deploy/main.bicep` and child modules) to an Azure resource group.

### Components

#### User-Assigned Managed Identity (UAMI)

- Created before all other resources to resolve circular dependencies
- Used by the Container App to authenticate to Azure Key Vault (no credentials in logs or process environment)
- Granted `Key Vault Secrets User` role on the Key Vault

#### Log Analytics Workspace

- Receives Container App logs, metrics, and diagnostics
- Retention configurable; enables alerting and log queries for operational visibility

#### Container Apps Environment (Managed Environment)

- Shared compute and networking fabric for Container Apps
- Integrates with Log Analytics for observability

#### PostgreSQL Flexible Server (Managed Database)

**SKU options:**
- Development: `Standard_B1ms` (Burstable, 1 vCore, 2 GiB RAM, default 32 GiB storage)
- Production: Customizable (default: `Standard_B2s` Burstable; `GeneralPurpose` recommended for sustained load)

**Network:** Public access (firewall rule: "allow Azure services"). Hardened production delta: VNet integration + private endpoint + password-less Entra MI auth.

**TLS:** HTTPS-required firewall rule enforced (sslmode=require in connection strings).

**Availability:**
- High-availability (zone-redundant standby replica) available but not deployed in pilot (cost/complexity trade-off)
- Single-replica pilot configuration is suitable for opt-in telemetry (tolerable availability for non-critical data)

**Backup:** Azure-managed geo-redundant backups (7-day retention by default)

#### Key Vault

- Stores sensitive secrets:
  - `ingest-tokens`: Bearer tokens (comma-separated via `readEnvironmentVariable()`)
  - `db-connection-string`: Full Npgsql connection string
- Secrets are never logged, displayed in deployment history, or written to parameter files
- Versionless secret URIs enable automatic secret rotation without Container App redeployment
- Access: Only the Container App's UAMI can read these secrets (via Key Vault Secrets User role)

#### Container App

**Image:** `vfxtelregistry.azurecr.io/vouchfx-telemetry-backend:<tag>`

**Replica count:**
- `minReplicas = 1` (for background job)
- `maxReplicas = 3` (default; configurable)

**Ingress:**
- External (platform provisions public HTTPS endpoint)
- `allowInsecure = false` (HTTPS-only)
- Target port 8080 (non-privileged)
- TLS certificate managed by the platform

**Environment variables:**

**Secrets (from Key Vault):**
- `VOUCHFX_TELEMETRY_INGEST_TOKENS` ← `ingest-tokens` secret
- `ConnectionStrings__Telemetry` ← `db-connection` secret

**Non-secret tunables:**
- `VOUCHFX_TELEMETRY_MAX_BODY_BYTES` (default 2097152)
- `VOUCHFX_TELEMETRY_MAX_BATCH_LINES` (default 500)
- `VOUCHFX_TELEMETRY_RATE_PERMITS` (default 120)
- `VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS` (default 60)
- `VOUCHFX_TELEMETRY_RETENTION_DAYS` (default 90)
- `VOUCHFX_TELEMETRY_PRECREATE_DAYS` (default 7)
- `VOUCHFX_TELEMETRY_DEDUP_RETENTION_DAYS` (default 35)
- `VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS` (default 24)
- `ASPNETCORE_URLS` (fixed: `http://+:8080`)

**Health probes:**
- Liveness: `GET /healthz` (30s initial delay, 30s period, 5s timeout, 3 failure threshold)
- Readiness: `GET /readyz` (10s initial delay, 10s period, 5s timeout, 3 failure threshold)

**Resources per replica:**
- CPU: 0.5 cores
- Memory: 1 GiB

## Deployment Process

See `deploy.yml` for the full workflow. Summary:

1. **GitHub Actions** checks out the repo
2. **Builds the .NET service** (`dotnet publish`)
3. **Builds and pushes the Docker image** to ACR (`az acr build`)
4. **Validates Bicep parameters** (reads secrets from env vars via `readEnvironmentVariable()`)
5. **Deploys Bicep IaC** (`az deployment group create --template-file deploy/main.bicep --parameters @deploy/parameters/dev.bicepparam`)
6. **Bicep provisions all Azure resources** and injects secrets from GitHub via Key Vault

The Bicep parameter files (`dev.bicepparam`, `prod.bicepparam`) define resource sizes and configuration; secrets are never written to files, only injected at deploy time via environment variables.

## Separation from the Engine Repository

This is a **separate GitHub repository** (not vendored in vouchfx). Rationale:

- **Deployment credentials:** Backend secrets (Key Vault, ACR credentials) are separate from engine secrets
- **Deployment cadence:** Backend release cycle is independent of engine releases
- **Team ownership:** Infra team can manage backend deployment without engine code access
- **Binary compatibility:** Backend image versioning (SHA/semver tags) is decoupled from engine versioning
- **Compliance:** Easier to audit and control who has access to production secrets

The engine client (Phase A, merged in PR #155) is inert until a backend endpoint + token are configured. Configuration is driven by optional environment variables (`VOUCHFX_TELEMETRY_ENDPOINT`, `VOUCHFX_TELEMETRY_TOKEN`) that users provide when opting in.
