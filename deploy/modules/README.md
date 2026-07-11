# Bicep modules — reference

One module per Azure resource, orchestrated by [`../main.bicep`](../main.bicep). Deployment
instructions live in [`../README.md`](../README.md); this file documents what each module
provisions and how they chain together.

## Deployment order

Ordering is implicit — Bicep derives it from output-to-input dependencies:

```
uami (inline resource in main.bicep)
  └─→ keyVault       (needs uami.properties.principalId for the RBAC grant)
logAnalytics
  └─→ environment    (needs workspaceId + customerId)
postgres             (independent — the connection string arrives as a @secure() param)
containerApp         (deploys last: needs uami.id, keyVault's secret URIs,
                      and environment's environmentId)
```

The **User-Assigned Managed Identity** is deliberately an inline resource in `main.bicep`
rather than a module: a system-assigned identity would create a circular dependency (the
Container App references KV secret URIs, which require the RBAC grant, which requires the
app's principal ID). Provisioning the UAMI first breaks the cycle in a single deployment.

## Modules

### `logAnalytics.bicep`

Log Analytics workspace (`PerGB2018`) receiving Container Apps console/system logs.

| | |
|---|---|
| Inputs | `location`, `workspaceName`, `retentionInDays` (default 30) |
| Outputs | `workspaceId` (full ARM ID), `customerId` (GUID) |

Only the non-sensitive IDs are output. The workspace **shared key** is never passed between
modules — `environment.bicep` retrieves it itself via `listKeys()` so it can't leak through
deployment history.

### `environment.bicep`

Container Apps managed environment wired to the Log Analytics workspace.

| | |
|---|---|
| Inputs | `location`, `environmentName`, `logAnalyticsWorkspaceId`, `logAnalyticsCustomerId` |
| Outputs | `environmentId` |

### `postgres.bicep`

Azure Database for PostgreSQL **Flexible Server 16** with the `telemetry` database,
`require_secure_transport = ON`, and the `AllowAzureServices` firewall rule
(0.0.0.0 magic rule). Admin login is `vfxteladmin`.

| | |
|---|---|
| Inputs | `location`, `serverName`, `adminPassword` (`@secure()`), `skuTier`, `skuName`, `storageSizeGb`, `backupRetentionDays` (default 7) |
| Outputs | `serverFqdn` |

The admin password is a `@secure()` parameter and is never echoed as an output. The full
connection string is **not** built here — it is supplied to the Container App as a separate
`@secure()` deploy-time parameter (see the first-deployment bootstrapping note in
[`../README.md`](../README.md)).

### `keyVault.bicep`

Key Vault in **RBAC mode** (`enableRbacAuthorization: true` — no classic access policies)
storing the two runtime secrets and granting the UAMI the built-in **Key Vault Secrets
User** role.

| | |
|---|---|
| Inputs | `location`, `keyVaultName`, `uamiPrincipalId`, `ingestTokens` (`@secure()`), `dbConnectionString` (`@secure()`) |
| Outputs | `keyVaultUri`, `ingestTokensSecretUri`, `dbConnectionSecretUri` |
| Secrets created | `ingest-tokens`, `db-connection` |

Only the secret **URIs** are output (needed by `containerApp.bicep` for KV-backed secret
references) — never the values.

### `containerApp.bicep`

The backend service itself: external HTTPS ingress, Key Vault-backed secrets resolved with
the UAMI, and all non-secret tunables (`maxBodyBytes`, `maxBatchLines`, rate limiting,
retention/partition settings) injected as container environment variables.

| | |
|---|---|
| Inputs | `location`, `containerAppName`, `caEnvironmentId`, `acrLoginServer`, `imageTag`, `uamiId`, `kvIngestTokensSecretUri`, `kvDbConnectionSecretUri`, `maxReplicas` + tunables |
| Outputs | `fqdn` (the ingress hostname used for `VOUCHFX_TELEMETRY_ENDPOINT`) |

**`minReplicas` is pinned to 1** and must stay that way: the retention/partition-maintenance
job runs as an in-process `IHostedService` timer, so scale-to-zero would silently stop
partition rotation.

## Conventions

- **No secret ever crosses a module boundary as an output.** Secrets travel only as
  `@secure()` parameters downward; anything sensitive needed inside a module
  (e.g. the Log Analytics shared key) is fetched in-module via ARM functions.
- **Pilot vs production:** each module header documents its own *"hardened production
  delta"* (VNet integration, private endpoints, Entra DB auth, zone-redundant HA, purge
  protection, …). The consolidated list is
  [`../OPERATOR-HANDBACK.md` §6](../OPERATOR-HANDBACK.md#6-deferred-production-hardening).
- **Naming:** resource names derive from `prefix` + `environmentSuffix` in `main.bicep`
  (`vfxtel-dev-pg`, `vfxtel-prod-app`, …); modules receive the final name as a parameter and
  never compose names themselves.
