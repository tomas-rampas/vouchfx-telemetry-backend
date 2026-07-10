# Operator Handback — Deployment Runbook

**vouchfx Telemetry Backend** — ASP.NET Core 8 + PostgreSQL  
**Phase:** S12-G-01 (issue [vouchfx#152](https://github.com/tomas-rampas/vouchfx/issues/152)) Phase B  
**Status:** Engineering-complete. Deployment requires operator provisioning of Azure subscription and GitHub configuration.

This document provides the precise, step-by-step procedure for an operator to deploy this service to Azure and wire the engine to use it.

---

## 1. Azure Prerequisites

### 1.1 Resource Group

Create a resource group in your target subscription where all resources will be deployed.

```bash
az group create \
  --name <resource-group-name> \
  --location <azure-region>  # e.g. uksouth, eastus, northeurope
```

Record the resource group name — it will be used in GitHub Actions VARIABLES below.

### 1.2 Entra App Registration & OIDC Federated Credential

The workflow uses **Azure OIDC federated identity** — GitHub Actions obtains a short-lived token
without storing long-lived client secrets or certificates.

**Create an Entra App Registration:**

```bash
az ad app create --display-name vouchfx-telemetry-backend-deploy
```

Record the `appId` from the output — this becomes `AZURE_CLIENT_ID` (GitHub VARIABLE).

**Create a service principal:**

```bash
az ad sp create --id <appId>
```

**Create a federated credential** (scoped to this repository and the `main` branch):

The GitHub Actions OIDC subject format for GitHub-hosted runners is:

```
repo:<owner>/<repo>:ref:refs/heads/<branch>
```

For this repository:

```
repo:tomas-rampas/vouchfx-telemetry-backend:ref:refs/heads/main
```

Create the federated credential:

```bash
az ad app federated-credential create \
  --id <appId> \
  --parameters '{
      "name": "github-vouchfx-telemetry-backend-main",
      "issuer": "https://token.actions.githubusercontent.com",
      "subject": "repo:tomas-rampas/vouchfx-telemetry-backend:ref:refs/heads/main",
      "audiences": ["api://AzureADTokenExchange"]
    }'
```

**Note:** If you use a different branch or environment (dev vs. prod), create separate federated
credentials with the corresponding branch name. The `deploy.yml` workflow uses
`vars.DEPLOY_ENVIRONMENT` to select which federated credential is needed (the subject
must match the branch triggering the workflow).

### 1.3 Role Assignments

The service principal needs:

- **Contributor** on the resource group (to deploy all resources via Bicep)
- **AcrPush** on the ACR (if the operator performs `az acr build`; the current workflow uses ACR's
  server-side build, which requires Contributor on the RG)

Assign **Contributor** on the resource group:

```bash
az role assignment create \
  --assignee <appId> \
  --role "Contributor" \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>
```

Verify:

```bash
az role assignment list \
  --assignee <appId> \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>
```

### 1.4 Tenant ID & Subscription ID

Record:

- **Azure Tenant ID**: `az account show --query tenantId -o tsv`
- **Azure Subscription ID**: `az account show --query id -o tsv`

These become `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` (GitHub VARIABLES).

### 1.5 Azure Container Registry

Set up or use an existing Azure Container Registry (ACR) where the workflow will push the
container image.

```bash
az acr create \
  --resource-group <resource-group-name> \
  --name <registry-name> \
  --sku Basic  # or Standard/Premium depending on needs
```

Record the **login server FQDN** (e.g. `vfxtelregistry.azurecr.io`) — it becomes
`ACR_LOGIN_SERVER` (GitHub VARIABLE).

---

## 2. GitHub Configuration

All settings are on the repository's **Settings → Secrets and variables → Variables** and
**Settings → Secrets and variables → Secrets** pages. Below are the exact names and values.

### 2.1 GitHub VARIABLES (Settings → Variables)

Create the following **repository variables** (not secrets — these are non-sensitive):

| Variable | Value | Example |
|----------|-------|---------|
| `AZURE_CLIENT_ID` | Entra app registration ID | `00000000-0000-0000-0000-000000000000` |
| `AZURE_TENANT_ID` | Azure AD tenant ID | `00000000-0000-0000-0000-000000000001` |
| `AZURE_SUBSCRIPTION_ID` | Target subscription ID | `00000000-0000-0000-0000-000000000002` |
| `AZURE_RESOURCE_GROUP` | Resource group name (created in §1.1) | `vfx-telemetry-prod` |
| `ACR_LOGIN_SERVER` | ACR login server FQDN | `vfxtelregistry.azurecr.io` |
| `DEPLOY_ENVIRONMENT` | Environment selector (`dev` or `prod`) | `prod` |

**Verification:** In the workflow file (`.github/workflows/deploy.yml`), these variables are referenced:

- Line 83: `client-id: ${{ vars.AZURE_CLIENT_ID }}`
- Line 84: `tenant-id: ${{ vars.AZURE_TENANT_ID }}`
- Line 85: `subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}`
- Line 118: `ACR_SERVER: ${{ vars.ACR_LOGIN_SERVER }}`
- Line 147: `AZURE_RESOURCE_GROUP: ${{ vars.AZURE_RESOURCE_GROUP }}`
- Line 148: `DEPLOY_ENV: ${{ vars.DEPLOY_ENVIRONMENT || 'dev' }}`

### 2.2 GitHub SECRETS (Settings → Secrets)

Create the following **repository secrets**:

#### `TELEMETRY_INGEST_TOKENS`

**Format:** Comma-separated bearer tokens (no spaces).

The backend validates incoming `POST /v1/telemetry` requests against these tokens
via HTTP `Authorization: Bearer <token>` headers. Generate strong random values
(minimum 32 bytes, base64 or hex).

**Example:**

```
token-prod-001-abcdef123456,token-prod-002-xyz789klmn
```

The engine passes one of these tokens in the `VOUCHFX_TELEMETRY_TOKEN` environment variable.
Rotate these tokens periodically; the backend stores them in Key Vault and can be updated
without redeploying the service (the Container App re-reads the KV secret).

**Verification:** In `.github/workflows/deploy.yml`:
- Line 144: `VFX_INGEST_TOKENS: ${{ secrets.TELEMETRY_INGEST_TOKENS }}`
- In `containerApp.bicep` line 133–135, the secret is injected as `VOUCHFX_TELEMETRY_INGEST_TOKENS`

#### `DB_ADMIN_PASSWORD`

**Format:** Strong random password (minimum 16 characters, mix of upper, lower, digits, special chars).

PostgreSQL admin user is `vfxteladmin`. This password is used only during server provisioning
and should be stored in a secrets manager (not this GitHub secret, which is only for deployment).

**Verification:** In `.github/workflows/deploy.yml`:
- Line 145: `VFX_DB_ADMIN_PASSWORD: ${{ secrets.DB_ADMIN_PASSWORD }}`
- In `postgres.bicep` line 56, used for `administratorLoginPassword`

#### `DB_CONNECTION_STRING`

**Format:** Full Npgsql connection string.

Template:

```
Server=vfxtel-prod-pg.postgres.database.azure.com;Port=5432;User Id=vfxteladmin;Password=<DB_ADMIN_PASSWORD>;Database=telemetry;Ssl Mode=Require;
```

**Notes:**

- **Server FQDN:** After PostgreSQL is provisioned by Bicep, obtain the FQDN from the Azure portal
  or via `az postgres flexible-server show --resource-group <rg> --name vfxtel-prod-pg --query fullyQualifiedDomainName`
  (adjust the server name based on your `prefix` and `environmentSuffix` in the bicepparam file).
- **Port:** Always `5432` (default PostgreSQL port).
- **User ID:** `vfxteladmin` (hardcoded in `postgres.bicep` line 55).
- **Password:** Use the same value as `DB_ADMIN_PASSWORD` secret above.
- **Database:** `telemetry` (created by Bicep in `postgres.bicep` line 97).
- **SSL Mode:** `Require` (enforced by `require_secure_transport = ON` in `postgres.bicep` line 109).

**Chicken-and-egg problem:** The PostgreSQL server (and its FQDN) are provisioned *during* the
Bicep deployment. To get the connection string:

1. **First deployment:** Use a temporary placeholder value for `DB_CONNECTION_STRING` (e.g., a dummy
   string). The deployment will fail at the Container App step because the connection string is invalid,
   *but* PostgreSQL will be provisioned and the FQDN will be visible in the Azure portal or via the
   Bicep deployment output.
2. Update the `DB_CONNECTION_STRING` secret with the actual FQDN.
3. Re-run the workflow dispatch.

Alternatively, **manually construct** the connection string after the first Bicep deployment
completes partially (PostgreSQL module succeeds before Container App fails), then re-dispatch.

**Verification:** In `.github/workflows/deploy.yml`:
- Line 146: `VFX_DB_CONNECTION_STRING: ${{ secrets.DB_CONNECTION_STRING }}`
- In `containerApp.bicep` line 137–139, injected as `ConnectionStrings__Telemetry`
- Used by ASP.NET Core's built-in configuration to bind `IConfiguration["ConnectionStrings:Telemetry"]`

---

## 3. Deployment Dispatch Procedure

### 3.1 Manual Deployment (workflow_dispatch)

The workflow is triggered by `workflow_dispatch` (manual trigger) or on a GitHub release publication.

**To deploy manually:**

1. Go to the repository → **Actions** → **Deploy** workflow
2. Click **Run workflow**
3. Select the branch (should be `main`)
4. Confirm that all GitHub VARIABLES and SECRETS (§2) are populated
5. Click **Run workflow**

**Monitor the workflow:**

- The job is named `Build image and deploy to Azure` (`.github/workflows/deploy.yml` line 60)
- Steps:
  1. **Checkout** — Clone the repository
  2. **Setup .NET** — Install .NET SDK (version from `global.json`)
  3. **Azure OIDC login** — Obtain an OIDC token; verify AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID
  4. **Compute image tag** — Generate `sha-<short>` for manual dispatch or `v*.*.*` for releases
  5. **Build and push image to ACR** — Run `az acr build` (server-side build)
  6. **Deploy Bicep IaC** — Run `az deployment group create` with the parameter file

Expected output:

```
Deployment Mode       : Incremental
Deployment Name       : <timestamp-guid>
Timestamp             : <timestamp>
Status                : Succeeded
```

### 3.2 Release-Based Deployment

When a GitHub release is published (`release: types: [published]`, line 52 in `deploy.yml`),
the workflow triggers automatically with the release tag as the image tag.

**To trigger a release deployment:**

1. Create a release on GitHub (go to **Releases** → **Draft a new release**)
2. Set the tag to a semantic version (e.g. `v1.0.0`)
3. Publish the release
4. The workflow will trigger and deploy image tag `v1.0.0`

---

## 4. Post-Deployment Verification

After the workflow completes successfully:

### 4.1 Service Health

Retrieve the Container App FQDN from the Bicep deployment outputs:

```bash
az deployment group show \
  --resource-group <AZURE_RESOURCE_GROUP> \
  --name deploy-container-app \
  --query properties.outputs.containerAppFqdn.value -o tsv
```

Or check the Azure portal: **Container Apps** → `vfxtel-prod-app` (adjust suffix) → **Application URL**.

**Test the health endpoints:**

```bash
# Liveness check (returns 200 if the app is alive)
curl https://<app-fqdn>/healthz

# Readiness check (returns 200 if ready, 503 if dependencies are down)
curl https://<app-fqdn>/readyz
```

Both should return HTTP 200 with a response body like `{"status":"Healthy"}`.

### 4.2 Ingest a Test Batch

**Test the ingest endpoint** with a minimal NDJSON telemetry batch:

```bash
# Create a test batch (one TelemetryEvent line)
cat > /tmp/test-batch.ndjson <<'EOF'
{"schemaVersion":1,"installId":"00000000-0000-0000-0000-000000000000","eventTimestamp":"2026-07-10T12:00:00Z","toolVersion":"1.0.0","engineVersion":"1.0.0","dotnetVersion":"8.0.0","runCount":1,"scenarioCount":1,"stepPass":1,"stepFail":0,"stepEnvError":0,"stepInconclusive":0,"scenarioPass":1,"scenarioFail":0,"scenarioEnvError":0,"scenarioInconclusive":0,"stepFamilies":{},"stepProviders":{},"startupMs":100,"timeToFirstTestMs":500}
EOF

# Ingest the batch
curl -X POST \
  https://<app-fqdn>/v1/telemetry \
  -H "Authorization: Bearer <one-of-your-TELEMETRY_INGEST_TOKENS>" \
  -H "Content-Type: application/x-ndjson" \
  --data-binary @/tmp/test-batch.ndjson
```

Expected response:

```
HTTP 200
{}
```

### 4.3 Database Connectivity & Schema

Verify the PostgreSQL database and schema are set up:

```bash
# Connect to the database
psql --host=<postgres-server-fqdn> \
     --port=5432 \
     --username=vfxteladmin \
     --dbname=telemetry

# In the psql prompt, check the schema
\dt                          # List tables
SELECT * FROM telemetry_event LIMIT 1;  # Should return your test event
```

If the schema is not present, the bootstrap SQL must be run manually. The Bicep deployment
does **not** automatically execute `deploy/sql/bootstrap.sql`; that is a manual step:

```bash
psql --host=<postgres-server-fqdn> \
     --port=5432 \
     --username=vfxteladmin \
     --dbname=telemetry \
     -f deploy/sql/bootstrap.sql
```

### 4.4 Key Vault & Secrets

Verify secrets are stored in Key Vault:

```bash
az keyvault secret list --vault-name <keyvault-name>
```

The KV name is `vfxtel-prod-kv` (adjust the suffix). Expected secrets:

- `ingest-tokens` (source: `TELEMETRY_INGEST_TOKENS`)
- `db-connection` (source: `DB_CONNECTION_STRING`)

### 4.5 Logs

Logs are sent to **Azure Monitor / Log Analytics**:

1. Go to **Log Analytics workspaces** in the Azure portal
2. Find the workspace named `vfxtel-prod-logs` (or your `prefix-environment` value)
3. Run a Kusto query to check application logs:

```kusto
ContainerAppConsoleLogs
| where ContainerAppName == "vfxtel-prod-app"
| order by TimeGenerated desc
| limit 100
```

Check for errors during startup (database connection issues, secret retrieval, schema bootstrap).

---

## 5. Engine-Side Wiring

Once the backend is live and verified, wire the **vouchfx engine** to send telemetry to it.

### 5.1 Environment Variables

On the machine or container running vouchfx, set:

```bash
# The HTTPS endpoint of the deployed backend (without trailing slash)
export VOUCHFX_TELEMETRY_ENDPOINT=https://<app-fqdn>/v1/telemetry

# One of the TELEMETRY_INGEST_TOKENS you generated in §2.2
export VOUCHFX_TELEMETRY_TOKEN=<bearer-token>
```

### 5.2 Opt-In Gate

Telemetry is **opt-in by default** — the engine will not send telemetry unless the user
explicitly consents. Users enable telemetry via:

```bash
vouchfx telemetry enable
```

This command persists the user's preference in the vouchfx configuration (persisted locally;
no network traffic to the backend).

To check the current telemetry status:

```bash
vouchfx telemetry status
```

To disable:

```bash
vouchfx telemetry disable
```

**Documentation reference:** The engine repository's `docs/telemetry.md` documents the user-facing
telemetry feature, consent model, and configuration. When this backend goes live, that document
should be updated to reflect the hosted pilot endpoint (currently a placeholder). This update is
out of scope for this handback; coordinate with the engine maintainer.

### 5.3 Batch Contents

The engine sends telemetry batches as NDJSON (one `TelemetryEvent` per line) to
`POST /v1/telemetry` with Bearer authentication. The exact schema is defined in
[`docs/wire-contract.md`](../docs/wire-contract.md).

Example batch (two events):

```
{"schemaVersion":1,"installId":"<uuid>","eventTimestamp":"2026-07-10T12:00:00Z",...}
{"schemaVersion":1,"installId":"<uuid>","eventTimestamp":"2026-07-10T12:30:00Z",...}
```

---

## 6. Deferred Production Hardening

The Bicep templates document several hardening measures that are **deferred for the pilot**
but recommended for production. They are listed here so operators can prioritise them
post-launch.

### 6.1 PostgreSQL

From `deploy/modules/postgres.bicep` lines 8–18:

1. **VNet integration + private endpoint:** Disable public network access and create a
   private endpoint in the same VNet as the Container Apps environment. This keeps
   database traffic on the Azure backbone.

2. **Entra managed-identity authentication:** Enable
   `authConfig.activeDirectoryAuthEnabled = true` and disable password auth.
   The Container App's UAMI can authenticate to the database without a password.

3. **Zone-redundant HA:** Set `highAvailability.mode = 'ZoneRedundant'` for automatic
   failover (requires GeneralPurpose or MemoryOptimized SKU, unavailable on Burstable).

4. **Customer-managed encryption keys (BYOK):** Set `dataEncryption.type = 'AzureKeyVault'`
   to use a customer-managed key in Key Vault instead of Microsoft-managed keys.

5. **Geo-redundant backups:** Set `backup.geoRedundantBackup = 'Enabled'` for cross-region
   data protection (currently disabled for pilot cost).

### 6.2 Key Vault

From `deploy/modules/keyVault.bicep` lines 9–16:

1. **Private endpoint + VNet integration:** Disable `publicNetworkAccess` and create
   a private endpoint so Key Vault is unreachable from the internet.

2. **Purge protection:** Set `enablePurgeProtection = true` to prevent accidental
   permanent deletion of secrets.

3. **Firewall:** Set `networkAcls.defaultAction = 'Deny'` and allow only the Container Apps
   outbound CIDR range via IP rules.

### 6.3 Container App

From `deploy/modules/containerApp.bicep` lines 22–28:

1. **VNet egress:** Wire the managed environment to a VNet subnet so all outbound traffic
   (to PostgreSQL, Key Vault) stays on the Azure backbone instead of routing through the
   public internet.

2. **ACR pull via managed identity:** Grant the UAMI `AcrPull` role on the ACR and set
   `registries[].identity = uamiId` instead of using ACR admin credentials.

3. **Dedicated workload profile:** Use a Dedicated workload profile (e.g. D4) for predictable
   CPU/memory under sustained ingest load instead of Consumption profile.

---

## Troubleshooting

### Deployment fails: "OptionsValidationException — CliPath is required"

**Cause:** The Bicep deployment is running in a context without the Aspire AppHost SDK metadata.
This is a **known limitation** of headless Container Apps deployments — not applicable to this
service, which is pure ASP.NET Core minimal API.

**Resolution:** No action needed; the error occurs only in Aspire-based applications.

### Service returns 503 on `/readyz`

**Cause:** The readiness probe checks database connectivity and schema presence. Possible reasons:

- PostgreSQL is not fully provisioned yet (wait 5–10 minutes after first deployment)
- The `bootstrap.sql` schema has not been applied (see §4.3)
- The connection string secret in Key Vault is invalid (check §2.2)

**Resolution:**

1. Check logs in Log Analytics (§4.5)
2. Verify the connection string manually (§4.3)
3. Run bootstrap SQL if needed (§4.3)

### Ingest request returns 401 Unauthorized

**Cause:** The Bearer token in the `Authorization` header does not match any token in
`TELEMETRY_INGEST_TOKENS`.

**Resolution:**

- Check that the `Authorization: Bearer <token>` header is correct
- Verify that the token is in the `TELEMETRY_INGEST_TOKENS` secret (comma-separated list)
- If rotating tokens, update the secret and wait 1–2 minutes for the Container App to re-read
  Key Vault

### PostgreSQL firewall error: "FATAL: no pg_hba.conf entry"

**Cause:** The firewall rule was not created or does not include the Container App outbound IP.

**Resolution:**

- The Bicep template includes `AllowAzureServices` firewall rule (0.0.0.0/0.0.0.0 magic rule),
  which should cover all Azure services
- If the issue persists, add an explicit IP rule for the Container App's outbound IP (obtain
  from Azure portal → Container Apps → Environment → Outbound addresses)

---

## Summary Checklist

- [ ] Resource group created (§1.1)
- [ ] Entra App Registration created (§1.2)
- [ ] Federated credential created for `main` branch (§1.2)
- [ ] Contributor role assigned to service principal (§1.3)
- [ ] Tenant ID, Subscription ID recorded (§1.4)
- [ ] Azure Container Registry set up (§1.5)
- [ ] GitHub VARIABLES populated (§2.1)
- [ ] GitHub SECRETS populated (§2.2)
- [ ] Workflow dispatched successfully (§3.1)
- [ ] `/healthz` and `/readyz` endpoints returning 200 (§4.1)
- [ ] Test ingest batch successful (§4.2)
- [ ] PostgreSQL schema verified (§4.3)
- [ ] Key Vault secrets present (§4.4)
- [ ] Logs visible in Log Analytics (§4.5)
- [ ] Engine environment variables set (§5.1)
- [ ] Engine telemetry gate enabled (`vouchfx telemetry enable`) (§5.2)
- [ ] First telemetry batch ingested and visible in database (§4.3)
