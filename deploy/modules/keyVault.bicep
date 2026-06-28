// deploy/modules/keyVault.bicep
// Azure Key Vault storing the ingest-tokens and db-connection secrets.
//
// Access model: RBAC (enableRbacAuthorization: true).
//   The UAMI is granted the built-in 'Key Vault Secrets User' role which
//   allows read access to secret contents.  Classic access policies are
//   disabled — RBAC is the modern, recommended model for AKV.
//
// ── Hardened production delta (documented — not yet applied) ─────────────────
//   1. Private endpoint + VNet integration: disable publicNetworkAccess and
//      create a private endpoint in the same VNet as the Container Apps
//      environment so KV is not reachable from the internet.
//   2. Purge protection: set enablePurgeProtection = true to prevent accidental
//      permanent deletion (requires soft-delete to be enabled, which it is).
//   3. Firewall: set networkAcls.defaultAction = 'Deny' and allow only the
//      Container Apps environment's outbound CIDR range.
// ─────────────────────────────────────────────────────────────────────────────

param location string

@description('Name of the Key Vault (globally unique, 3-24 chars, alphanumeric + hyphens).')
param keyVaultName string

@description('Principal ID (object ID) of the UAMI that will be granted Secrets User access.')
param uamiPrincipalId string

@description('Comma-separated bearer ingest tokens. Stored as KV secret ingest-tokens.')
@secure()
param ingestTokens string

@description('Npgsql connection string to the telemetry database. Stored as KV secret db-connection.')
@secure()
param dbConnectionString string

// Key Vault Secrets User built-in role definition ID.
// Allows: Get, List on secret contents.
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e0'

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    // RBAC authorisation model (preferred over access policies).
    enableRbacAuthorization: true
    // Soft-delete: secrets can be recovered within the retention window.
    enableSoftDelete: true
    softDeleteRetentionInDays: 7   // 7 days for pilot; use 90 in production.
    // Production hardening: set publicNetworkAccess = 'Disabled' + private endpoint.
    publicNetworkAccess: 'Enabled'
  }
}

// ── Secrets ──────────────────────────────────────────────────────────────────

resource ingestTokensSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'ingest-tokens'
  properties: {
    // PLACEHOLDER: the value is the actual comma-separated bearer tokens passed
    // as a secure deploy-time parameter.  Never hardcode here.
    value: ingestTokens
  }
}

resource dbConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'db-connection'
  properties: {
    // PLACEHOLDER: the value is the actual Npgsql connection string passed as
    // a secure deploy-time parameter.  Never hardcode here.
    value: dbConnectionString
  }
}

// ── RBAC: grant the UAMI 'Key Vault Secrets User' on this vault ──────────────
// The role assignment name is a deterministic GUID derived from the vault ID,
// principal ID, and role ID — making the assignment idempotent across deploys.
resource kvSecretsUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, uamiPrincipalId, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: uamiPrincipalId
    // ServicePrincipal covers both app registrations and managed identities.
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────
output keyVaultUri string = kv.properties.vaultUri

// Versionless secret URIs — the Container App resolves the current version at
// runtime, enabling secret rotation without a redeployment.
output ingestTokensSecretUri string = ingestTokensSecret.properties.secretUri
output dbConnectionSecretUri string  = dbConnectionSecret.properties.secretUri
