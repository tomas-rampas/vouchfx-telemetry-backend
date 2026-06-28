# Privacy Policy & Data Handling

S12-G-01 / Issue #152 Phase B

This document describes what data the telemetry backend collects, stores, and deletes, and the privacy guarantees provided by this implementation.

## Data Collection Principles

The vouchfx telemetry system is **privacy-allowlist-only by construction**. It does not collect or transmit:

- Test step names, ids, or contents
- Captured variable values or secrets
- System Under Test (SUT) addresses, hostnames, or URLs
- Container image names or customer-specific identifiers
- Scenario names or test intent
- Any customer data whatsoever

**Structural guarantee:** The `TelemetryEvent` record is an explicit allowlist of permitted fields. Any field not declared on this record has no property to bind to during JSON deserialization, and therefore **cannot be stored no matter how the client sends it**. This physical absence is the "provably never sent" contract.

The engine enforces this allowlist at the client side; the backend enforces it again at the ingest side (strict schema validation for v1 events). Double enforcement is defence-in-depth.

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
| Timestamp | ISO8601 in UTC | When this run ended (client-reported) |

**Closed taxonomies:** Step families (`http`, `db-assert`, `mq-publish`, `mq-expect`, `webhook-listen`, `script`) and step providers (`http.rest`, `db-assert.postgres`, `mq-publish.kafka`, etc.) are frozen enumerations. Any custom/non-Core provider step is bucketed under a generic `"custom"` key so that author-chosen step kind identifiers (which might contain customer names or project identifiers) are never transmitted.

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

**Consequence:** After 90 days, a telemetry row is no longer in the active database and is not accessible via queries.

### Dedup Records (PostgreSQL)

**Default retention:** 35 days

**Mechanism:** `ingest_batch` records (batch-level idempotency keys) are purged on the daily job after 35 days.

**Rationale:** The 35-day window accounts for the client's 30-day outbox cap (maximum time a client retains unsent batches) plus 5 days of Polly exponential back-off headroom. This ensures that a legitimate client retry will find the dedup record and be safely de-duplicated.

**Consequence:** After 35 days, a batch's dedup record is deleted, and a very late retry (unlikely) would be treated as a new batch. This is acceptable because the original data is also aged out of the active store.

### Point-in-Time Restore Backups (Azure)

**Azure-managed backup retention:** 7 days (default)

**Limitation:** Point-in-time restore (PITR) backups are continuous snapshots of the PostgreSQL database. A forgotten install's rows will remain in PITR backups until the backup window rolls off (7 days later). During this window, they cannot be queried via normal SQL, but they are recoverable via PITR restore.

**Risk:** If a customer's forgotten install data must be permanently irrecoverable within hours (not days), PITR residue is a limitation. This is acceptable for the pilot because:
1. The data is aggregate (low sensitivity)
2. PITR recovery requires Azure admin access (not available to customers)
3. Deletion is processed within ~24h (backup residue is short-lived)

**Mitigation:** Keep the PITR window short for the pilot. Customers with stricter requirements would need a longer window (e.g. 30 days) or no PITR at all, with appropriate trade-offs (no point-in-time recovery for disaster recovery).

## Deletion (Forget) Workflow

### User-Initiated Deletion

