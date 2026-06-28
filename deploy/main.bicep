// ────────────────────────────────────────────────────────────────────────────────
// deploy/main.bicep
// vouchfx Telemetry Backend — Azure Resource Deployment Orchestrator
// S12-G-01 / Issue #152 Phase B
//
// targetScope: resourceGroup  (az deployment group create --resource-group ...)
//
// ── Identity strategy — User-Assigned Managed Identity (UAMI) ────────────────
//   Bicep cannot resolve the circular dependency that arises with a
//   system-assigned identity and Key Vault-backed Container App secrets:
//     - Container App's principal ID is only known after the CA is created.
//     - Container App definition references KV secret URIs, which require
//       the KV RBAC grant to already be in place.
//   UAMI breaks the cycle: the identity is provisioned first (inline resource),
//   granted KV Secrets User access, then referenced by the Container App —
//   all within a single Bicep deployment, no multi-phase scripts required.
//   Reference: https://learn.microsoft.com/azure/container-apps/managed-identity
//
// ── Module deployment order (implicit via output-to-input dependencies) ───────
//   uami (inline resource)
//     → keyVault     (needs uami.properties.principalId for RBAC)
//     → containerApp (needs uami.id and KV secret URIs)
//   logAnalytics
//     → caEnvironment
//   postgres        (independent; connection string passed as secure param)
//
// ── Secrets handling ─────────────────────────────────────────────────────────
//   Secrets (ingestTokens, dbAdminPassword, dbConnectionString) are passed as
//   @secure() parameters at deploy time via GitHub Actions secrets.  They are
//   NEVER committed to parameter files, logged by Azure, or written to any
//   output.  They are stored encrypted in Key Vault by keyVault.bicep and
//   referenced at runtime via the Container App's KV-backed secret store.
// ────────────────────────────────────────────────────────────────────────────────

targetScope = 'resourceGroup'

