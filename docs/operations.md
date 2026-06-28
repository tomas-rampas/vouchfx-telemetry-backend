# Operations Runbook

S12-G-01 / Issue #152 Phase B

This document is the operator's guide to deploying, configuring, monitoring, and troubleshooting the vouchfx telemetry backend.

## Deployment Overview

This is a **complete, build-ready implementation** that requires operator provisioning and deployment. The repository contains no deployed backend; you must:

1. Create a GitHub repository (fork or new)
2. Configure GitHub secrets and variables
3. Provision Azure resources via Bicep
4. Deploy the container image to Azure Container Apps
5. Initialize the database schema

## Pre-Deployment Checklist

- [ ] GitHub repository (private or public) created
- [ ] Azure subscription, resource group, and ACR set up
- [ ] Entra App Registration with federated credential for GitHub OIDC (see deploy.yml header)
- [ ] Azure Key Vault access; Secret Officer role on the vault
- [ ] PostgreSQL admin password and ingest tokens (secure random values) generated
- [ ] Local `az` CLI authenticated to the target subscription

## Step 1: Configure GitHub Secrets and Variables

All values are set in **Settings → Secrets and variables → Secrets** (secrets) and **Variables** (non-secret env vars).

### Required Variables (Settings → Variables)

| Name | Value | Example |
|------|-------|---------|
| `AZURE_CLIENT_ID` | Entra App Registration client ID (used for OIDC, not a secret) | `f47ac10b-58cc-4372-a567-0e02b2c3d479` |
| `AZURE_TENANT_ID` | Azure AD tenant ID | `12345678-1234-1234-1234-123456789012` |
| `AZURE_SUBSCRIPTION_ID` | Target Azure subscription ID | `abcdef01-2345-6789-abcd-ef0123456789` |
| `AZURE_RESOURCE_GROUP` | Target resource group name (must already exist) | `rg-vfx-telemetry-prod` |
| `ACR_LOGIN_SERVER` | ACR FQDN (Azure Container Registry) | `vfxtelregistry.azurecr.io` |
| `DEPLOY_ENVIRONMENT` | Environment name: `dev` or `prod` (selects parameter file) | `prod` |

### Required Secrets (Settings → Secrets)

| Name | Value | Notes |
|------|-------|-------|
| `TELEMETRY_INGEST_TOKENS` | Comma-separated bearer tokens (at least one). Each token is a high-entropy string (minimum 32 characters, e.g. a UUID or random 32-byte hex). | `token-prod-001,token-prod-002` (no spaces after commas). On first deployment, use a PLACEHOLDER like `PLACEHOLDER_REPLACE_ME` and rotate it immediately after the first deployment succeeds. |
| `DB_ADMIN_PASSWORD` | PostgreSQL admin password (strong, random, ≥20 chars). Must be URL-safe (no `@`, `%`, or special chars that require escaping in Npgsql connection strings). | Store securely (e.g. 1Password, Azure Key Vault, or a secrets manager). |
| `DB_CONNECTION_STRING` | Full Npgsql connection string to the Postgres database, including hostname, port, database name, admin username, and password. Format: `Server=<host>;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=<pwd>;SslMode=Require;` | See Step 2 below. |

**PLACEHOLDER workflow:**
1. Set `TELEMETRY_INGEST_TOKENS` to a placeholder (e.g. `PLACEHOLDER_INITIAL`) on initial deployment
2. Deploy successfully to Azure
3. Immediately re-run the workflow with a real token value and redeploy
4. Test the real token in a dev environment before production use

## Step 2: Provision Azure Resources (Bicep)

The `deploy/main.bicep` template orchestrates all resources. You have two options:

### Option A: GitHub Actions (Recommended)

1. Trigger the `deploy` workflow from **Actions** (or push a release tag if using release-triggered deployment)
2. The workflow will:
   - Build the .NET service
   - Push the image to ACR
   - Run Bicep deployment
   - Emit outputs (Container App FQDN, Postgres server FQDN)

### Option B: Local `az` CLI

