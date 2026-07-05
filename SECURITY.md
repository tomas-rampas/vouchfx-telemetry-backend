# Security Policy

This repository contains the opt-in **telemetry ingest service** for
[vouchfx](https://github.com/tomas-rampas/vouchfx): an ASP.NET Core 8 minimal-API
service that accepts the engine's JSON Lines telemetry batches over a
Bearer-token-authenticated endpoint and stores them in partitioned PostgreSQL under
a 90-day retention policy, with an install-id deletion ("forget") path. It is an
internet-facing service holding data collected under explicit privacy commitments
([`docs/privacy.md`](docs/privacy.md)), so we take security reports seriously and
aim to respond quickly and transparently.

This document describes how to report a vulnerability, what is in and out of scope,
and our coordinated-disclosure expectations.

## Reporting a vulnerability

**Please do not open a public GitHub issue, pull request, or discussion for a
suspected security vulnerability.** Public disclosure before a fix is available
puts every user at risk.

Report privately via **GitHub private vulnerability reporting on this repository**:
go to **Security → Advisories → Report a vulnerability**
(`https://github.com/tomas-rampas/vouchfx-telemetry-backend/security/advisories/new`).
This opens a private channel visible only to you and the maintainers, with a
built-in workflow for coordinating the fix and publishing a CVE where warranted.

If you cannot use that route, open a *minimal*, non-revealing public issue that says
only "I have a security report, please provide a private contact" — never include
details, reproduction steps, or proof-of-concept in the public channel.

### What to include

A good report lets us reproduce and triage fast. Please include, where possible:

- A clear description of the issue and the **impact** (what an attacker can do).
- The **affected surface** and version/commit (an endpoint, the maintenance job,
  the wire-contract validation, a deployment asset).
- **Reproduction steps** — ideally a minimal request sequence or payload that
  triggers the issue.
- Any **proof-of-concept**, logs, or stack traces (redact any real tokens or
  connection strings first).
- Your assessment of **severity** and any known mitigations or workarounds.

## Our commitment (coordinated disclosure)

When you report privately, we will:

| Stage | Target |
| --- | --- |
| **Acknowledge** receipt of your report | within **3 business days** |
| **Initial assessment** (validity, severity, scope) | within **7 business days** |
| **Status updates** while we work a confirmed issue | at least every **7 business days** |
| **Fix or mitigation** for confirmed High/Critical issues | targeted within **90 days** of confirmation |

We follow a **coordinated-disclosure** model:

- We will work with you on a disclosure timeline and a mutually agreed publication
  date. Our default embargo is up to **90 days**, sooner if a fix ships earlier, and
  we may extend it for complex fixes — always in communication with you.
- We will publish a **GitHub Security Advisory** (and request a CVE where warranted)
  when the fix is released.
- We will **credit** you in the advisory and release notes unless you ask to remain
  anonymous. We do not currently operate a paid bug-bounty programme.
- We ask that you give us reasonable time to remediate before any public disclosure,
  and that your testing does not harm users, degrade the deployed service, or access
  data that is not yours.

## Supported versions

This is a single deployed service, not a versioned library. Security fixes land on
`main` and are deployed from it; there are no maintained release branches. If you
operate your own instance, track `main`.

## Scope

### In scope

Security issues in the parts of this repository the project maintains:

- **The ingest service** itself: `POST /v1/telemetry` (Bearer-token authentication,
  NDJSON parsing, strict v1 schema validation, batch/event deduplication) and the
  health endpoints.
- **The wire contract** (`docs/wire-contract.md`) — any way to make the service
  accept, store, or act on input outside the contract.
- **Retention and deletion**: the `POST /v1/telemetry/forget` endpoint, the
  forget-queue drain, and the daily maintenance job (partition pre-create, 90-day
  partition drop, dedup purge). A path where retention or a forget request is *not*
  honoured is an in-scope, high-priority report.
- **Privacy-relevant findings are explicitly welcome.** The service is designed to
  be privacy-allowlist-only by construction (see
  [`docs/privacy.md`](docs/privacy.md)): only the frozen `TelemetryEvent` fields can
  be stored, and the install id is the only row-level identifier. If you find a path
  where data outside that allowlist reaches storage or logs, or where stored data
  can be correlated back to a person, machine, or customer system, that is in scope
  and high priority.
- **The deployment and CI assets we publish** (`Dockerfile`, `deploy/` Bicep and
  `bootstrap.sql`, `.github/workflows/`) — a software-supply-chain surface.
  Examples: workflow injection via untrusted input, secret/token exfiltration, or
  image tampering.

### Out of scope

- **The engine-side telemetry transport** (the client that sends batches) — that
  code lives in the [engine repository](https://github.com/tomas-rampas/vouchfx);
  report it via the engine's
  [SECURITY.md](https://github.com/tomas-rampas/vouchfx/blob/main/SECURITY.md).
- **Vulnerabilities in upstream dependencies** themselves (ASP.NET Core, Npgsql,
  PostgreSQL, Testcontainers, etc.) — report those to the upstream project. If this
  service's *use* of a dependency is what creates the exposure, that is in scope.
- **An operator's own deployment configuration** — a weak token chosen by an
  operator, a mis-scoped Azure role assignment, or network rules outside the
  published Bicep. If the published defaults are what make the misconfiguration
  likely, that is in scope.
- Issues requiring a **malicious maintainer**, physical access to the database
  host, or an already-compromised cloud subscription.
- Reports generated solely by automated scanners with no demonstrated impact,
  best-practice/"hardening" suggestions with no concrete exploit, and social
  engineering of maintainers.

---

_This policy is a living document, aligned with the vouchfx engine's
[security policy](https://github.com/tomas-rampas/vouchfx/blob/main/SECURITY.md).
The disclosure SLAs will be confirmed as the project moves from adoption stage to a
stable v1 release._
