# Deploying the vouchfx Telemetry Backend to Azure

This folder contains the **Bicep infrastructure-as-code** that deploys the vouchfx telemetry
backend (ASP.NET Core 8 + PostgreSQL) to Azure, and the parameter files consumed by the
CI/CD pipeline. This README is the step-by-step guide; the exhaustive operator runbook
(verification, troubleshooting, hardening checklist) is [`OPERATOR-HANDBACK.md`](OPERATOR-HANDBACK.md).

## What gets deployed

A single `az deployment group create` against [`main.bicep`](main.bicep) provisions:

| Resource | Name pattern (default) | Module |
|---|---|---|
| User-Assigned Managed Identity (UAMI) | `vfxtel-<env>-id` | inline in `main.bicep` |
| Log Analytics workspace | `vfxtel-<env>-logs` | [`modules/logAnalytics.bicep`](modules/logAnalytics.bicep) |
| Container Apps managed environment | `vfxtel-<env>-cae` | [`modules/environment.bicep`](modules/environment.bicep) |
| PostgreSQL Flexible Server 16 + `telemetry` DB | `vfxtel-<env>-pg` | [`modules/postgres.bicep`](modules/postgres.bicep) |
| Key Vault (RBAC mode, holds runtime secrets) | `vfxtel-<env>-kv` | [`modules/keyVault.bicep`](modules/keyVault.bicep) |
| Container App (the backend service) | `vfxtel-<env>-app` | [`modules/containerApp.bicep`](modules/containerApp.bicep) |

`<env>` is `dev` or `prod`, selected by the parameter file. See
[`modules/README.md`](modules/README.md) for the module reference and deployment ordering.

**Secrets flow:** the ingest tokens, the PostgreSQL admin password and the Npgsql connection
string are `@secure()` deploy-time parameters, read from `VFX_*` environment variables by the
`.bicepparam` files (`readEnvironmentVariable()`). The ingest tokens and the connection string
are stored encrypted in Key Vault and surfaced to the Container App via Key Vault-backed
secret references resolved with the UAMI; the admin password is used **only to provision** the
PostgreSQL server and is not stored anywhere in Azure (keep it in your own secrets manager).
None of the three is ever committed, logged, or exposed through a Bicep output.

## Folder contents

```
deploy/
├── main.bicep              # Orchestrator (targetScope: resourceGroup)
├── modules/                # One module per Azure resource (see modules/README.md)
├── parameters/
│   ├── dev.bicepparam      # Non-secret dev parameters (Burstable B1ms, 30-day retention)
│   └── prod.bicepparam     # Non-secret prod parameters (GP D2s_v3, 90-day retention)
├── sql/bootstrap.sql       # DB schema (applied automatically at service startup)
└── OPERATOR-HANDBACK.md    # Full operator runbook: verification, troubleshooting, hardening
```

## Prerequisites

- An Azure subscription and the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`)
- Owner or User Access Administrator rights on the target resource group (the template
  creates an RBAC role assignment for Key Vault)
- Permission to create an Entra App Registration (for the CI/CD path)
- Repository admin rights on GitHub (to set Actions variables/secrets)

---

## Step 1 — Provision the Azure prerequisites (one-off)

These resources are **not** created by Bicep and must exist before the first deployment.

### 1.1 Resource group

```bash
az group create --name <resource-group-name> --location <region>   # e.g. uksouth
```

### 1.2 Azure Container Registry

The pipeline builds the image server-side with `az acr build` and the Container App pulls it
from here.

```bash
az acr create --resource-group <resource-group-name> --name <registry-name> --sku Basic
```

Record the login server FQDN (e.g. `vfxtelregistry.azurecr.io`).

### 1.3 Entra App Registration with OIDC federated credentials (CI/CD path only)

GitHub Actions authenticates to Azure with **OIDC federated identity** — no long-lived
client secret is ever stored in GitHub.

```bash
# App registration + service principal
az ad app create --display-name vouchfx-telemetry-backend-deploy   # record the appId
az ad sp create --id <appId>

# Federated credential — the subject is ENVIRONMENT-based because the deploy job
# binds to a GitHub Environment (deploy.yml: environment: ${{ vars.DEPLOY_ENVIRONMENT || 'dev' }})
az ad app federated-credential create --id <appId> --parameters '{
    "name": "github-vouchfx-telemetry-backend-dev",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:tomas-rampas/vouchfx-telemetry-backend:environment:dev",
    "audiences": ["api://AzureADTokenExchange"]
  }'
# Repeat with :environment:prod for production.