```bash
# Ensure you are logged in
az account show

# Validate the Bicep template
az bicep build-params --file deploy/parameters/prod.bicepparam

# Deploy (you must set VFX_* env vars manually)
export VFX_ACR_LOGIN_SERVER="vfxtelregistry.azurecr.io"
export VFX_IMAGE_TAG="v1.0.0"
export VFX_INGEST_TOKENS="real-token-value"
export VFX_DB_ADMIN_PASSWORD="your-postgres-password"
export VFX_DB_CONNECTION_STRING="Server=vfxtel-prod-pg.postgres.database.azure.com;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=...;SslMode=Require;"

az deployment group create \
  --resource-group "rg-vfx-telemetry-prod" \
  --template-file deploy/main.bicep \
  --parameters "@deploy/parameters/prod.bicepparam"
```

### PostgreSQL Connection String

After the Postgres resource is provisioned, construct the connection string:

```
Server=<server_hostname>;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=<admin_password>;SslMode=Require;
```

**Values:**
- `<server_hostname>`: Output from `az bicep build` or Bicep deployment; typically `vfxtel-prod-pg.postgres.database.azure.com`
- `<admin_password>`: The value you provided in `DB_ADMIN_PASSWORD` secret
- `SslMode=Require`: Mandatory for Azure Postgres

**Example:**
```
Server=vfxtel-prod-pg.postgres.database.azure.com;Port=5432;Database=vfxtelemetry;User Id=postgres;Password=MyP@ssw0rd123!;SslMode=Require;
```

Update the `DB_CONNECTION_STRING` secret in GitHub with this value before (or immediately after) deploying.

## Step 3: Database Bootstrap

The Container App's startup sequence automatically runs `DbBootstrapper.BootstrapAsync()` if a connection string is configured. This:

1. Executes `deploy/sql/bootstrap.sql` (fully idempotent: `CREATE TABLE IF NOT EXISTS`, `CREATE OR REPLACE FUNCTION`, etc.)
2. Creates the schema: `telemetry_event` parent + DEFAULT partition, `ingest_batch`, `forget_queue`, functions, views
3. Pre-creates daily partitions for the past 90 days + the next 7 days

**Verification:**
```bash
# Connect to Postgres (from your workstation with Postgres client installed)
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry

# Inside psql:
\dt  -- list tables
\df  -- list functions
\dv  -- list views
SELECT schemaname, tablename FROM pg_tables WHERE tablename LIKE 'telemetry_event%';
```

If bootstrap fails (e.g. network connectivity, wrong password), the Container App will not pass the readiness probe and Azure will not route traffic to it. Check **Container Apps → Logs** in the Azure Portal.

## Configuration Reference

All environment variables are configurable and have defaults. Most scenarios only require setting the two secrets (`TELEMETRY_INGEST_TOKENS` and `ConnectionStrings__Telemetry`); the defaults are suitable for pilot scale.

### Environment Variables & Defaults

| Name | Default | Type | Description |
|------|---------|------|-------------|
| `VOUCHFX_TELEMETRY_INGEST_TOKENS` | (empty) | string | Comma-separated bearer tokens. If empty, all requests are rejected with 401. |
| `ConnectionStrings__Telemetry` or `VOUCHFX_TELEMETRY_DB_CONNECTION` | (empty) | string | Npgsql connection string. If empty, the app starts in no-op mode (dev only). In Production, the app refuses to start without a DB. |
| `VOUCHFX_TELEMETRY_MAX_BODY_BYTES` | `2097152` (2 MiB) | long | Kestrel max request body size. Requests larger than this are rejected with HTTP 413. |
| `VOUCHFX_TELEMETRY_MAX_BATCH_LINES` | `500` | int | Maximum NDJSON lines per batch. Batches with more lines are rejected with HTTP 413. |
| `VOUCHFX_TELEMETRY_RATE_PERMITS` | `120` | int | Fixed-window rate limiter: permitted requests per token per window. |
| `VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS` | `60` | int | Fixed-window duration (seconds). Default: 120 req/min per token. |
| `VOUCHFX_TELEMETRY_RETENTION_DAYS` | `90` | int | Telemetry event retention. Partitions with upper bound ≥90 days old are dropped on the daily job. |
| `VOUCHFX_TELEMETRY_PRECREATE_DAYS` | `7` | int | Number of future day-partitions to pre-create. Default: one week ahead. |
| `VOUCHFX_TELEMETRY_DEDUP_RETENTION_DAYS` | `35` | int | Ingest-batch dedup record retention. Older records are purged on the daily job. Must exceed the client's 30-day outbox cap plus back-off headroom. |
| `VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS` | `24` | int | Background maintenance job interval (hours). Default: once daily. |
| `ASPNETCORE_URLS` | `http://+:8080` | string | Kestrel listen address (set by Dockerfile). |

