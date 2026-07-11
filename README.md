# vouchfx-telemetry-backend

The server half of [vouchfx](https://github.com/tomas-rampas/vouchfx)'s opt-in, privacy-first telemetry system.

> **Status:** complete and tested. The engine-side transport ships in the vouchfx engine and
> stays inert until an endpoint and token are configured. This repository contains the server half.
> Operator deployment (Bicep, secrets, database bootstrap) via GitHub Actions is required before
> the system is live. There is no hosted instance running — telemetry remains local-only unless self-hosted.

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

- **[Why telemetry & how to opt in](docs/why-telemetry.md)** — User-facing overview: what telemetry collects, how to enable/disable, and your privacy rights
- **[Self-hosting without Azure](docs/self-hosting.md)** — Building and running the backend on your own infrastructure
- **[Wire Contract](docs/wire-contract.md)** — The HTTP API specification (endpoints, status codes, payload schema, deduplication)
- **[Architecture](docs/architecture.md)** — System design, five-component architecture, PostgreSQL schema, Azure topology
- **[Operations Runbook](docs/operations.md)** — Deployment procedures, configuration reference, troubleshooting, maintenance tasks
- **[Privacy Policy](docs/privacy.md)** — Data handling, retention, deletion, and compliance considerations
- **[Engine telemetry reference](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html)** — CLI-side telemetry configuration in the vouchfx engine

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

## Contributing & security

Contributions are welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md) for the build,
test, and quality gates, and the [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) that
applies to all interactions here. To report a suspected vulnerability, please use
GitHub private vulnerability reporting as described in [`SECURITY.md`](SECURITY.md)
— never a public issue. Product-level telemetry discussion belongs in the
[engine repository's issues](https://github.com/tomas-rampas/vouchfx/issues).

## Licence

Apache-2.0 (matching the vouchfx engine).
