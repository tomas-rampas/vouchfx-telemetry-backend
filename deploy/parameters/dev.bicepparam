// deploy/parameters/dev.bicepparam
// Non-secret parameters for the dev environment.
//
// ── Secrets injected at deploy time — intentionally NOT stored here ──────────
// The five parameters below (acrLoginServer, imageTag, ingestTokens,
// dbAdminPassword, dbConnectionString) are resolved from environment variables
// at Bicep compilation time via readEnvironmentVariable().  They are NEVER
// hard-coded here.  In CI (deploy.yml) the deploy step exports the following
// environment variables from GitHub Actions secrets / computed values:
//
//   VFX_ACR_LOGIN_SERVER      ← vars.ACR_LOGIN_SERVER (GitHub variable)
//   VFX_IMAGE_TAG             ← computed image tag (sha-<short> or semver)
//   VFX_INGEST_TOKENS         ← secrets.TELEMETRY_INGEST_TOKENS
//   VFX_DB_ADMIN_PASSWORD     ← secrets.DB_ADMIN_PASSWORD
//   VFX_DB_CONNECTION_STRING  ← secrets.DB_CONNECTION_STRING
//
// Omitting any of these variables will cause the Bicep compilation to fail
// fast before any resource is modified (fail-secure behaviour).
// ─────────────────────────────────────────────────────────────────────────────

using '../main.bicep'

// ── Naming ────────────────────────────────────────────────────────────────────
param prefix            = 'vfxtel'
param environmentSuffix = 'dev'

// ── Compute / scale ───────────────────────────────────────────────────────────
// Burstable B1ms: cheapest option for a pilot with low sustained load.
param postgresTier       = 'Burstable'
param postgresSkuName    = 'Standard_B1ms'
param postgresStorageSizeGb = 32
param maxReplicas        = 2

// ── Tunables (dev uses defaults for most; can be tightened for cost) ──────────
param retentionDays      = 30    // Keep 30 days of data in dev (vs 90 in prod).
param precreateDays      = 3     // Pre-create 3 future partitions in dev.
param dedupRetentionDays = 35    // Must be >= retentionDays AND >= the 30-day client outbox window (+5 days headroom, matching prod rationale).
param jobIntervalHours   = 24
param maxBodyBytes       = 2097152
param maxBatchLines      = 500
param ratePermits        = 120
param rateWindowSeconds  = 60

// ── Deploy-time secrets (resolved from CI environment variables) ──────────────
param acrLoginServer     = readEnvironmentVariable('VFX_ACR_LOGIN_SERVER')
param imageTag           = readEnvironmentVariable('VFX_IMAGE_TAG')
param ingestTokens       = readEnvironmentVariable('VFX_INGEST_TOKENS')
param dbAdminPassword    = readEnvironmentVariable('VFX_DB_ADMIN_PASSWORD')
param dbConnectionString = readEnvironmentVariable('VFX_DB_CONNECTION_STRING')