// ── Location ──────────────────────────────────────────────────────────────────
@description('Azure region for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

// ── Naming ────────────────────────────────────────────────────────────────────
@description('Short prefix used in all resource names (max 6 chars, lowercase alphanumeric).')
@maxLength(6)
param prefix string = 'vfxtel'

@description('Environment suffix appended to resource names.')
@allowed(['dev', 'prod'])
param environmentSuffix string = 'dev'

// ── Container image ───────────────────────────────────────────────────────────
@description('ACR login server FQDN, e.g. vfxtelregistry.azurecr.io')
param acrLoginServer string

@description('Container image tag to deploy (e.g. sha-abc1234 or v1.2.3).')
param imageTag string

// ── Scale ─────────────────────────────────────────────────────────────────────
@description('Maximum number of Container App replicas. Minimum is always 1 (in-process timer requirement).')
@minValue(1)
param maxReplicas int = 3

// ── PostgreSQL SKU (parameterised for dev vs prod) ────────────────────────────
@description('PostgreSQL Flexible Server compute tier.')
@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresTier string = 'Burstable'

@description('PostgreSQL Flexible Server SKU name within the tier.')
param postgresSkuName string = 'Standard_B1ms'

@description('PostgreSQL Flexible Server storage size in GB.')
param postgresStorageSizeGb int = 32

// ── Tunable environment variables (non-secret) ────────────────────────────────
@description('Maximum ingest request body size in bytes (default 2 MiB).')
param maxBodyBytes int = 2097152

@description('Maximum number of NDJSON lines per ingest batch.')
param maxBatchLines int = 500

@description('Rate-limit: permitted requests per window.')
param ratePermits int = 120

@description('Rate-limit: sliding window size in seconds.')
param rateWindowSeconds int = 60

@description('Telemetry event retention in days (partition drop threshold).')
param retentionDays int = 90

@description('Number of future day-partitions to pre-create on startup.')
param precreateDays int = 7

@description('Ingest-batch idempotency key retention in days.')
param dedupRetentionDays int = 35

@description('Maintenance job run interval in hours.')
param jobIntervalHours int = 24

// ── Secrets (secure deploy-time parameters — NEVER in parameter files) ────────
@description('Comma-separated bearer ingest tokens. Injected at deploy time from GitHub Actions secrets; stored in Key Vault by keyVault.bicep.')
@secure()
param ingestTokens string

@description('PostgreSQL flexible server admin password. Injected at deploy time from GitHub Actions secrets.')
@secure()
param dbAdminPassword string

@description('Full Npgsql connection string to the telemetry database. Injected at deploy time from GitHub Actions secrets.')
@secure()
param dbConnectionString string

// ── Derived resource names ────────────────────────────────────────────────────
var env               = environmentSuffix
var logWorkspaceName  = '${prefix}-${env}-logs'
var caEnvironmentName = '${prefix}-${env}-cae'
var postgresName      = '${prefix}-${env}-pg'
var keyVaultName      = '${prefix}-${env}-kv'
var containerAppName  = '${prefix}-${env}-app'
var uamiName          = '${prefix}-${env}-id'

// ────────────────────────────────────────────────────────────────────────────────
// User-Assigned Managed Identity
// Created before all other resources so its principalId is available for the
// Key Vault RBAC assignment and its resourceId is available for the Container App.
// ────────────────────────────────────────────────────────────────────────────────
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

// ────────────────────────────────────────────────────────────────────────────────
// Log Analytics workspace
// ────────────────────────────────────────────────────────────────────────────────
module logAnalytics 'modules/logAnalytics.bicep' = {
  name: 'deploy-log-analytics'
  params: {
    location: location
    workspaceName: logWorkspaceName
  }
}

// ────────────────────────────────────────────────────────────────────────────────
// Container Apps managed environment
// ────────────────────────────────────────────────────────────────────────────────
module caEnvironment 'modules/environment.bicep' = {
  name: 'deploy-ca-environment'
  params: {
    location: location
    environmentName: caEnvironmentName
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
    logAnalyticsCustomerId: logAnalytics.outputs.customerId
  }
}

// ────────────────────────────────────────────────────────────────────────────────
// PostgreSQL Flexible Server
// The admin password and connection string are secure params; they are never
// passed through outputs or logged.
// ────────────────────────────────────────────────────────────────────────────────
module postgres 'modules/postgres.bicep' = {
  name: 'deploy-postgres'
  params: {
    location: location
    serverName: postgresName
    adminPassword: dbAdminPassword
    skuTier: postgresTier
    skuName: postgresSkuName
    storageSizeGb: postgresStorageSizeGb
  }
}

// ────────────────────────────────────────────────────────────────────────────────
// Key Vault
// Stores ingest-tokens and db-connection secrets; grants the UAMI the
// Key Vault Secrets User role so the Container App can retrieve them at runtime.
// ────────────────────────────────────────────────────────────────────────────────
module keyVault 'modules/keyVault.bicep' = {
  name: 'deploy-key-vault'
  params: {
    location: location
    keyVaultName: keyVaultName
    uamiPrincipalId: uami.properties.principalId
    ingestTokens: ingestTokens
    dbConnectionString: dbConnectionString
  }
}

// ────────────────────────────────────────────────────────────────────────────────
// Container App
// ────────────────────────────────────────────────────────────────────────────────
module containerApp 'modules/containerApp.bicep' = {
  name: 'deploy-container-app'
  params: {
    location: location
    containerAppName: containerAppName
    caEnvironmentId: caEnvironment.outputs.environmentId
    acrLoginServer: acrLoginServer
    imageTag: imageTag
    uamiId: uami.id
    kvIngestTokensSecretUri: keyVault.outputs.ingestTokensSecretUri
    kvDbConnectionSecretUri: keyVault.outputs.dbConnectionSecretUri
    maxReplicas: maxReplicas
    maxBodyBytes: maxBodyBytes
    maxBatchLines: maxBatchLines
    ratePermits: ratePermits
    rateWindowSeconds: rateWindowSeconds
    retentionDays: retentionDays
    precreateDays: precreateDays
    dedupRetentionDays: dedupRetentionDays
    jobIntervalHours: jobIntervalHours
  }
}

// ────────────────────────────────────────────────────────────────────────────────
// Outputs  (non-sensitive only)
// ────────────────────────────────────────────────────────────────────────────────
@description('Fully qualified domain name of the Container App ingress.')
output containerAppFqdn string = containerApp.outputs.fqdn

@description('Fully qualified domain name of the PostgreSQL Flexible Server.')
output postgresServerFqdn string = postgres.outputs.serverFqdn

@description('URI of the Key Vault (for referencing secrets in future tooling).')
output keyVaultUri string = keyVault.outputs.keyVaultUri
