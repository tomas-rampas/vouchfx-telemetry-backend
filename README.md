# vouchfx-telemetry-backend

The deployed, opt-in **telemetry pilot backend** for [vouchfx](https://github.com/tomas-rampas/vouchfx)
— Phase B of task **S12-G-01** (issue
[vouchfx#152](https://github.com/tomas-rampas/vouchfx/issues/152)).

> **Status:** complete and ready for deployment. The engine-side transport (Phase A) is merged
> into the engine repo (vouchfx PR #155) and is **inert** until an endpoint + token are
> configured; this repository contains the server half (complete, tested, documented).
> Operator deployment (Bicep, secrets, database bootstrap) is required before the system is live.

## What it is

A lean **ASP.NET Core 8 minimal-API** service that:

1. **Ingests** the opt-in JSON Lines telemetry batches the vouchfx engine already produces
   (`POST /v1/telemetry`), authenticated with a shared **Bearer** token, and stores them in
   **PostgreSQL** under a **90-day** retention policy.
2. Honours an **install-id deletion** request (`POST /v1/telemetry/forget`) within **30 days**.
3. Self-maintains via a daily in-service job: partition pre-create, 90-day partition drop, and
   forget-queue drain.

It is **privacy-allowlist-only by construction**: it stores only the frozen `TelemetryEvent`
fields (aggregate counts, timings, anonymous install id, versions). Nothing a customer's tests
touch can reach it — there is nowhere to put it.

## Documentation

- **[Wire Contract](docs/wire-contract.md)** — The HTTP API specification (endpoints, status codes, payload schema, deduplication)
- **[Architecture](docs/architecture.md)** — System design, five-component architecture, PostgreSQL schema, Azure topology
- **[Operations Runbook](docs/operations.md)** — Deployment procedures, configuration reference, troubleshooting, maintenance tasks
- **[Privacy Policy](docs/privacy.md)** — Data handling, retention, deletion, and compliance considerations

## Endpoints at a Glance

| Endpoint | Method | Auth | Body | Returns |
|----------|--------|------|------|---------|
| `/v1/telemetry` | POST | Bearer token | `application/x-ndjson` NDJSON `TelemetryEvent` lines | 200 (accepted) / 4xx / 5xx |
| `/v1/telemetry/forget` | POST | Bearer token | `application/json` `{"installId":"<guid>"}` | 200 (queued) / 4xx / 5xx |
| `/healthz` | GET | None | — | 200 (alive) |
| `/readyz` | GET | None | — | 200 (ready) / 503 (not ready) |

## Repository layout

```
src/                 the ASP.NET Core service
tests/               unit tests (no DB) + Testcontainers Postgres integration tests
deploy/              Bicep IaC, Dockerfile inputs, bootstrap.sql
docs/                architecture, operations runbook, privacy, wire-contract
.github/workflows/   CI (build/format/unit), integration, deploy
```

## Build & test

```bash
dotnet build Vouchfx.Telemetry.Backend.sln
dotnet test tests/Vouchfx.Telemetry.Backend.UnitTests        # no Docker required
dotnet test tests/Vouchfx.Telemetry.Backend.IntegrationTests # requires Docker (Testcontainers)
```

## Licence

Apache-2.0 (matching the vouchfx engine and provider tiers).
