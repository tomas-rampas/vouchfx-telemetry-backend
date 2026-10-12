# Privacy Policy & Data Handling

This document describes the reference implementation's allowed data and default maintenance behavior. It operates no maintainer-hosted service. Hosting, access controls, backup retention and service commitments belong to the operator.

**Note:** For opt-in/out mechanics and data sending, see [Why telemetry & how to opt in](why-telemetry.md). This page describes the default 90-day retention window and forget workflow. Operators must document their configured retention, deletion deadlines and backup policies; these defaults do not establish a hosted-service SLA.

## Data Collection Principles

The vouchfx telemetry system is **privacy-allowlist-only by construction**. It does not collect or transmit:

- Test step names, ids, or contents
- Captured variable values or secrets
- System Under Test (SUT) addresses, hostnames, or URLs
- Container image names or customer-specific identifiers
- Scenario names or test intent
- Any customer data whatsoever

**Structural guarantee:** The `TelemetryEvent` record is an explicit allowlist of permitted fields. Any field not declared on this record has no property to bind to during JSON deserialization, and therefore **cannot be stored no matter how the client sends it**. This physical absence is the "provably never sent" contract.

The engine enforces this allowlist at the client side; the backend enforces it again at the ingest side (strict schema validation for schema versions 1–2, the versions this backend knows). Double enforcement is defence-in-depth.

Usage-map keys are validated against the frozen Core family/provider taxonomy plus `custom`, at every schema version. Unknown keys are rejected rather than stored or echoed in logs. Counts and timings must be non-negative; required reference values cannot be null. New Core taxonomy tokens require a deliberate backend allowlist update before clients send them.

## Allowed Data

The backend stores only these aggregate, non-identifying metrics per run:

| Category | Example Values | Purpose |
|----------|-----------------|---------|
| Install ID | GUID | Unique identifier for this vouchfx installation (minted at opt-in; deleted on `telemetry disable`) |
| Versions | tool: "1.0.0", engine: "1.0.0", .NET: ".NET 8.0.7" | Track which tool/runtime combinations are in use |
| Run counts | runCount: 1, scenarioCount: 5 | How many scenarios executed per run |
| Verdict counts | stepPass: 40, stepFail: 2, stepEnvError: 1 | Per-verdict step/scenario counts (no names, no ids) |
| Step family counts | {"http": 3, "db-assert": 1} | Which step families were used (closed taxonomy, no customer data) |
| Step provider counts | {"http.rest": 3, "db-assert.postgres": 1} | Which built-in providers were used (closed taxonomy) |
| Timing metrics | startupMs: 5000, timeToFirstTestMs: 8500 | Non-identifying wall-clock durations (milliseconds) |
| Skipped event-stream lines | skippedEventLines: 2 | A count only: how many event-stream lines the engine's telemetry builder could not read while building this event. Never which line or what it contained. Required at `schemaVersion` 2 (issue #30); absent at `schemaVersion` 1; reads as `0` when absent above `schemaVersion` 2. |
| Timestamp | ISO8601 in UTC | When this run ended (client-reported) |

