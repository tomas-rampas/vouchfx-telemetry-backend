# Contributing to vouchfx-telemetry-backend

Thank you for your interest in contributing. This is a deliberately small, focused
repository: the **opt-in telemetry ingest service** for
[vouchfx](https://github.com/tomas-rampas/vouchfx) — an ASP.NET Core 8 minimal-API
service backed by partitioned PostgreSQL. It ingests the engine's opt-in JSON Lines
telemetry batches (`POST /v1/telemetry`), enforces a 90-day retention policy, and
honours install-id deletion requests (`POST /v1/telemetry/forget`).

Please also read our [Code of Conduct](CODE_OF_CONDUCT.md); it applies to all
interactions in this repository.

## Where discussion belongs

- **Bugs and changes in *this service*** (ingest, dedup, retention, forget queue,
  maintenance job, deployment assets) — open an issue or pull request **here**.
- **Product-level discussion** — what telemetry vouchfx collects, the opt-in
  experience, the `TelemetryEvent` allowlist, the engine-side transport — belongs in
  the [engine repository's issues](https://github.com/tomas-rampas/vouchfx/issues).
  This repository implements the server half of that contract; it does not decide it.

## Building and testing

The .NET SDK version is pinned in `global.json` (8.0.400, `rollForward: latestFeature`).

```bash
dotnet build Vouchfx.Telemetry.Backend.sln
dotnet test tests/Vouchfx.Telemetry.Backend.UnitTests        # no Docker required
dotnet test tests/Vouchfx.Telemetry.Backend.IntegrationTests # requires Docker (Testcontainers)
```

CI (`.github/workflows/ci.yml`) runs the equivalent gates on every push and pull
request to `main`:

```bash
dotnet restore Vouchfx.Telemetry.Backend.sln
dotnet build Vouchfx.Telemetry.Backend.sln -c Release --no-restore -warnaserror
dotnet format --verify-no-changes Vouchfx.Telemetry.Backend.sln
dotnet test tests/Vouchfx.Telemetry.Backend.UnitTests -c Release --no-build
docker build -f Dockerfile -t vfxtel:ci-check .   # container-build job: image must build
```

The Testcontainers-based integration tests run in a separate workflow
(`.github/workflows/integration.yml`) on their own schedule; run them locally with
Docker available before submitting changes that touch the database layer.

## The quality bar

Every pull request must pass:

1. **Zero-warning build.** `Directory.Build.props` sets
   `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, and CI builds with
   `-warnaserror`. A new warning is a broken build.
2. **The format gate.** `dotnet format --verify-no-changes` against `.editorconfig`
   must pass; run `dotnet format` locally before pushing.
3. **Unit tests** (no database) and, for changes touching persistence or the wire
   surface, the **Testcontainers Postgres integration tests**.
4. **The container image builds.** CI builds the Dockerfile end-to-end; a missing
   build-context file or broken `COPY` blocks the PR.

Two contracts in this repository are load-bearing and change only deliberately:

- **The wire contract** (`docs/wire-contract.md`) — the engine-side transport is
  already shipped; breaking the ingest surface breaks deployed clients.
- **The privacy allowlist** (`docs/privacy.md`) — the service is
  privacy-allowlist-only *by construction*. Any change that widens what can be
  stored is a privacy change, not a code change: raise it in the engine repository
  first.

## Sign-off (DCO)

All commits must carry a Developer Certificate of Origin sign-off
(`git commit -s`, or the "Sign off" option in the GitHub web UI). The DCO confirms
you have the right to license your contribution; it is the same lightweight standard
used across the vouchfx repositories.

## Licence

This repository is licensed under Apache-2.0, matching the vouchfx engine and
provider tiers. By submitting a pull request, you agree to license your contribution
under Apache-2.0.

## Volatile facts on the documentation site

Version numbers and registry counts shown on the rendered site are resolved at build time via `{{fact:...}}` tokens in `scripts/build_site.py` (with a checked-in fallback in `site/facts-fallback.json`). When writing documentation prose, do not hard-code the current engine or package version — reference the mechanism (a pin file, "the current release") or use a fact token, so pages cannot silently rot. Sibling repos trigger a rebuild here through the `repository_dispatch` trigger in `.github/workflows/pages.yml` (the workflow's `notify` job is the outbound half — it tells the siblings when this repo's own docs change).