# Contributor on the resource group (deploys all resources, including az acr build)
az role assignment create --assignee <appId> --role "Contributor" \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>
```

> The federated credential subject **must match** `vars.DEPLOY_ENVIRONMENT` exactly,
> otherwise the Azure login step fails with `AADSTS700213`.

Record the tenant and subscription IDs:

```bash
az account show --query tenantId -o tsv
az account show --query id -o tsv
```

---

## Step 2 — Configure the GitHub repository

Under **Settings → Secrets and variables → Actions** create:

### Variables (non-sensitive)

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `appId` from step 1.3 |
| `AZURE_TENANT_ID` | Tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `AZURE_RESOURCE_GROUP` | Resource group name from step 1.1 |
| `ACR_LOGIN_SERVER` | ACR FQDN from step 1.2, e.g. `vfxtelregistry.azurecr.io` |
| `DEPLOY_ENVIRONMENT` | `dev` or `prod` — selects `parameters/<env>.bicepparam` **and** the GitHub Environment |

### Secrets

| Secret | Value |
|---|---|
| `TELEMETRY_INGEST_TOKENS` | Comma-separated bearer tokens (generate ≥32 random bytes each). These are the tokens vouchfx clients present on `Authorization: Bearer` |
| `DB_ADMIN_PASSWORD` | Strong random password for the PostgreSQL admin user `vfxteladmin` |
| `DB_CONNECTION_STRING` | Full Npgsql connection string — see the bootstrapping note below |

**Connection-string template** (the server FQDN only exists after the first deployment —
see step 3):

```
Server=vfxtel-<env>-pg.postgres.database.azure.com;Port=5432;User Id=vfxteladmin;Password=<DB_ADMIN_PASSWORD>;Database=telemetry;Ssl Mode=Require;
```

---

## Step 3 — Deploy

### Option A — via the CI/CD pipeline (recommended)

The [`Deploy` workflow](../.github/workflows/deploy.yml) builds the container image in ACR
and runs the Bicep deployment. It triggers on:

- **Manual dispatch** — repository → **Actions** → **Deploy** → **Run workflow** (branch
  `main`). The image is tagged `sha-<short-commit>`.
- **Release publication** — publishing a GitHub release whose tag starts with `v` and
  matches the workflow's allowlist `^v[0-9A-Za-z.+-]+$` (semver-style, e.g. `v1.2.3` or
  `v1.2.3-rc.1`) deploys that tag automatically; anything else fails the tag-validation step.

Pipeline steps: checkout → .NET setup → **Azure OIDC login** → compute image tag →
`az acr build` (server-side, pushes `vouchfx-telemetry-backend:<tag>` and `:latest`) →
`az deployment group create --template-file deploy/main.bicep --parameters @deploy/parameters/<env>.bicepparam`.

**First-deployment bootstrapping (chicken-and-egg):** the PostgreSQL FQDN needed for
`DB_CONNECTION_STRING` is only known once Bicep has provisioned the server. On the very
first run, set `DB_CONNECTION_STRING` to a placeholder; the run will fail at the Container
App step but PostgreSQL will be created. Then fetch the FQDN, fix the secret, and re-run:

```bash
az postgres flexible-server show --resource-group <rg> --name vfxtel-<env>-pg \
  --query fullyQualifiedDomainName -o tsv
```

### Option B — manually from your workstation

Useful for a first smoke deployment or when iterating on the templates. The `.bicepparam`
files read the secrets from `VFX_*` environment variables, so export them first:

```bash
az login
az account set --subscription <subscription-id>

# 1. Build and push the image (server-side build — no local Docker needed)
az acr build --registry <registry-name> \
  --image vouchfx-telemetry-backend:manual-$(git rev-parse --short HEAD) \
  --file Dockerfile .

# 2. Export the deploy-time parameters (missing vars fail the Bicep compile — fail-secure)
export VFX_ACR_LOGIN_SERVER='<registry-name>.azurecr.io'
export VFX_IMAGE_TAG='manual-<short-sha>'
export VFX_INGEST_TOKENS='<token1>,<token2>'
export VFX_DB_ADMIN_PASSWORD='<strong-password>'
export VFX_DB_CONNECTION_STRING='Server=...;Port=5432;User Id=vfxteladmin;Password=...;Database=telemetry;Ssl Mode=Require;'

# 3. Deploy (run from the deploy/ folder)
az deployment group create \
  --resource-group <resource-group-name> \
  --template-file main.bicep \
  --parameters @parameters/dev.bicepparam
```

To preview changes without applying them, substitute `az deployment group what-if` for
`create` in step 3.

---

## Step 4 — Verify

```bash
# Container App FQDN (also a Bicep deployment output: containerAppFqdn)
az containerapp show --resource-group <rg> --name vfxtel-<env>-app \
  --query properties.configuration.ingress.fqdn -o tsv