**Closed taxonomies:** Step families (all eleven Core families: `http`, `db-assert`, `mq-publish`, `mq-expect`, `cache-assert`, `mail-expect`, `webhook-listen`, `metrics-assert`, `storage-assert`, `trace-expect`, `script`) and step providers (the twenty-five Core `family.provider` ids — `http.rest`, `db-assert.postgres`, `mq-publish.kafka`, etc.; the [engine README](https://github.com/tomas-rampas/vouchfx#readme) carries the full catalogue) are frozen enumerations. Any custom/non-Core provider step is bucketed under a generic `"custom"` key so that author-chosen step kind identifiers (which might contain customer names or project identifiers) are never transmitted. (Engines released up to `v1.0.0-alpha.5` counted only the original six families and six provider ids; on those versions the remaining Core steps also appear under `"custom"`.)

## Installation ID

The `installId` is the **only row-level identifier** stored. It is:

- **Opaque:** A randomly-generated GUID with no internal structure or meaning
- **Installation-scoped:** Unique to one vouchfx CLI installation (minted at opt-in, persists across runs until deleted)
- **Deletion-enabled:** Deleted when the user runs `vouchfx telemetry disable`
- **Non-identifying:** Does not correlate to the user, machine, organization, IP address, or any test content

An install ID is **not** a user ID or device ID. Deleting it severs the link between telemetry events and the source. The backend has no way to identify *who* or *what machine* sent the data.

## Data Retention

### Active Store (PostgreSQL)

**Default retention:** 90 days

**Mechanism:** Daily maintenance job drops PostgreSQL partitions whose upper bound is ≥90 days old (configurable via `VOUCHFX_TELEMETRY_RETENTION_DAYS`).

**Partition lifecycle:**
- Daily partitions (e.g. `telemetry_event_20260628`) are created automatically
- Old partitions are dropped on the daily job
- DEFAULT catch-all partition is swept (aged rows deleted, rather than dropped)

**Consequence:** Successful daily maintenance removes partitions beyond the configured window and sweeps aged rows from the default partition. Processing follows the configured schedule and requires a healthy maintenance job; 90 days is not an exact deletion deadline.

### Dedup Records (PostgreSQL)

**Default retention:** 35 days

**Mechanism:** `ingest_batch` records (batch-level idempotency keys) are purged on the daily job after 35 days.

**Rationale:** The 35-day window accounts for the client's 30-day outbox cap (maximum time a client retains unsent batches) plus 5 days of Polly exponential back-off headroom. This ensures that a legitimate client retry will find the dedup record and be safely de-duplicated.

**Consequence:** When a batch's dedup record expires, a late retry is treated as a new batch at the batch-dedup layer. Event rows have a separate retention window (90 days by default), so dedup expiry does not erase the original telemetry. [Row-level natural-key deduplication](wire-contract.md#layer-2-row-level-dedup-natural-key) still applies to retained events. Both retention windows are independently configurable.

<a id="point-in-time-restore-backups-azure"></a>

### Point-in-Time Restore Backups

Backup retention is deployment-specific. The optional Azure PostgreSQL configuration
uses a seven-day backup window by default. If an operator enables PITR, forgotten
rows can remain recoverable until that deployment's backup window expires, even
after deletion from the active database.

Operators must document who can restore backups, their retention window and how
restores handle previously received deletion requests. A requirement for faster
irrecoverability calls for a shorter backup window or no PITR, with the corresponding
disaster-recovery trade-off.

## Deletion (Forget) Workflow

### User-Initiated Deletion

1. **User runs:** `vouchfx telemetry disable`
2. **Engine client:**
   - Stops recording telemetry
   - Computes the local install ID (stored in user's home directory)
   - Sends `POST /v1/telemetry/forget` with the install ID (best-effort)
3. **Backend:**
   - Validates the request (401 if token invalid, 400 if malformed)
   - Enqueues the forget request (insert into `forget_queue`)
   - Returns 200 OK to the client immediately

### Backend Processing

4. **Daily maintenance job** (up to 24 hours later):
   - Acquires PostgreSQL advisory lock (so only one replica runs)
   - Queries `forget_queue WHERE processed_at IS NULL`
   - For each pending row, within a transaction:
     - `DELETE FROM telemetry_event WHERE install_id = $1`
     - `DELETE FROM ingest_batch WHERE install_id = $1`
     - `UPDATE forget_queue SET processed_at = now() WHERE install_id = $1`
   - If all three succeed, the forget is marked processed
   - If any fails, the transaction rolls back and the forget is retried next cycle

<a id="deletion-guarantees"></a>

### Deletion Processing and Operator Commitments

- **Active store:** a received forget request is processed by the next successful
  maintenance cycle; the default interval is 24 hours. Failed transactions remain
  queued for a later cycle.
- **Dedup records:** the same forget transaction deletes the install's dedup records.
  The default 35-day retention is the separate routine policy for other batches.
- **Backups:** removal from the active store does not erase retained backup copies.
  The operator's backup and restore policy determines their remaining lifetime.
- **Delivery:** the engine's forget request is best-effort. Operators must provide a
  way to handle deletion requests that could not reach the backend.
- **Service commitments:** an operator offering a 30-day or other deletion deadline
  must monitor request delivery, queued work and maintenance failures. This reference
  implementation does not guarantee a deployment's SLA.

### Why Dual Authorization Isn't Implemented (Pilot)

The `/v1/telemetry/forget` endpoint is authorized only by the shared ingest token (no per-install proof required). This means any token holder can request deletion of any install GUID.

**Security trade-off:**
- **Pro:** Simpler implementation; sufficient for pilot (low-value aggregate data)
- **Con:** Attacker with a valid token could request deletion of a guessed install GUID (DOS via deletion, not privilege escalation)

**For production deployment**, consider implementing per-install proof-of-deletion (e.g. a signature embedded in the telemetry envelope) to prevent unauthorized deletion requests.

## Logs & Observability

### What the Backend Logs

- **Ingest success:** `"Ingested batch <idempotencyKey>: <newRows> new row(s), <totalEvents> event(s)"`
  - The install ID is **not** logged by this success line
  - The idempotency key is logged (SHA-256 hex of the request body, not reversible)
  
- **Ingest errors:** Parse errors and validation failures are logged at Information via `LogMalformedBatch`. Refusal reasons contain a line number and fixed validation text only. JSON exception messages, paths, unknown property names, map keys, and malformed literal fragments are never included. The caller receives the generic HTTP 400 response `Malformed event data.`; invalid batches never reach storage.
  
- **Forget requests:** `"Forget request processed for <installIdPrefix>"`
  - The install ID is truncated to the first 8 characters followed by an ellipsis (e.g. `550e8400…`)
  - The full ID is never logged

### Log Retention

For the optional Azure path, Container Apps logs are sent to Log Analytics and retained according to the configured workspace policy. Other deployments use their selected log platform. Log retention is separate from active database retention and belongs to the operator.

**Operator responsibility:** restrict log access, review emitted fields and set the log-retention window. Azure operators can configure these controls in Log Analytics.

## Third-Party Access

<a id="microsoft-azure"></a>

### Optional Microsoft Azure Deployment

If an operator selects Azure Container Apps and PostgreSQL Flexible Server, access
to images, operational logs and the database is governed by that deployment's Azure
roles and service policies. The reference repository has no maintainer-hosted Azure
instance. Operators must document the infrastructure providers and access policies
they actually use.

<a id="github-actions"></a>

### Optional GitHub Actions Deployment

When using the retained Azure deployment workflow, ingest tokens and database
credentials are configured as GitHub Actions secrets and passed to secure Bicep
parameters in the deployment step. They are not committed to source or parameter
files. Self-hosting with Docker or another orchestrator does not require this path.

Limit access to deployment credentials and rotate them according to the operator's
policy. The Azure workflow uses OIDC for Azure authentication.

### Customers (Telemetry Users)

People the operator authorizes to access PostgreSQL or its configured log platform can query aggregate data. They see:
- Install ID (opaque GUID), version counts, verdict counts, step family/provider usage
- No step contents, test names, captured values, or SUT details
- Dashboard views `v_step_family_daily`, `v_step_provider_daily`, `v_run_daily` are designed for safe aggregation

**Access control:** The operator controls database, log and secret-store permissions. The optional Azure path uses Azure roles and Key Vault access policies; other deployments need equivalent controls.

<a id="data-sovereignty-compliance"></a>

## Hosting and Operator Privacy Review

- **Storage location:** determined by the operator's selected database and hosting region.
- **Encryption:** the container serves HTTP; HTTPS requires a TLS-terminating proxy or
  ingress. Encryption at rest depends on the selected database and storage configuration.
- **Retention and deletion:** document the configured maintenance schedule, request-delivery
  process, backup retention and restore procedures in the operator's privacy notice.
- **Applicable obligations:** the operator must assess its deployment and usage requirements.
  The reference implementation's configuration and tests do not establish legal compliance.

## Privacy by Design

The vouchfx telemetry backend incorporates privacy-by-design principles:

1. **Data minimization:** Only aggregate counts and non-identifying metrics are stored
2. **Purpose limitation:** Data is used only for telemetry dashboards and product analytics, not for tracking or profiling
3. **Storage limitation:** The default retention window is 90 days, applied by successful maintenance
4. **User control:** Request delivery is best-effort; received requests are processed by a successful maintenance cycle
5. **Transparency:** This privacy policy documents exactly what is stored and how it is deleted
6. **Encryption:** The operator configures HTTPS termination and encryption at rest

## Recommendations for Production Deployment

Before using this backend in production, review and address:

1. **PITR backup window:** Decide whether 7-day PITR is acceptable for your compliance requirements. Shorten or disable if needed.

2. **Forget authorization:** Implement per-install proof-of-deletion to prevent unauthorized deletion requests.

3. **Database role hardening:** Create a dedicated application role with minimal permissions instead of using the admin role (see operations runbook).

4. **Log handling:** Set access and retention rules for the configured log platform; on Azure this is Log Analytics.

5. **Access control:** Limit database and secret-store access to authorized operators; use Azure roles when selecting the Azure path.

6. **Encryption:** Verify TLS termination and the database/storage encryption settings for the selected hosting platform.

7. **Incident response:** Establish procedures for unauthorized access, data breaches, or accidental deletion.

8. **Privacy notice:** Update your product's privacy policy and documentation to inform users about telemetry data collection and deletion options.
