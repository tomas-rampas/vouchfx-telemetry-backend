// deploy/modules/logAnalytics.bicep
// Log Analytics workspace for Container Apps diagnostic logging and platform metrics.

param location string

@description('Name of the Log Analytics workspace.')
param workspaceName string

@description('Log retention in days (30–730). Lower values reduce cost; pilot default is 30.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      // Keep local authentication enabled (disableLocalAuth: false) — the
      // shared-key shipper in environment.bicep uses listKeys() to obtain the
      // workspace key and write logs.  Disable only if all log shippers are
      // migrated to a Entra-based data-collection endpoint.
      disableLocalAuth: false
    }
  }
}

// workspaceId is the full ARM resource ID, passed to environment.bicep so it
// can call listKeys() locally without the key ever appearing as a plain output.
output workspaceId string = workspace.id

// customerId is the Log Analytics workspace GUID required by the Container Apps
// environment's appLogsConfiguration.  It is not sensitive.
output customerId string = workspace.properties.customerId
