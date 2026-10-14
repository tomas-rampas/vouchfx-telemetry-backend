# Self-hosting the telemetry backend without Azure

A step-by-step guide to building and running the vouchfx telemetry backend on your own infrastructure.

## What you need

### 1. The container image

Build the backend from the repository:

```bash
git clone https://github.com/tomas-rampas/vouchfx-telemetry-backend.git
cd vouchfx-telemetry-backend

# Build the Docker image
docker build -t vouchfx-telemetry-backend:latest .
```

Alternatively, pull from a registry if your operator has published a built image (see your operator's instructions).

### 2. PostgreSQL 16 (the tested and targeted version)

The backend is built and tested on PostgreSQL 16. Earlier versions are untested. You can:

- **Use a local PostgreSQL instance:** `postgresql.org/download/` for self-hosted, or a managed service (AWS RDS, Google Cloud SQL, Digital Ocean, etc.)
- **Use PostgreSQL in a container:** See examples below

The schema is fully self-initialising (see §3 below), so you only need a running PostgreSQL server and admin credentials.

## Step 1: Start PostgreSQL

### Option A: Docker (quickest for testing)

```bash
docker run --rm \
  --name vfx-postgres \
  -e POSTGRES_PASSWORD=changeme \
  -e POSTGRES_DB=vfxtelemetry \
  -p 5432:5432 \
  postgres:16
```

Inside the container, PostgreSQL is listening on port 5432. The admin credentials are:
- Username: `postgres`
- Password: `changeme` (change this!)
- Database: `vfxtelemetry` (created by the `-e POSTGRES_DB=vfxtelemetry` flag above)

### Option B: Local PostgreSQL or managed service

Ensure PostgreSQL is running and note:
- Hostname / IP address
- Port (default 5432)
- Admin username (default: `postgres`)
- Admin password (strong, random)

## Step 2: Bootstrap the database schema

The backend's `DbBootstrapper` runs automatically on every startup against the existing database.
Under one session advisory lock, it runs `deploy/sql/bootstrap.sql`, then
`deploy/sql/bootstrap-schema-evolution.sql`, then configured daily partition seeding, each in a
separate transaction. This keeps partition creation from holding `ingest_batch` locks while waiting
for `telemetry_event`, which would reverse the forget drainer's lock order. Together these phases:

1. Create all tables: `telemetry_event` (partitioned), `ingest_batch`, `forget_queue`
2. Create the supporting indexes on those tables
3. Create functions: `ensure_partitions`, `ensure_partition`, `drop_old_partitions`, `sweep_default`
4. Create views: `v_step_family_daily`, `v_step_provider_daily`, `v_run_daily`
5. Seed `[current_date - RetentionDays, current_date + PrecreateDays)` (defaults: 90 trailing days,
   today and six following days). A shorter retention window is respected on every restart.

The bootstrap is fully idempotent and safe to re-run; it uses `CREATE TABLE IF NOT EXISTS`, `CREATE OR REPLACE FUNCTION`, etc.

**Upgrading:** a backend build that adds a column (e.g. `skipped_event_lines`) takes a brief, bounded-wait exclusive lock on `telemetry_event` on its first start; upgrade the backend before any engines that report the new field, or their events land with the column reading `0` for "not reported" rather than the true count.

**Partition contention:** partition seeding uses a five-second `lock_timeout` per lock wait. A long
reader can cause startup to fail with `55P03`; the partition transaction rolls back and the advisory
lock is released. Retry after that reader completes. Already committed schema phases remain intact.

**Manual bootstrap (if you need to run it separately):**

```bash
# Run from the repository root. Match 90 and 7 to your configured window.
psql -h localhost -U postgres -d vfxtelemetry -v ON_ERROR_STOP=1 <<'SQL'
SET lock_timeout = '30s';
SELECT pg_advisory_lock(152152153);
SET lock_timeout = DEFAULT;
\i deploy/sql/bootstrap.sql
\i deploy/sql/bootstrap-schema-evolution.sql
BEGIN;
SET LOCAL lock_timeout = '5s';
SELECT ensure_partitions(current_date - 90, current_date + 7);
COMMIT;
SELECT pg_advisory_unlock(152152153);
SQL
```

You'll be prompted for the admin password. The one psql session keeps the advisory lock across all
phases, autocommits the schema statements, and seeds partitions in its own explicit transaction.
`ON_ERROR_STOP=1` exits on failure; closing the session releases its advisory lock. Do not combine
the schema scripts and partition seeding into one transaction. On an existing database, running only
`bootstrap.sql` omits schema evolution and ingest can fail.

**Create database explicitly (for non-containerised PostgreSQL):**

For local or managed PostgreSQL instances, create the database before bootstrap:

```bash
# Using psql
psql -h localhost -U postgres -c "CREATE DATABASE vfxtelemetry;"
```

**Verification:**

```bash
psql -h localhost -U postgres -d vfxtelemetry
# Inside psql:
\dt  -- should list: telemetry_event, ingest_batch, forget_queue
\df  -- should list: ensure_partitions, ensure_partition, drop_old_partitions, sweep_default
\dv  -- should list: v_step_family_daily, v_step_provider_daily, v_run_daily
```

## Step 3: Configuration (environment variables)

The backend reads configuration from environment variables. At minimum, you must set:

### Essential configuration

| Variable | Value | Example |
|----------|-------|---------|
| `VOUCHFX_TELEMETRY_INGEST_TOKENS` | Comma-separated bearer tokens (minimum one). Each token should be a high-entropy random string (UUID or 32+ bytes of random hex). | `token-prod-001,token-prod-002` |
| `ConnectionStrings__Telemetry` or `VOUCHFX_TELEMETRY_DB_CONNECTION` | Npgsql connection string to your PostgreSQL database. | `Server=localhost;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=changeme;SslMode=Disable;` |

**Connection string format (Npgsql):**

```
Server=<hostname>;Port=<port>;Database=vfxtelemetry;User Id=<username>;Password=<password>;SslMode=<mode>;
```

- `Server`: PostgreSQL hostname or IP
- `Port`: PostgreSQL port (default 5432)
- `Database`: The database name (must be created before bootstrap runs; typically `vfxtelemetry`)
- `User Id`: Admin username (e.g. `postgres`)
- `Password`: Admin password (in Npgsql keyword format only `;` and quotes need escaping — wrap the value in single quotes if it contains either)
- `SslMode`: `Require` for production (SSL/TLS encryption), `Disable` for local testing

**Note:** The database name is operator-chosen and must match the `Database` value in your connection string. The Azure Bicep template provisions one named `telemetry`; self-hosted deployments typically use `vfxtelemetry`.

### Optional configuration

<!-- Config table duplicated from Program.cs / docs/operations.md Configuration Reference — update those first -->

| Variable | Default | Description |
|----------|---------|-------------|
| `VOUCHFX_TELEMETRY_MAX_BODY_BYTES` | `2097152` (2 MiB) | Maximum request body size. Requests larger than this are rejected with HTTP 413. |
| `VOUCHFX_TELEMETRY_MAX_BATCH_LINES` | `500` | Maximum NDJSON lines per batch. Batches with more lines are rejected with HTTP 413. |
| `VOUCHFX_TELEMETRY_RATE_PERMITS` | `120` | Fixed-window rate limiter: permitted requests per token per window. |
| `VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS` | `60` | Fixed-window duration (seconds). Default: 120 requests/minute per token. |
| `VOUCHFX_TELEMETRY_RETENTION_DAYS` | `90` | Telemetry event retention in days. Partitions with upper bound ≥90 days old are dropped on the daily job. |
| `VOUCHFX_TELEMETRY_PRECREATE_DAYS` | `7` | Number of future day-partitions to pre-create. Default: one week ahead. |
| `VOUCHFX_TELEMETRY_DEDUP_RETENTION_DAYS` | `35` | Ingest-batch dedup record retention in days. Older records are purged on the daily job. Should exceed client's 30-day outbox cap. |
| `VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS` | `24` | Background maintenance job interval (hours). Default: once daily. |
| `ASPNETCORE_URLS` | `http://+:8080` | Kestrel HTTP listener address (already set by the Dockerfile). |

**Example environment file (.env):**

```bash
VOUCHFX_TELEMETRY_INGEST_TOKENS=my-prod-token-abc123,my-dev-token-xyz789
ConnectionStrings__Telemetry=Server=postgres.example.com;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=MyPassword123!;SslMode=Require;
VOUCHFX_TELEMETRY_MAX_BODY_BYTES=2097152
VOUCHFX_TELEMETRY_RETENTION_DAYS=90
```

## Step 4: Health probes

The backend exposes two unauthenticated health-check endpoints, required by most container orchestrators:

| Endpoint | Purpose | Returns |
|----------|---------|---------|
| `GET /healthz` | Liveness probe (is the service alive?) | 200 (always, if process is running) |
| `GET /readyz` | Readiness probe (is the service ready to accept traffic?) | 200 (if database is reachable), 503 (if database is down) |

These are rate-limit exempt and should be polled every few seconds by your orchestrator or load balancer.

## Step 5: Start the backend

### Docker example

```bash
docker run --rm \
  --link vfx-postgres \
  -e VOUCHFX_TELEMETRY_INGEST_TOKENS="my-ingest-token" \
  -e "ConnectionStrings__Telemetry=Server=vfx-postgres;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=changeme;SslMode=Disable;" \
  -p 8080:8080 \
  vouchfx-telemetry-backend:latest
```

The service listens on `http://localhost:8080` by default.

### Docker Compose example

Use the Docker Compose v2 plugin. Save this as `compose.yaml` in the repository root:

```yaml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: changeme
      POSTGRES_DB: vfxtelemetry
    ports:
      - "5432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 5s
      timeout: 5s
      retries: 5

  telemetry-backend:
    build: .
    environment:
      VOUCHFX_TELEMETRY_INGEST_TOKENS: "my-ingest-token"
      ConnectionStrings__Telemetry: "Server=postgres;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=changeme;SslMode=Disable;"
      VOUCHFX_TELEMETRY_RETENTION_DAYS: "90"
    ports:
      - "8080:8080"
    depends_on:
      postgres:
        condition: service_healthy
```

From the repository root, verify the plugin and start both services:

```bash
docker compose version
docker compose up --build
```

Keep this terminal running. In another terminal, wait for `GET http://localhost:8080/readyz`
to return HTTP 200 before sending the smoke-test event below.

### Kubernetes example (Helm values file)

Cluster nodes need a registry image they can pull; the local Docker build above does not publish
one. Build, tag, and push to your own reachable registry first. Replace the example registry and
namespace below, authenticate to that registry, and choose a unique tag for your source build:

```bash
IMAGE_REPOSITORY="registry.example.com/your-team/vouchfx-telemetry-backend"
IMAGE_TAG="source-$(git rev-parse --short=12 HEAD)"
docker build -t "$IMAGE_REPOSITORY:$IMAGE_TAG" .
docker push "$IMAGE_REPOSITORY:$IMAGE_TAG"
```

No Helm chart is shipped in this repository. These values are illustrative; adapt them to your
chosen chart's image, environment, and probe templates. Replace `image.repository` and
`image.tag` with the full registry repository and exact tag you pushed. For an immutable image
reference, record the digest printed by `docker push`, use your chart's supported digest syntax,
and verify the rendered Pod image is `registry.example.com/your-team/vouchfx-telemetry-backend@sha256:<digest>`
with your repository and digest substituted, without an appended tag. Configure image-pull
credentials if the registry is private. This example does not imply a public project image or a
validated Kubernetes deployment.

```yaml
image:
  repository: registry.example.com/your-team/vouchfx-telemetry-backend
  tag: source-your-commit
  pullPolicy: Always

replicaCount: 1

env:
  - name: VOUCHFX_TELEMETRY_INGEST_TOKENS
    valueFrom:
      secretKeyRef:
        name: telemetry-secrets
        key: ingest-tokens
  - name: ConnectionStrings__Telemetry
    valueFrom:
      secretKeyRef:
        name: telemetry-secrets
        key: db-connection-string
  - name: VOUCHFX_TELEMETRY_RETENTION_DAYS
    value: "90"

livenessProbe:
  httpGet:
    path: /healthz
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /readyz
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 5
```

## Step 6: Generate ingest tokens

Tokens are arbitrary strings. You can generate them with:

```bash
# Using OpenSSL (secure, random 32 bytes)
openssl rand -hex 32

# Using uuidgen
uuidgen

# Using Python
python3 -c "import secrets; print(secrets.token_hex(32))"

# Using PowerShell
[System.Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Store tokens securely (e.g. a secrets manager, env var file, or Kubernetes secret). Never commit them to version control.

## Step 7: Smoke test — send a telemetry event

To verify the backend is working, send a test event:

```bash
# Generate an idempotency key (SHA-256 of the request body)
BODY='{"schemaVersion":1,"timestamp":"2026-06-28T14:30:00+00:00","installId":"550e8400-e29b-41d4-a716-446655440000","toolVersion":"1.0.0","engineVersion":"1.0.0","dotnetVersion":".NET 8.0.7","runCount":1,"scenarioCount":1,"stepVerdicts":{"pass":1,"fail":0,"envError":0,"inconclusive":0},"scenarioVerdicts":{"pass":1,"fail":0,"envError":0,"inconclusive":0},"stepFamilies":{"http":1},"stepProviders":{"http.rest":1},"startupMs":100,"timeToFirstTestMs":200}'
IDEMPOTENCY_KEY=$(echo -n "$BODY" | sha256sum | awk '{print $1}')

curl -X POST \
  -H "Content-Type: application/x-ndjson" \
  -H "Authorization: Bearer my-ingest-token" \
  -H "Idempotency-Key: $IDEMPOTENCY_KEY" \
  --data "$BODY" \
  http://localhost:8080/v1/telemetry
```

Expected response: `HTTP 200 OK` (empty body)

**Verify the data was stored:**

```bash
psql -h localhost -U postgres -d vfxtelemetry -c \
  "SELECT COUNT(*) FROM telemetry_event;"
```

Should return `1` (one row inserted).

## Step 8: Point the engine at your backend

On client machines where vouchfx is installed, enable telemetry and configure the endpoint:

```bash
vouchfx telemetry enable

export VOUCHFX_TELEMETRY_ENDPOINT="https://your-backend-hostname.example.com"
export VOUCHFX_TELEMETRY_TOKEN="my-ingest-token"

vouchfx run …
```

> **TLS note:** the container itself serves plain HTTP on port 8080 (`ASPNETCORE_URLS=http://+:8080`) —
> it does not terminate TLS. For an `https://` endpoint, put a TLS-terminating reverse proxy or
> ingress in front of the container. Plain `http://host:8080` is fine for local testing, but the
> Bearer token and payload traverse cleartext, so use HTTPS for anything beyond localhost.

The engine will collect telemetry during the run and attempt to POST it to your backend. If the endpoint is unreachable or the token is invalid, the engine stays silent (fail-silent guarantee) and the run completes normally; events accumulate in the local outbox and are retried on a subsequent run (the engine backs off between failed drain attempts).

For full details on client-side telemetry configuration, see the [vouchfx engine's telemetry documentation](https://vouchfx.io/telemetry/).

## What Azure adds (if you use Azure in production)

If you deploy to Azure instead of self-hosting, the Azure path adds:

- **Managed PostgreSQL:** Azure handles backup/restore, patches, SSL/TLS termination
- **Point-in-time restore (PITR):** Automatic 7-day backup window (configurable)
- **Azure Key Vault:** Secure token/credential storage with access controls
- **Container Apps:** Managed container orchestration, auto-scaling, health probes
- **Log Analytics:** Centralised logging and monitoring
- **Application Insights:** Performance and error tracking

See [Operations Runbook](operations.md) for the full Azure deployment workflow and Bicep templates.

## Next steps

- **Monitor:** Set up logging and alerting (e.g. PostgreSQL slow-query logs, application metrics)
- **Maintenance:** Run the daily job (partition cleanup, forget-queue drain) via a cron task or Kubernetes CronJob
- **Scaling:** Tune rate limits, retention, and database resources based on load
- **Security:** Use SSL/TLS for all connections; rotate tokens periodically; limit database access to the application user

## Isolated hosted acceptance

After publishing and independently checking a stable Core release (1.0.2 or later), run the
manual **Self-hosted telemetry acceptance** workflow. Supply its public version, full release
commit and the SHA-256 of the official `vouchfx` NuGet package. The workflow verifies the
GitHub tag and NuGet repository metadata before installing those exact package bytes.

It builds this repository's Dockerfile and checks Production Kestrel plus PostgreSQL on an
owned network, with an ephemeral loopback-only HTTP port. It covers missing/wrong authentication,
schema-2 fixture ingestion and replay, allowlist refusal, then actual CLI opt-in, schema-2
transport, local opt-out and backend forget delivery. Forget processing is verified by restarting
only the owned backend, which runs its normal startup maintenance; this does not imply immediate
forget processing on a continuously running deployment.

The artefact contains source/package identities, command exits, HTTP/SQL observations, JUnit
results and cleanup/foreign-resource checks. A successful image build or fixture ingestion alone
is not a real CLI transport result. `scripts/accept_self_hosted.py` refuses local or self-hosted
runner execution; the manual workflow uses an isolated GitHub-hosted runner. Ephemeral tokens,
PostgreSQL credentials, tool files and private CLI state are excluded from the artefact.

Command output is drained concurrently, capped at 1 MiB per stream and 1.5 MiB per command.
The run retains at most 40 MiB of raw command output, with 8 MiB reserved for cleanup.
Overflow fails the command. Raw captures stay private; only bounded, sanitized files in the
atomically finalized `published/` directory are uploaded. Incomplete child closure or
sanitization refuses publication. Failed builds also recover and remove exact labelled images.

### Failure-control gate

The **Telemetry acceptance failure controls** workflow exercises the real acceptance helper
with deterministic in-memory process, pipe, clock and Docker boundaries. It runs when the
helper, harness or acceptance workflows change, and the manual production journey requires
this job to succeed first. These controls require no Docker daemon or public Core package.

The nine required controls cover stdout, stderr and aggregate overflow; dummy credentials
during interruption; byte-redaction failure; uncertain child reap; failed builds with an IID;
owned-image recovery without an IID; and interruption before observed EOF, including an
exited leader with an inherited pipe. Assertions check original failures and exits, byte limits,
owned cleanup, foreign-resource preservation and refusal to publish unsafe captures.

Every run must report **9 passed, 0 failed, 0 skipped** in `results.json` and `junit.xml`.
The workflow attempts to retain these two sanitized records even when a control fails.
Private raw captures remain below the runner temporary directory and are never uploaded.
Each result binds the backend source, helper and harness hashes. A passing control gate
validates the helper failure paths; the separately bound public CLI/Production journey must
still pass before self-hosted transport is accepted.

The harness is `scripts/test_accept_self_hosted_controls.py`; it admits only the repository's
isolated GitHub-hosted workflow. Adding the harness or reviewing its source does not establish
a passing run. Check the workflow's retained records for the exact commit being evaluated.

Control result admission compares the full backend commit and helper/harness SHA-256 hashes
to the checked-out source. JSON schema 2 requires integer counts (booleans and floats are
refused), exact case and original-record sets, and matching strict JUnit. Original sanitized
record values use `utf8-json-sortkeys-compact-v1`: UTF-8 JSON with sorted keys, compact
separators, Unicode characters preserved and non-finite numbers refused. The retained
`canonicalSha256` binds that representation; it does not claim the private file's raw-byte hash.

The hosted harness also requires 42 negative admission fixtures to refuse missing/wrong
source hashes, altered record hashes/values/variants/paths, changed case sets or verdicts,
and boolean/float counts. These checks mutate the actual helper result. Only results.json
and junit.xml remain upload eligible; a passing source review is not a passing control run.