curl -i https://<app-fqdn>/healthz   # 200 = alive
curl -i https://<app-fqdn>/readyz    # 200 = DB reachable; 503 = see OPERATOR-HANDBACK.md
```

The database schema is bootstrapped automatically at service startup (idempotent, embedded
`sql/bootstrap.sql`, guarded by an advisory lock). For a full smoke test — ingesting a
sample NDJSON batch with the required `Idempotency-Key` header — follow
[`OPERATOR-HANDBACK.md` §4](OPERATOR-HANDBACK.md#4-post-deployment-verification).

---

## Step 5 — Connect the backend to the vouchfx CI/CD pipeline

The backend is the server side of the frozen `/v1/telemetry` wire contract
([`docs/wire-contract.md`](../docs/wire-contract.md)). The client side is the
**vouchfx engine** ([tomas-rampas/vouchfx](https://github.com/tomas-rampas/vouchfx)), which
drains its local telemetry outbox to this backend after each test run — locally or from any
CI pipeline that runs vouchfx suites.

### 5.1 Local (developer machine): two variables + explicit consent

```bash
# BASE URL only — the engine appends /v1/telemetry (and /v1/telemetry/forget) itself
export VOUCHFX_TELEMETRY_ENDPOINT=https://<app-fqdn>

# One of the tokens from the TELEMETRY_INGEST_TOKENS secret (step 2)
export VOUCHFX_TELEMETRY_TOKEN=<bearer-token>

vouchfx telemetry enable    # persists consent + mints the anonymous install ID
vouchfx telemetry status    # inspect the current state
```

> **Do not** include the `/v1/telemetry` path in the endpoint — the engine's HTTP transport
> resolves it relative to the base URL, so a full path would double it.

### 5.2 CI: environment-configured telemetry (three variables, no enable step)

CI runners are ephemeral, so the local consent flow would mint a **fresh install ID on
every job** — each run would show up in this backend as a new install. Instead, CI uses
*environment-configured telemetry*: a third variable supplies a **stable per-repo install
ID**, and providing all three variables *is* the opt-in — no `telemetry enable` step at all.

One-time setup per consumer repository (Settings → Secrets and variables → Actions):

| Name | Kind | Value |
|---|---|---|
| `VOUCHFX_TELEMETRY_ENDPOINT` | variable | `https://<app-fqdn>` |
| `VOUCHFX_TELEMETRY_INSTALL_ID` | variable | any GUID, generated once (`uuidgen`) |
| `VOUCHFX_TELEMETRY_TOKEN` | **secret** | one of the `TELEMETRY_INGEST_TOKENS` values |

Any of the three missing → the engine stays inert. `VOUCHFX_NO_TELEMETRY=1` always forces
telemetry off, in CI and locally alike.

### 5.3 Wiring a GitHub Actions job that runs vouchfx

Via the reusable suite-runner workflow (which plumbs the three values through for you):

```yaml
jobs:
  e2e:
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@<commit-sha>
    with:
      scenario-path: ./tests/e2e
      telemetry-endpoint: ${{ vars.VOUCHFX_TELEMETRY_ENDPOINT }}
      telemetry-install-id: ${{ vars.VOUCHFX_TELEMETRY_INSTALL_ID }}
    secrets:
      telemetry-token: ${{ secrets.VOUCHFX_TELEMETRY_TOKEN }}
```

Or in a job that invokes the vouchfx CLI directly, simply set the same three values as job
`env:` — the engine reads them itself:

```yaml
    env:
      VOUCHFX_TELEMETRY_ENDPOINT: ${{ vars.VOUCHFX_TELEMETRY_ENDPOINT }}
      VOUCHFX_TELEMETRY_INSTALL_ID: ${{ vars.VOUCHFX_TELEMETRY_INSTALL_ID }}
      VOUCHFX_TELEMETRY_TOKEN: ${{ secrets.VOUCHFX_TELEMETRY_TOKEN }}
```

(GitLab consumers set the same three names as project CI/CD variables — the token masked —
and the engine picks them up directly; the GitLab template needs no extra configuration.)

Notes:

- Token rotation: update the `TELEMETRY_INGEST_TOKENS` secret in **this** repository,
  re-run the Deploy workflow (the Container App restarts and re-reads Key Vault), then
  update the consumer-side `VOUCHFX_TELEMETRY_TOKEN` secret.
- Because every run of a repo reuses the same install ID, this backend's per-install
  aggregation reflects real adoption instead of one phantom install per CI job.

The full engine-side reference (outbox caps, forget flow, batch format) is in the engine
repository's `docs/telemetry.md` and in
[`OPERATOR-HANDBACK.md` §5](OPERATOR-HANDBACK.md#5-engine-side-wiring).

---

## Production hardening

The pilot configuration deliberately defers several hardening measures (VNet integration,
private endpoints, Entra DB auth, zone-redundant HA, KV purge protection). Each module's
header documents its own "hardened production delta"; the consolidated checklist is in
[`OPERATOR-HANDBACK.md` §6](OPERATOR-HANDBACK.md#6-deferred-production-hardening).