### Tuning for Production

**Higher throughput (~1000 req/sec):**
```
VOUCHFX_TELEMETRY_RATE_PERMITS=240            # 240 req/min per token
VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS=60      # 60-second window
```

**More retention (180 days instead of 90):**
```
VOUCHFX_TELEMETRY_RETENTION_DAYS=180
```

**Larger body limit (for dense batches):**
```
VOUCHFX_TELEMETRY_MAX_BODY_BYTES=5242880      # 5 MiB
```

## Token Rotation (Zero-Downtime)

The backend supports multiple ingest tokens. To rotate tokens without downtime:

1. **Add the new token** to the secret value:
   ```
   TELEMETRY_INGEST_TOKENS: old-token,new-token
   ```
   Redeploy.

2. **Wait for client migration** (old token to stop being used, new token in active use).

3. **Remove the old token**:
   ```
   TELEMETRY_INGEST_TOKENS: new-token
   ```
   Redeploy.

During the overlap window (old and new tokens both valid), existing clients can continue using the old token, and new clients can use the new token. No requests are dropped.

## Operational Procedures

### Verify Deployment Health

```bash
# Check Container App is running
az containerapp show -g rg-vfx-telemetry-prod -n vfxtel-prod-app

# Check replica count and status
az containerapp revision list -g rg-vfx-telemetry-prod -n vfxtel-prod-app

# Check logs (last 100 lines)
az containerapp logs show -g rg-vfx-telemetry-prod -n vfxtel-prod-app --tail 100

# Check HTTP probes
curl https://vfxtel-prod-app.example.azurecontainers.io/healthz
curl https://vfxtel-prod-app.example.azurecontainers.io/readyz
```

### Verify Database Connectivity

```bash
# From Container App logs, check for "Maintenance job running" messages
az containerapp logs show -g rg-vfx-telemetry-prod -n vfxtel-prod-app --tail 200 | grep -i maintenance

# Connect directly to Postgres
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry -c "SELECT NOW();"

# Check partition status
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT schemaname, tablename FROM pg_tables WHERE tablename LIKE 'telemetry_event%' ORDER BY tablename;"
```

### Test a Round-Trip Ingest + Forget

**Ingest a test batch:**

```bash
ENDPOINT="https://vfxtel-prod-app.example.azurecontainers.io"
TOKEN="your-real-ingest-token"
INSTALL_ID="550e8400-e29b-41d4-a716-446655440000"

# Create a minimal test event
BODY="{\"schemaVersion\":1,\"timestamp\":\"2026-06-28T14:30:00Z\",\"installId\":\"$INSTALL_ID\",\"toolVersion\":\"1.0.0\",\"engineVersion\":\"1.0.0\",\"dotnetVersion\":\".NET 8.0.7\",\"runCount\":1,\"scenarioCount\":1,\"stepVerdicts\":{\"pass\":1,\"fail\":0,\"envError\":0,\"inconclusive\":0},\"scenarioVerdicts\":{\"pass\":1,\"fail\":0,\"envError\":0,\"inconclusive\":0},\"stepFamilies\":{\"http\":1},\"stepProviders\":{\"http.rest\":1},\"startupMs\":100,\"timeToFirstTestMs\":500}"

# Compute the idempotency key (SHA-256 of the body)
IDEMPOTENCY_KEY=$(echo -n "$BODY" | sha256sum | cut -d' ' -f1)

# POST to /v1/telemetry
curl -v \
  -X POST "$ENDPOINT/v1/telemetry" \
  -H "Content-Type: application/x-ndjson" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: $IDEMPOTENCY_KEY" \
  -d "$BODY"

# Expected response: 200 OK
```

**Verify the row was inserted:**

```bash
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT install_id, event_timestamp, schema_version FROM telemetry_event WHERE install_id = '550e8400-e29b-41d4-a716-446655440000';"
```

