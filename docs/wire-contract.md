# Wire Contract Specification

This document is the authoritative specification of the HTTP API this backend implements.

## Overview

The telemetry backend provides two endpoints:

1. **`POST /v1/telemetry`** — Ingest opt-in telemetry batches from the engine client
2. **`POST /v1/telemetry/forget`** — Enqueue deletion of a specific install's data

Both endpoints require HTTP Bearer authentication via an `Authorization` header; the shared token value is deployed via Azure Key Vault at startup.

## Endpoint: POST /v1/telemetry

### Request

**Content-Type:** `application/x-ndjson` (mandatory; charset parameters are permitted but ignored)

**Headers:**

- `Authorization: Bearer <token>` — A valid ingest token (shared, symmetric; configured at deployment time)
- `Idempotency-Key: <64-char-hex>` — The lowercase hexadecimal SHA-256 digest of the **exact raw request body** (all bytes, as the client sends them). This value is authoritative; the backend validates its format (64 lowercase hex characters) but does not recompute it. It serves as the batch-level dedup key.

**Body:** Zero or more newline-separated JSON objects, each a `TelemetryEvent` (see Schema).

#### Examples

A valid minimal batch with two events:

```
POST /v1/telemetry HTTP/1.1
Host: vfxtel-prod-app.example.azurecontainers.io
Content-Type: application/x-ndjson
Authorization: Bearer eyJhbGci...
Idempotency-Key: a1b2c3d4e5f6...f6e5d4c3b2a1a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3

{"schemaVersion":1,"timestamp":"2026-06-28T14:30:00+00:00","installId":"550e8400-e29b-41d4-a716-446655440000",...}
{"schemaVersion":1,"timestamp":"2026-06-28T14:35:00+00:00","installId":"6ba7b810-9dad-11d1-80b4-00c04fd430c8",...}
```

### Response

#### 2xx Success

**Status code:** 200 (OK)

**Body:** Empty

**Semantics:** The batch was accepted. If this is a duplicate (same `Idempotency-Key`), the service still returns 200 but does not re-process the batch — this idempotence allows the client to safely retry without data duplication.

The service may accept the batch in its entirety, or may silently skip individual duplicate event rows (via `ON CONFLICT DO NOTHING` on the natural key `(install_id, event_timestamp, schema_version)`). The client observes only the 200 status and does not distinguish these cases.

#### 4xx Client Error

| Status | Reason | Client Action |
|--------|--------|---------------|
| **400** | Request validation failed | Do not retry. Likely causes: missing/invalid `Idempotency-Key`, empty body, malformed JSON, unknown event field (schema version 1 only), event timestamp too far in the future (>2 days ahead). The server logs the parse error but does not return it to the caller. |
| **401** | Authentication failed | Do not retry. The `Authorization` header is missing or the token is not on the configured allowlist. Store the token securely and retry only after reconfiguring. |
| **413** | Payload too large | Do not retry until the client batches more carefully. Caused by: request body exceeds `VOUCHFX_TELEMETRY_MAX_BODY_BYTES` (default 2 MiB), or NDJSON line count exceeds `VOUCHFX_TELEMETRY_MAX_BATCH_LINES` (default 500). |
| **415** | Unsupported media type | Do not retry. The `Content-Type` header is missing or not `application/x-ndjson`. |
| **429** | Rate limit exceeded | Retry after the `Retry-After` header value (in seconds). The backend enforces two-layer rate limiting: per-token fixed-window (default 120 requests per 60-second window) and global cap (10× the per-token limit). Invalid/missing tokens share a single global partition to prevent DOS via token variation. |

#### 5xx Server Error

| Status | Reason | Client Action |
|--------|--------|---------------|
| **503** | Service unavailable (transient storage fault) | Retry the **entire batch with the same `Idempotency-Key`** after the `Retry-After` header value (in seconds, default 30). The database is temporarily unavailable (e.g. Postgres connection pool exhausted, network partition). The idempotency key ensures that when the connection recovers, a retry will not duplicate data. |

### TelemetryEvent Schema

The allowlist contract (frozen for v1.x). A `TelemetryEvent` is a JSON object with these required fields:

