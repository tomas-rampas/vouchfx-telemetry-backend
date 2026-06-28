// deploy/modules/environment.bicep
// Azure Container Apps managed environment wired to Log Analytics.
//
// The Log Analytics shared key is retrieved inside this module via listKeys()
// rather than being passed as a parameter from main.bicep.  This avoids the
// key ever appearing as a plain Bicep output (which would surface it in
// deployment history).  Only the non-sensitive workspace resource ID and
// customer ID are passed in.

param location string

@description('Name of the Container Apps managed environment.')
param environmentName string

@description('Full ARM resource ID of the Log Analytics workspace.')
param logAnalyticsWorkspaceId string

@description('Log Analytics workspace customer ID (GUID). Used by the Container Apps log shipper.')
param logAnalyticsCustomerId string

// Retrieve the workspace shared key at deploy time within this module scope.
// listKeys() is an ARM function evaluated server-side; the value is not
// written to any Bicep output or deployment log.
var logKey = listKeys(logAnalyticsWorkspaceId, '2022-10-01').primarySharedKey

resource caEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logKey
      }
    }
  }
}

output environmentId string = caEnvironment.id