1. **User runs:** `vouchfx telemetry disable --forget-my-data`
2. **Engine client:**
   - Stops recording telemetry
   - Computes the local install ID (stored in user's home directory)
   - Sends `POST /v1/telemetry/forget` with the install ID
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

### Deletion Guarantees

- **Active-store SLA:** ≤24 hours (next maintenance job cycle) for deletion from live PostgreSQL
- **Dedup-record cleanup:** Automatic within 35 days (or sooner if the daily job runs)
- **PITR residue:** Forgotten data remains in backups for up to 7 days (Azure default)
- **Best-effort:** Backend is a backstop; clients can also request deletion directly (dual responsibility)

### Why Dual Authorization Isn't Implemented (Pilot)

The `/v1/telemetry/forget` endpoint is authorized only by the shared ingest token (no per-install proof required). This means any token holder can request deletion of any install GUID.

**Security trade-off:**
- **Pro:** Simpler implementation; sufficient for pilot (low-value aggregate data)
- **Con:** Attacker with a valid token could request deletion of a guessed install GUID (DOS via deletion, not privilege escalation)

**For production deployment**, consider implementing per-install proof-of-deletion (e.g. a signature embedded in the telemetry envelope) to prevent unauthorized deletion requests.

## Logs & Observability

### What the Backend Logs

- **Ingest success:** `"Ingested batch <idempotencyKey>: <newRows> new row(s), <totalEvents> event(s)"`
  - The install ID is **not** logged
  - The idempotency key is logged (SHA-256 hex of the request body, not reversible)
  
- **Ingest errors:** Parse errors and validation failures (reason logged, but not the raw event data)
  
- **Forget requests:** `"Forget request processed for <installIdPrefix>"`
  - The install ID is truncated to the first 8 characters followed by an ellipsis (e.g. `550e8400…`)
  - The full ID is never logged

### Log Retention

Container Apps logs are sent to Log Analytics and retained according to the workspace's retention policy (configurable, default 30 days). Forgotten data may appear in logs for up to 30 days (separate from active database retention).

**Mitigation (Production):** Set up log redaction rules in Log Analytics to mask install IDs in logs, or use shorter log retention (7 days).

## Third-Party Access

### Microsoft Azure

The backend is deployed on Azure Container Apps and PostgreSQL Flexible Server. Azure infrastructure and operations teams have access to:
- Container image contents (via ACR)
- Runtime logs and metrics (via Log Analytics)
- Database (via Azure support access, governed by Microsoft security policies)

**SLA:** Microsoft follows Azure security policies and compliance standards (SOC2, ISO27001, etc.). Customer data is subject to Microsoft's privacy and security commitments.

### GitHub Actions

Deployment secrets (ingest tokens, database credentials) are stored as GitHub Actions secrets and are:
- Encrypted at rest
- Never logged
- Only exposed to the `Deploy Bicep IaC` step
- Not written to parameter files or version control

**Risk:** If GitHub is compromised, secrets could be exposed. Mitigate by rotating tokens/passwords regularly and using short-lived credentials where possible (e.g. Azure OIDC instead of long-lived tokens).

### Customers (Telemetry Users)

Telemetry team members with access to the PostgreSQL database or Log Analytics workspace can query aggregate data. They see:
- Install ID (opaque GUID), version counts, verdict counts, step family/provider usage
- No step contents, test names, captured values, or SUT details
- Dashboard views `v_step_family_daily`, `v_step_provider_daily`, `v_run_daily` are designed for safe aggregation

**Access control:** Limit database and Log Analytics access to the telemetry team (via Azure RBAC and Key Vault access policies).

## Data Sovereignty & Compliance

- **Storage location:** Determined by the Azure resource group (e.g. `East US`, `West Europe`)
- **Encryption:** Azure-managed encryption at rest (default); HTTPS in transit
- **Compliance:** Depends on Azure region and organization's compliance requirements (GDPR, HIPAA, SOC2, etc.)

**For GDPR compliance:**
- The `installId` is personal data (under GDPR, a unique identifier can be personal data)
- Deletion requests (`/v1/telemetry/forget`) satisfy the "right to be forgotten" within the active-store SLA
- PITR backup residue should be documented in privacy policies
- Customers should opt-in before telemetry is enabled

## Privacy by Design

The vouchfx telemetry backend incorporates privacy-by-design principles:

1. **Data minimization:** Only aggregate counts and non-identifying metrics are stored
2. **Purpose limitation:** Data is used only for telemetry dashboards and product analytics, not for tracking or profiling
3. **Storage limitation:** Data is automatically deleted after 90 days (default)
4. **User control:** Deletion is user-initiated and processed best-effort within 24 hours
5. **Transparency:** This privacy policy documents exactly what is stored and how it is deleted
6. **Encryption:** Data in transit (HTTPS) and at rest (Azure managed keys)

## Recommendations for Production Deployment

Before using this backend in production, review and address:

1. **PITR backup window:** Decide whether 7-day PITR is acceptable for your compliance requirements. Shorten or disable if needed.

2. **Forget authorization:** Implement per-install proof-of-deletion to prevent unauthorized deletion requests.

3. **Database role hardening:** Create a dedicated application role with minimal permissions instead of using the admin role (see operations runbook).

4. **Log redaction:** Set up Log Analytics rules to mask or truncate install IDs in operational logs.

5. **Access control:** Limit database and Key Vault access to the telemetry team using Azure RBAC.

6. **Encryption:** Consider Azure Disk Encryption or Transparent Data Encryption (TDE) for the PostgreSQL database if encryption at rest beyond Azure defaults is required.

7. **Incident response:** Establish procedures for unauthorized access, data breaches, or accidental deletion.

8. **Privacy notice:** Update your product's privacy policy and documentation to inform users about telemetry data collection and deletion options.