| Field | Type | Description |
|-------|------|-------------|
| `schemaVersion` | `int` | The telemetry schema version. `v1` is strict (unknown fields rejected). `v>1` is lenient for forward-compatibility (unknown fields tolerated but never stored, because the typed allowlist prevents them from binding to a C# property). |
| `timestamp` | `ISO8601 DateTimeOffset` | UTC timestamp at which the telemetry batch was built (the end of the vouchfx run). Must not be >2 days in the future. |
| `installId` | `GUID` | An opaque, randomly-generated identifier unique to this vouchfx installation (minted at opt-in, deleted on `telemetry disable`). This is the only row-level identifier; it does not identify the user, machine, or any test content. |
| `toolVersion` | `string` | Informational version of the vouchfx CLI, e.g. `"1.0.0"`. |
| `engineVersion` | `string` | Informational version of the vouchfx engine assembly. |
| `dotnetVersion` | `string` | Description of the .NET runtime the tool ran on, e.g. `".NET 8.0.7"` (from `RuntimeInformation.FrameworkDescription`). |
| `runCount` | `int` | Number of suite runs. Always `1` in v1 (one event per `vouchfx run`); present for future aggregation without schema change. |
| `scenarioCount` | `int` | Number of scenarios that executed in this run. |
| `stepVerdicts` | `TelemetryVerdictCounts` | Per-verdict step counts across the run (structure below). |
| `scenarioVerdicts` | `TelemetryVerdictCounts` | Per-verdict scenario counts across the run (structure below). |
| `stepFamilies` | `object` | JSON object: step counts keyed by family (intent: `"http"`, `"db-assert"`, `"mq-publish"`, etc.). Keys are drawn only from the frozen built-in Core family taxonomy; any custom/non-Core provider step is counted under the `"custom"` bucket. Example: `{"http":3,"db-assert":1}` |
| `stepProviders` | `object` | JSON object: step counts keyed by family.provider (technology: `"http.rest"`, `"db-assert.postgres"`, etc.). Keys are drawn only from the frozen built-in Core provider taxonomy; custom steps are counted under `"custom"`. Example: `{"http.rest":3,"db-assert.postgres":1}` |
| `startupMs` | `long` | Wall-clock milliseconds from run start to first scenario start (topology + engine startup). |
| `timeToFirstTestMs` | `long` | Wall-clock milliseconds from run start to first step completion (time-to-first-test). |

#### TelemetryVerdictCounts Structure

Used for both `stepVerdicts` and `scenarioVerdicts`:

| Field | Type | Description |
|-------|------|-------------|
| `pass` | `int` | Count of steps/scenarios with Verdict = Pass |
| `fail` | `int` | Count of steps/scenarios with Verdict = Fail |
| `envError` | `int` | Count of steps/scenarios with Verdict = Environment Error (infrastructure fault) |
| `inconclusive` | `int` | Count of steps/scenarios with Verdict = Inconclusive (timeout or partition outlasted grace) |

### Schema Versioning

- **Schema version 1:** Strict mode. During deserialization, any JSON property not declared on the `TelemetryEvent` record is rejected with a 400 error. This is a defence-in-depth check: the engine's allowlist enforcement on the client side is the primary guard; the backend's strict parsing is the secondary guard to detect any bypass.

- **Schema version >1:** Lenient mode. Unknown properties are tolerated but never stored. This permits a future engine release to add new metrics without breaking the backend. Because the `TelemetryEvent` record is an allowlist (only declared properties can bind), unknown fields have nowhere to live in the CLR and are automatically discarded during deserialization.

The backend treats both as 200 OK and ingests the event. An at-least-once client emitting a schema version from a newer engine is never rejected forever.

## Endpoint: POST /v1/telemetry/forget

### Request

**Content-Type:** `application/json` (mandatory; charset parameters are permitted but ignored)

**Headers:**

- `Authorization: Bearer <token>` — A valid ingest token (same as `/v1/telemetry`)

**Body:**

```json
{
  "installId": "550e8400-e29b-41d4-a716-446655440000"
}
```

The `installId` field is required and must be a valid GUID.

### Response

#### 200 OK

**Body:** Empty

**Semantics:** The forget request was enqueued. The backend's daily maintenance job will process the queue and delete all rows belonging to this install from `telemetry_event` and `ingest_batch` within ~24 hours (see operations guide). The service guarantees a best-effort deletion within the 30-day commitment.

#### 4xx Client Error

| Status | Reason | Client Action |
|--------|--------|---------------|
| **400** | Request validation failed | Do not retry. Likely causes: missing `installId` field, non-GUID value, malformed JSON. |
| **401** | Authentication failed | Do not retry. The token is not on the configured allowlist. |
| **415** | Unsupported media type | Do not retry. The `Content-Type` header is not `application/json`. |
| **429** | Rate limit exceeded | Retry after the `Retry-After` header (same rules as `/v1/telemetry`). |

#### 5xx Server Error

| Status | Reason | Client Action |
|--------|--------|---------------|
| **503** | Service unavailable (transient storage fault) | Retry the request after the `Retry-After` header (default 30 seconds). The database is temporarily unavailable. Forget requests are idempotent by install ID, so retries are safe. |

## Deduplication Strategy

The backend employs two layers of deduplication to tolerate retries and network faults:

### Layer 1: Batch-Level Dedup (Idempotency Key)

Before processing any events in a batch, the service attempts to insert a record into `ingest_batch` with the `Idempotency-Key` as the primary key. If the insert succeeds (0 rows previously existed), the batch is new and processing continues. If the insert fails (a row already exists), the batch is a duplicate and the service returns 200 OK immediately without touching `telemetry_event`.

**Consequence:** If the client sends the exact same batch twice (same body bytes, same SHA-256), the second request is accepted as 200 OK but the events are not re-inserted.

### Layer 2: Row-Level Dedup (Natural Key)

Each event row is inserted into `telemetry_event` with an `ON CONFLICT DO NOTHING` clause on the natural key `(install_id, event_timestamp, schema_version)`. If a row with these three values already exists, the insert is silently skipped.

**Consequence:** If two semantically identical events arrive in separate batches (e.g. after a client restart re-batches the outbox), the duplicate event row is silently skipped.

**Why `event_timestamp` is the partition key:** PostgreSQL requires the partition key to appear in every unique constraint (including the primary key) of a partitioned table. By using `event_timestamp` (the client-reported run-end time) as the partition key, the natural key `(install_id, event_timestamp, schema_version)` becomes a legal primary key on a RANGE-partitioned table, and duplicate detection across partition boundaries works correctly. A re-sent batch (carrying the identical client timestamp) lands in the same partition as the original, ensuring the PK conflict fires.

## Batch Idempotency Key Computation

The client computes the idempotency key as:

```
Idempotency-Key = SHA-256(request_body_bytes) rendered as lowercase hex
```

Where `request_body_bytes` is the **exact raw request body** the client sends to the server — all NDJSON lines, all whitespace, all bytes. The server does not recompute the key; it validates its format (64 lowercase hex characters via the regex `^[0-9a-f]{64}$`) and uses it as the dedup anchor. This keeps the backend decoupled from the client's implementation of the hash (e.g. allowing a future client to use a different hashing scheme without a backend change).

The contract-parity test (`ContractParityTests` in the test suite) verifies that the client and backend compute the same SHA-256 hash for known payloads and that the server's format validation matches.

## Rate Limiting

The backend applies rate limiting at the HTTP request level before any endpoint logic runs:

- **Per-token fixed-window:** Each valid bearer token gets its own rate-limit partition keyed by SHA-256 of the token value. Default: 120 requests per 60-second window.
- **Invalid/missing token:** All unauthenticated requests share a single partition ("unauthenticated"), ensuring that an attacker cannot allocate unbounded partitions by sending different random token values and exhaust server memory.
- **Global cap (belt-and-suspenders):** All requests, valid or invalid, are subject to a global limit of 10× the per-token limit (default 1200 per 60 seconds across all tokens combined).

If a request exceeds the limit, the service returns `HTTP 429 Too Many Requests` with a `Retry-After` header suggesting a delay (default 60 seconds). The `OnRejected` callback in `Program.cs` computes the retry-after value from the rate limiter's lease metadata, or falls back to 60 seconds if metadata is unavailable.

## Health Probes

The backend exposes two unauthenticated, rate-limit-exempt probes for Azure Container Apps:

- **`GET /healthz`** — Liveness probe. Returns 200 if the process is running. No dependency checks.
- **`GET /readyz`** — Readiness probe. Returns 200 if the persistence backend is ready to serve traffic (database connection succeeds, bootstrap completed). Returns 503 if the database is unavailable or not configured. Container Apps removes the replica from the load balancer until readiness recovers.

## Timeouts

All requests have a reasonable HTTP body read timeout enforced by the Kestrel runtime. Request-body parsing has no additional timeout beyond Kestrel's `MinRequestBodyDataRate` (default 240 bytes per second, sufficient for telemetry batches).

Long-running operations (e.g. partition bootstrap on first start) happen during the readiness probe, which has a 5-second timeout before the probe fails. The service must complete schema bootstrap quickly enough to pass readiness before Container Apps begins routing traffic.
