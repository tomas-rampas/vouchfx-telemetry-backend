// deploy/modules/postgres.bicep
// Azure Database for PostgreSQL Flexible Server 16.
//
// Pilot configuration: Burstable B1ms, public network access with a firewall
// rule that allows Azure services (0.0.0.0/0.0.0.0 magic rule).
//
// ── Hardened production delta (documented — not yet applied) ─────────────────
//   1. VNet integration + private endpoint: set network.delegatedSubnetResourceId
//      and network.privateDnsZoneArmResourceId; disable publicNetworkAccess.
//   2. Entra managed-identity DB authentication: enable
//      authConfig.activeDirectoryAuthEnabled and disable
//      authConfig.passwordAuthEnabled once all clients support Entra tokens.
//   3. Zone-redundant HA: set highAvailability.mode = 'ZoneRedundant' and
//      highAvailability.standbyAvailabilityZone (requires GeneralPurpose or
//      MemoryOptimized tier — unavailable on Burstable).
//   4. Customer-managed encryption keys (BYOK): set
//      dataEncryption.type = 'AzureKeyVault' and reference a CMK in Key Vault.
//   5. Geo-redundant backups: set backup.geoRedundantBackup = 'Enabled'.
// ─────────────────────────────────────────────────────────────────────────────

param location string

@description('Name of the PostgreSQL Flexible Server (must be globally unique).')
param serverName string

@description('Admin password. Injected as a secure deploy-time parameter; never logged.')
@secure()
param adminPassword string

@description('Compute tier.')
@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'Burstable'

@description('SKU name within the tier (e.g. Standard_B1ms for Burstable).')
param skuName string = 'Standard_B1ms'

@description('Storage size in GB.')
@minValue(32)
param storageSizeGb int = 32

@description('Backup retention in days.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01' = {
  name: serverName
  location: location
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: '16'
    administratorLogin: 'vfxteladmin'
    administratorLoginPassword: adminPassword
    storage: {
      storageSizeGB: storageSizeGb
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      // Geo-redundant backups disabled for pilot cost; enable in production.
      geoRedundantBackup: 'Disabled'
    }
    network: {
      // Public network access: enabled for pilot (see hardened delta above).
      publicNetworkAccess: 'Enabled'
    }
    highAvailability: {
      // HA requires GeneralPurpose or MemoryOptimized; disabled on Burstable.
      mode: 'Disabled'
    }
    authConfig: {
      // Password auth only for pilot.  Production target: Entra auth only.
      passwordAuthEnabled: true
      activeDirectoryAuthEnabled: false
    }
  }
}

// Firewall rule: allows connections from Azure services (the Container Apps
// outbound IPs are inside the Azure backbone so this rule covers them).
// The 0.0.0.0 → 0.0.0.0 range is the Azure magic rule for "allow Azure services".
// Production hardening: replace with VNet integration + private endpoint (see above).
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-12-01' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Create the telemetry database.
resource telemetryDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-12-01' = {
  parent: postgresServer
  name: 'telemetry'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Enforce TLS: require_secure_transport = ON prevents unencrypted connections.
resource requireSecureTransport 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2023-12-01' = {
  parent: postgresServer
  name: 'require_secure_transport'
  properties: {
    value: 'ON'
    source: 'user-override'
  }
}

output serverFqdn string = postgresServer.properties.fullyQualifiedDomainName
