// deploy/modules/containerApp.bicep
// Azure Container App — vouchfx Telemetry Backend service.
//
// ── Replica count: minReplicas MUST be 1 ─────────────────────────────────────
//   The retention/partition-maintenance job is implemented as an in-process
//   IHostedService timer (BackgroundService).  If minReplicas = 0 the
//   Container Apps platform scales the app to zero under no load, which
//   means the timer never fires and old partitions are never dropped.
//   minReplicas: 1 ensures at least one instance is always running so the
//   background job executes on schedule regardless of ingest traffic.
//   Consequence: a single idle replica incurs ~$15–20/month on Consumption
//   workload profile — an acceptable pilot cost.
//   Future alternative: extract the maintenance job to a dedicated Azure
//   Function or Container App Job on a cron trigger, which could then allow
//   minReplicas: 0 on the ingest service.
//
// ── Identity: User-Assigned Managed Identity (UAMI) ──────────────────────────
//   See main.bicep for the rationale.  The UAMI is the identity used for KV
//   secret retrieval; the same UAMI should be granted AcrPull on the ACR if
//   pull authentication is needed (not required when using az acr build push).
//
// ── Hardened production delta (documented — not yet applied) ─────────────────
//   1. VNet egress: wire the managed environment to a VNet subnet so all
//      outbound traffic (to Postgres, KV) stays on the Azure backbone.
//   2. ACR pull via managed identity: grant the UAMI AcrPull and set
//      registries[].identity = uamiId instead of using ACR admin credentials.
//   3. Dedicated workload profile (Dedicated D4) for predictable CPU/mem under
//      sustained ingest load.

param location string

@description('Name of the Container App.')
param containerAppName string

@description('Resource ID of the Container Apps managed environment.')
param caEnvironmentId string

@description('ACR login server FQDN, e.g. vfxtelregistry.azurecr.io')
param acrLoginServer string

@description('Container image tag.')
param imageTag string

@description('Resource ID of the UAMI used for Key Vault secret retrieval.')
param uamiId string

@description('Versionless KV URI for the ingest-tokens secret.')
param kvIngestTokensSecretUri string

@description('Versionless KV URI for the db-connection secret.')
param kvDbConnectionSecretUri string

// Scale
@minValue(1)
param maxReplicas int = 3

// Non-secret tunable env vars
param maxBodyBytes int     = 2097152
param maxBatchLines int    = 500
param ratePermits int      = 120
param rateWindowSeconds int = 60
param retentionDays int    = 90
param precreateDays int    = 7
param dedupRetentionDays int = 35
param jobIntervalHours int = 24

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location

  // UAMI is the identity used to pull KV secrets at runtime.
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }

  properties: {
    environmentId: caEnvironmentId

    // ── Ingress ──────────────────────────────────────────────────────────────
    // External ingress: platform provisions a public HTTPS endpoint.
    // allowInsecure: false enforces HTTPS-only at the platform level; plain
    // HTTP connections are rejected before they reach the container.
    // TLS certificate is managed by the platform (no certificate provisioning
    // required for the Container Apps default domain).
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }

      // ── KV-backed secrets ─────────────────────────────────────────────────
      // identity references the UAMI resource ID so the platform uses the UAMI
      // (not a system-assigned identity) to read the KV secret at runtime.
      // The versionless URIs mean secret rotation in KV automatically surfaces
      // to the Container App without a redeployment (Azure polls for changes).
      secrets: [
        {
          name: 'ingest-tokens'
          keyVaultUrl: kvIngestTokensSecretUri
          identity: uamiId
        }
        {
          name: 'db-connection'
          keyVaultUrl: kvDbConnectionSecretUri
          identity: uamiId
        }
      ]
    }

    template: {
      containers: [
        {
          name: 'backend'
          image: '${acrLoginServer}/vouchfx-telemetry-backend:${imageTag}'

          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }

          // ── Environment variables ─────────────────────────────────────────
          // Secrets are referenced by name (secretRef); their values are never
          // logged or exposed in deployment history.
          // Non-secret tunables are passed as plain values.
          env: [
            // Secrets (injected from KV via Container App secret store)
            {
              name: 'VOUCHFX_TELEMETRY_INGEST_TOKENS'
              secretRef: 'ingest-tokens'
            }
            {
              name: 'ConnectionStrings__Telemetry'
              secretRef: 'db-connection'
            }
            // Runtime bind address — matches EXPOSE 8080 and ingress targetPort.
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            // Tunables
            {
              name: 'VOUCHFX_TELEMETRY_MAX_BODY_BYTES'
              value: string(maxBodyBytes)
            }
            {
              name: 'VOUCHFX_TELEMETRY_MAX_BATCH_LINES'
              value: string(maxBatchLines)
            }
            {
              name: 'VOUCHFX_TELEMETRY_RATE_PERMITS'
              value: string(ratePermits)
            }
            {
              name: 'VOUCHFX_TELEMETRY_RATE_WINDOW_SECONDS'
              value: string(rateWindowSeconds)
            }
            {
              name: 'VOUCHFX_TELEMETRY_RETENTION_DAYS'
              value: string(retentionDays)
            }
            {
              name: 'VOUCHFX_TELEMETRY_PRECREATE_DAYS'
              value: string(precreateDays)
            }
            {
              name: 'VOUCHFX_TELEMETRY_DEDUP_RETENTION_DAYS'
              value: string(dedupRetentionDays)
            }
            {
              name: 'VOUCHFX_TELEMETRY_JOB_INTERVAL_HOURS'
              value: string(jobIntervalHours)
            }
          ]

          // ── Health probes ─────────────────────────────────────────────────
          // Liveness: if /healthz returns non-2xx the container is restarted.
          // Readiness: if /readyz returns non-2xx the replica is removed from
          //   the load balancer until it recovers (e.g. while Postgres is
          //   initialising or the schema bootstrap is running).
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 30
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/readyz'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]

      // ── Scaling ───────────────────────────────────────────────────────────
      // minReplicas: 1  — see module header comment (in-process timer requirement).
      // HTTP concurrency rule: scale out when concurrent HTTP requests exceed
      // the threshold; scale in when load drops (with a stabilisation window).
      scale: {
        minReplicas: 1
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

output fqdn string = containerApp.properties.configuration.ingress.fqdn
