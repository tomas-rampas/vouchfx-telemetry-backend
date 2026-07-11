# Why telemetry, and how to opt in

An overview of vouchfx's opt-in telemetry, what is collected, and how to enable, disable, and verify your privacy.

## Why telemetry exists

vouchfx collects **aggregate, anonymous usage metrics** to improve the platform and prioritise features:

- **Provider prioritisation:** Which step families and providers are actually used in production? The answer shapes the roadmap and development focus.
- **Flake diagnosis:** Which providers experience the most environment errors? This informs stability work and test-infrastructure recommendations.
- **Platform support matrix:** Which .NET runtime versions and tool versions are in use? This data informs when legacy versions can be retired and when new dependencies must be added.
- **Performance baseline:** How long does a typical startup take? How much time passes before the first test runs? Trends inform optimisation priorities.

Telemetry is **strictly opt-in**: nothing is collected until you explicitly run `vouchfx telemetry enable`.

## What is sent — and what never is

The backend stores only the fields listed in the **allowlist** (see [Wire Contract §TelemetryEvent Schema](wire-contract.md#telemetryevent-schema)). A strict rule governs both the engine client and the backend:

**Allowed fields:** Version identifiers, run/scenario/step counts, verdict breakdowns (pass/fail/environment error/inconclusive), step family and provider usage, wall-clock timings (startup, time-to-first-test).

**Never collected:**
- Test step names, step IDs, or any step contents
- Captured variable values
- Secrets or credentials
- System Under Test (SUT) hostnames, URLs, IP addresses, or any customer identifiers
- Container image names or any customer-specific configuration
- Scenario names or test intent
- Test data, fixtures, or payloads

The **physical guarantee:** The `TelemetryEvent` record on the backend is an explicit allowlist of permitted fields. Any field not declared on this record has no C# property to bind to during JSON deserialisation, and therefore **cannot be stored no matter how the client sends it**. This absence is structural — the data has nowhere to live.

<!-- Field summary duplicated from docs/wire-contract.md — update that first -->

## How to opt in

Telemetry is off by default. To enable it, run:

```bash
vouchfx telemetry enable
```

This command:
1. Generates a unique install ID (a random GUID) if one does not exist
2. Stores your consent locally on your machine
3. Prints a summary of what will be collected

**After enabling:** Each time you run `vouchfx run`, the engine collects aggregate counts and metrics and stores them locally. Nothing is sent unless you configure an endpoint (see below).

For full details on the enable command and what vouchfx considers "anonymous, aggregate usage data," see the [engine's telemetry reference](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html).

## How to opt out, forever or per-run

### Permanent opt-out

Run:

```bash
vouchfx telemetry disable
```

This command:
1. Deletes your local install ID
2. Clears the local outbox (unsent telemetry events)
3. Asks the backend (if configured) to delete any stored data linked to your install ID

Once disabled, telemetry is off and nothing is collected on future runs.

### Per-run opt-out

Disable telemetry for a single run without permanently opting out:

```bash
VOUCHFX_NO_TELEMETRY=1 vouchfx run …
```

This environment variable suppresses telemetry collection for that run only. Your opt-in status remains unchanged for future runs.

For more details, see the [engine's telemetry reference](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html).

## Verify exactly what would be sent — the local outbox

When you enable telemetry, the engine collects events into a **local outbox file**. By default, nothing is sent to a backend until you configure an endpoint. This lets you inspect the outbox and see exactly what would be sent:

**Outbox location:**
- **Windows:** `%APPDATA%\vouchfx\telemetry-outbox.jsonl`
- **macOS/Linux:** `~/.config/vouchfx/telemetry-outbox.jsonl`

Check the outbox path any time by running:

```bash
vouchfx telemetry status
```

**Outbox format:** Each line is a complete JSON object (NDJSON — newline-delimited JSON). No line contains secrets or test content. Each line represents one run and contains the aggregate counts and timings collected during that run. Example fields (not an exhaustive list):

```json
{
  "schemaVersion": 1,
  "timestamp": "2026-06-28T14:30:00+00:00",
  "installId": "550e8400-e29b-41d4-a716-446655440000",
  "toolVersion": "1.0.0",
  "engineVersion": "1.0.0",
  "dotnetVersion": ".NET 8.0.7",
  "runCount": 1,
  "scenarioCount": 3,
  "stepVerdicts": {
    "pass": 9,
    "fail": 1,
    "envError": 0,
    "inconclusive": 0
  },
  "stepFamilies": {"http": 5, "db-assert": 3, "script": 2},
  "stepProviders": {"http.rest": 5, "db-assert.postgres": 3},
  "startupMs": 5000,
  "timeToFirstTestMs": 8500
}
```

Each line is what the transport would POST to the backend if an endpoint were configured. Delete the outbox at any time if you wish to clear local history.

## Sending to a backend

By default, telemetry is collected locally and never sent anywhere. To send telemetry to a backend, set two environment variables:

**`VOUCHFX_TELEMETRY_ENDPOINT`** — The base URL of the ingest endpoint (e.g. `https://vfx-telemetry.example.com`)

**`VOUCHFX_TELEMETRY_TOKEN`** — A bearer token issued by the backend operator (e.g. a long random string or UUID)

Example:

```bash
export VOUCHFX_TELEMETRY_ENDPOINT="https://vfx-telemetry.example.com"
export VOUCHFX_TELEMETRY_TOKEN="my-ingest-token"
vouchfx run …
```

**Failure mode:** If the endpoint is unreachable, the token is invalid, or the network is offline, the engine stays silent — telemetry is **never sent over a broken pipe**, and the run completes normally. The local outbox accumulates unsent events; they will be retried on a subsequent run (the engine backs off between failed drain attempts).

For configuration details and troubleshooting, see the [engine's telemetry reference](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html).

## Your right to be forgotten

When you run `vouchfx telemetry disable`, the engine sends a **best-effort deletion request** to the backend. The backend processes the request within ~24 hours and deletes:

1. All telemetry events linked to your install ID
2. All batch-level dedup records linked to your install ID

**Deletion commitment:** Your data is deleted from the active database within 30 days. Point-in-time restore backups (a separate Azure feature) may contain aged copies for up to 7 days, but these are not queryable or accessible without database administrator intervention.

For details on how deletion is processed and its guarantees, see [Privacy Policy § Deletion (Forget) Workflow](privacy.md#deletion-forget-workflow).

## Where the data lives, and for how long

**Storage:** PostgreSQL database (hosted, location determined by operator)

**Default retention:** 90 days. Telemetry events older than 90 days are automatically deleted on a daily maintenance job. You can configure a different retention window when deploying a backend.

**Pilot status:** The vouchfx telemetry backend is complete and tested. **No hosted instance is currently running.** If you self-host a backend, you operate and own the database; retention and deletion are your responsibility.

For authoritative details on data retention, how the backend processes deletions, and compliance considerations, see [Privacy Policy](privacy.md).

## Where to ask questions

**Product-level telemetry discussion** (should telemetry be collected differently? is a metric useful?) belongs in the [vouchfx engine repository's issues](https://github.com/tomas-rampas/vouchfx/issues).

**Backend-specific questions** (deployment, self-hosting, configuration) belong in this repository's issues.

Both repositories follow a [Code of Conduct](../CODE_OF_CONDUCT.md); please read it before opening an issue.