**Enqueue a forget request:**

```bash
curl -v \
  -X POST "$ENDPOINT/v1/telemetry/forget" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d "{\"installId\":\"$INSTALL_ID\"}"

# Expected response: 200 OK
```

**Verify the forget request was queued:**

```bash
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT install_id, requested_at, processed_at FROM forget_queue WHERE install_id = '550e8400-e29b-41d4-a716-446655440000';"
```

**Wait for the daily job or trigger it manually** (the job runs on a 24-hour interval by default):

```bash
# Option 1: Wait ~24 hours for the next job cycle
# Option 2: Manually restart the Container App to trigger the job immediately
az containerapp restart -g rg-vfx-telemetry-prod -n vfxtel-prod-app
```

**Verify the forget was processed:**

```bash
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT * FROM forget_queue WHERE install_id = '550e8400-e29b-41d4-a716-446655440000';"
# processed_at should now be a recent timestamp

psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT COUNT(*) FROM telemetry_event WHERE install_id = '550e8400-e29b-41d4-a716-446655440000';"
# Should return 0 (rows deleted)
```

### Inspect Partitions

```bash
# List all partitions and their row counts
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry << 'EOF'
SELECT
    schemaname,
    tablename,
    pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size,
    (SELECT COUNT(*) FROM telemetry_event WHERE tableoid = (schemaname||'.'||tablename)::regclass) AS rows
FROM pg_tables
WHERE schemaname = 'public' AND tablename LIKE 'telemetry_event_%'
ORDER BY tablename DESC;
EOF
```

### Check DEFAULT Partition for Clock-Skewed Data

**Non-empty DEFAULT partition is a signal that the daily job is not running:**

```bash
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry \
  -c "SELECT COUNT(*) FROM telemetry_event_default;"
```

If the count is >0 and not decreasing over time, the daily maintenance job is not executing or is failing. Check:

1. **Container App logs** for job errors
2. **Partition manager logs** for advisory lock acquisition failures
3. **minReplicas** setting (must be ≥1 for the job to run)

### Run Maintenance Manually

```bash
# Connect to Postgres as admin
psql -h vfxtel-prod-pg.postgres.database.azure.com -U postgres -d vfxtelemetry

-- Create partitions ahead
SELECT ensure_partitions(current_date, current_date + 7);

-- Drop old partitions (>90 days)
SELECT drop_old_partitions(90);

-- Sweep DEFAULT partition
SELECT sweep_default(90);

-- Process forget queue
-- (This requires application-level logic; run via the app or manually execute the forget transaction)

-- Purge old dedup records
DELETE FROM ingest_batch WHERE received_at < now() - make_interval(days => 35);
```

## Known Limitations & Mitigations

### Database Role Permissions

**Limitation:** The Container App's database connection runs as the `postgres` admin role, which has unrestricted DDL (create/drop partitions, alter tables). In production, this is a security concern.

**Mitigation (Production Delta):**
- Create a dedicated application role: `CREATE ROLE app_telemetry WITH LOGIN PASSWORD '...'`
- Grant only necessary permissions to that role (SELECT, INSERT, DELETE, UPDATE on data tables; execute permissions on partition functions)
- Use `SECURITY DEFINER` stored procedures for maintenance operations, owned by an admin role
- This prevents an SQL injection or compromised connection string from accidentally dropping critical infrastructure

The pilot uses the admin role for simplicity; harden this before production deployment.

### Forget Authorization

**Limitation:** The `/v1/telemetry/forget` endpoint is authorized only by the shared ingest token (no per-install proof required). Any token holder who knows an opaque install GUID can request deletion of that install's data.

**Threat model:** Low risk for the pilot (aggregate data, low sensitivity). An attacker would need to:
1. Guess a valid install GUID (UUID search space is huge)
2. Request deletion (best-effort, processed within ~24h)

