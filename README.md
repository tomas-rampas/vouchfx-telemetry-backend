# vouchfx-telemetry-backend

The deployed, opt-in **telemetry pilot backend** for [vouchfx](https://github.com/tomas-rampas/vouchfx)
— Phase B of task **S12-G-01** (issue
[vouchfx#152](https://github.com/tomas-rampas/vouchfx/issues/152)).

> **Status:** under construction. The engine-side transport (Phase A) is already merged into the
> engine repo (vouchfx PR #155) and is **inert** until an endpoint + token are configured; this
> repository is the server half it drains to.

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

## The frozen wire contract

This backend implements the contract the (frozen, merged) Phase-A engine client speaks. See
[`docs/wire-contract.md`](docs/wire-contract.md) for the authoritative description, cross-referenced
to vouchfx PR #155.

| | |
|---|---|
| Ingest | `POST /v1/telemetry` · `application/x-ndjson` · `Authorization: Bearer` · `Idempotency-Key` · NDJSON `TelemetryEvent` lines · any 2xx = accepted |
| Forget | `POST /v1/telemetry/forget` · `application/json` · `{"installId":"<guid>"}` · `Authorization: Bearer` |

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
