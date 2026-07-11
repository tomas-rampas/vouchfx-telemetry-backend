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

The backend's `DbBootstrapper` runs automatically on first startup and executes `deploy/sql/bootstrap.sql` within the existing `vfxtelemetry` database. This:

1. Creates all tables: `telemetry_event` (partitioned), `ingest_batch`, `forget_queue`
3. Creates functions: `ensure_partitions`, `ensure_partition`, `drop_old_partitions`, `sweep_default`
4. Creates views: `v_step_family_daily`, `v_step_provider_daily`, `v_run_daily`
5. Pre-creates daily partitions from 90 days ago through roughly one week ahead

The bootstrap is fully idempotent and safe to re-run; it uses `CREATE TABLE IF NOT EXISTS`, `CREATE OR REPLACE FUNCTION`, etc.

**Manual bootstrap (if you need to run it separately):**

```bash
# Using psql (Postgres command-line client)
psql -h localhost -U postgres -d vfxtelemetry -f deploy/sql/bootstrap.sql
```

You'll be prompted for the admin password.

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
- `Password`: Admin password (URL-safe; avoid `@`, `%`, or special chars that need escaping)
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

```yaml
version: '3.8'

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

Start both services:

```bash
docker-compose up
```

### Kubernetes example (Helm values file)

```yaml
image:
  repository: vouchfx-telemetry-backend
  tag: latest
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

The engine will collect telemetry during the run and attempt to POST it to your backend. If the endpoint is unreachable or the token is invalid, the engine stays silent (fail-silent guarantee) and the run completes normally; events accumulate in the local outbox and will retry on the next run.

For full details on client-side telemetry configuration, see the [vouchfx engine's telemetry documentation](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html).

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
