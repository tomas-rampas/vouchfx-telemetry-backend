// deploy/parameters/prod.bicepparam
// Non-secret parameters for the production environment.
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
param environmentSuffix = 'prod'

// ── Compute / scale ───────────────────────────────────────────────────────────
// GeneralPurpose D2s: enables zone-redundant HA and better throughput.
// Upgrade from Burstable once pilot traffic characterisation is complete.
param postgresTier          = 'GeneralPurpose'
param postgresSkuName       = 'Standard_D2s_v3'
param postgresStorageSizeGb = 64
param maxReplicas           = 3

// ── Tunables ──────────────────────────────────────────────────────────────────
param retentionDays      = 90    // 90-day partition retention per design.
param precreateDays      = 7     // Pre-create 7 future partitions.
param dedupRetentionDays = 35    // 35-day dedup window (30-day outbox + headroom).
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