**Mitigation (Production Delta):**
- Implement per-install proof-of-deletion (e.g. a per-install passphrase, or a signed token embedded in the engine's telemetry envelope)
- Require both the token and proof to authorize a forget request
- Rate-limit forget requests per IP to prevent abuse

### Network Posture (Pilot)

**Limitation:** PostgreSQL is deployed with "Allow Azure services" firewall rule and password authentication (no VNet egress or Entra MI auth).

**Production Delta (documented in containerApp.bicep):**
1. VNet subnet for Container Apps managed environment (egress stays on Azure backbone)
2. Private endpoint for PostgreSQL (no public IP)
3. User-Assigned Managed Identity for auth instead of password
4. Network Security Group rules to lock down PostgreSQL to the Container Apps subnet

## Troubleshooting

### Container App fails to start (readiness probe returns 503)

**Likely cause:** Database connection or bootstrap failed.

**Debug:**
```bash
az containerapp logs show -g rg-vfx-telemetry-prod -n vfxtel-prod-app --tail 200
```

Look for errors like:
- "No database connection string is configured"
- Connection refused / timeout (Postgres not reachable)
- SQL errors during bootstrap (schema conflicts, permission denied)

**Fix:**
1. Verify the connection string is correct and the database is running
2. Verify firewall rules allow Container Apps to reach Postgres
3. Check Postgres admin password is correct

### Ingest requests fail with 401 Unauthorized

**Likely cause:** Token is missing or not in the configured token list.

**Debug:**
```bash
curl -v -H "Authorization: Bearer wrong-token" https://.../v1/telemetry
```

**Fix:**
1. Verify the client is sending the correct token
2. Check GitHub secret `TELEMETRY_INGEST_TOKENS` is set and redeployed
3. Connect to the Container App and check the env var:
   ```bash
   az containerapp exec -g rg-vfx-telemetry-prod -n vfxtel-prod-app \
     -- env | grep VOUCHFX_TELEMETRY_INGEST_TOKENS
   ```

### Ingest requests fail with 429 Too Many Requests

**Likely cause:** Rate limit exceeded.

**Debug:**
- Check the `Retry-After` response header (default 60 seconds)
- Per-token limit: 120 requests per 60 seconds (configurable)
- Global limit: 1200 requests per 60 seconds across all tokens

**Fix:**
1. Client should back off and retry after `Retry-After` seconds
2. If sustained load exceeds the limits, increase `VOUCHFX_TELEMETRY_RATE_PERMITS` and `VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS` and redeploy

### Partitions are not being dropped (DEFAULT partition growing)

**Likely cause:** Maintenance job is not running or failing.

**Debug:**
```bash
# Check Container App logs for job errors
az containerapp logs show -g rg-vfx-telemetry-prod -n vfxtel-prod-app --tail 500 | grep -i "Maintenance\|DropOldPartitions\|Error"

# Check minReplicas
az containerapp show -g rg-vfx-telemetry-prod -n vfxtel-prod-app | jq '.properties.template.scale.minReplicas'
```

**Fix:**
1. Ensure `minReplicas: 1` in the Container App configuration
2. Check for PostgreSQL connection failures in the logs
3. Manually run `SELECT drop_old_partitions(90)` to verify the function works
4. Increase `VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS` if the job is running but you want it more frequent

## Monitoring & Alerting

**Recommended Log Analytics queries** (run in the Log Analytics workspace):

**Ingest success rate:**
```kusto
ContainerAppConsoleLogs_CL
| where Message contains "Ingested batch"
| summarize SuccessCount=count() by bin(TimeGenerated, 1h)
```

**Error rates:**
```kusto
ContainerAppConsoleLogs_CL
| where Level == "Error" or Level == "Warning"
| summarize ErrorCount=count() by Level, bin(TimeGenerated, 1h)
```

**Maintenance job status:**
```kusto
ContainerAppConsoleLogs_CL
| where Message contains "Maintenance" or Message contains "RetentionHostedService"
| project TimeGenerated, Message, Level
```

## Disaster Recovery

**Backup strategy:**
- Azure automatically maintains geo-redundant backups of PostgreSQL (7-day retention by default)
- Point-in-time restore (PITR) available within the backup window

**Restore procedure:**
1. In the Azure Portal, go to PostgreSQL → Backups
2. Select a point in time (within 7 days)
3. Click "Restore"
4. Provide a new server name (cannot restore over existing)
5. Update the Container App's `DB_CONNECTION_STRING` secret to point to the restored server

**PITR caveat (Privacy):**
A PITR restore includes forgotten data from before the PITR window. See `docs/privacy.md` for the residue mitigation.
