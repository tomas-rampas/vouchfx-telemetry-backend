#!/usr/bin/env python3
"""Build the vouchfx-telemetry-backend GitHub Pages site.

Copies the static landing page (site/) into the output directory, then renders
the repository's markdown — the user-facing telemetry overview, the privacy
policy, the wire contract, the architecture and operations references, and the
project documents — into styled HTML that matches the engine and provider-hub
sites. The markdown files remain the single source of truth; this generates
their HTML on every run, so a CI deploy keeps the published pages current with
every push.

The rendering machinery is shared with the other three vouchfx sites — see
https://github.com/tomas-rampas/vouchfx/tree/main/scripts/site-tools (the
vouchfx-site-tools package, vouchfx issue #200). This file only carries what
is specific to this repository's own site: the doc set and the page/portal
HTML. This repository's published site has always shipped
site/facts-fallback.json in its output (there was never an unlink step) —
delete_facts_fallback=False preserves that rather than silently changing it.

    python scripts/build_site.py [output_dir]   # default: _site

Requires: markdown, pygments, vouchfx-site-tools
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"


def _bootstrap_site_tools() -> None:
    """Resolve vouchfx_site_tools in four steps: (1) an already-installed
    package — this is what CI's pip install satisfies; (2) VOUCHFX_SITE_TOOLS,
    if set, pointing at a scripts/site-tools/src checkout; (3) the maintainer's
    usual local layout, all four repos checked out side by side. Each step is
    tried independently so a wrong VOUCHFX_SITE_TOOLS still falls through to
    the sibling checkout instead of failing outright."""
    try:
        import vouchfx_site_tools  # noqa: F401

        return
    except ImportError:
        pass

    env_path = os.environ.get("VOUCHFX_SITE_TOOLS")
    if env_path:
        sys.path.insert(0, env_path)
        try:
            import vouchfx_site_tools  # noqa: F401

            return
        except ImportError:
            sys.path.pop(0)

    sibling = (ROOT / ".." / "vouchfx" / "scripts" / "site-tools" / "src").resolve()
    sys.path.insert(0, str(sibling))
    try:
        import vouchfx_site_tools  # noqa: F401

        return
    except ImportError:
        sys.path.pop(0)

    raise SystemExit(
        "vouchfx-site-tools is not installed and no local checkout was found.\n"
        "Install it with:\n"
        '  pip install "vouchfx-site-tools @ git+https://github.com/tomas-rampas/vouchfx.git@<sha>'
        '#subdirectory=scripts/site-tools"\n'
        "(substitute <sha> for the pinned commit in .github/workflows/pages.yml), "
        "or set VOUCHFX_SITE_TOOLS to a local scripts/site-tools/src checkout, "
        "or check out vouchfx as a sibling of this repository."
    )


_bootstrap_site_tools()

from vouchfx_site_tools import SiteConfig, build  # noqa: E402

# Markdown files to render, in sidebar order. (source path relative to ROOT, nav group, label)
#
# SUPERSET INVARIANT: every source path listed here (and everything under
# docs/**/*.md, picked up automatically) must fall under one of the `paths:`
# globs in .github/workflows/pages.yml, or a change to that file will not
# trigger a rebuild. docs/** and the four root project documents already
# cover every entry below; if a new top-level doc is ever added outside those
# globs, the workflow's path list must grow to match.
DOCS: list[tuple[str, str, str]] = [
    # For users: the opt-in story and the privacy guarantees, in reading order.
    ("docs/why-telemetry.md", "For users", "Why telemetry, and how to opt in"),
    ("docs/privacy.md", "For users", "Privacy policy & data handling"),

    # Reference: the frozen wire contract and the system design behind it.
    ("docs/wire-contract.md", "Reference", "Wire contract"),
    ("docs/architecture.md", "Reference", "Architecture & system design"),

    # Operating: running a backend, on Azure or anywhere else.
    ("docs/operations.md", "Operating", "Operations runbook (Azure pilot)"),
    ("docs/self-hosting.md", "Operating", "Self-hosting without Azure"),

    # Project
    ("README.md", "Project", "Repository README"),
    ("CONTRIBUTING.md", "Project", "Contributing"),
    ("SECURITY.md", "Project", "Security policy"),
    ("CODE_OF_CONDUCT.md", "Project", "Code of conduct"),
]

# Any additional markdown that is link-reachable but not in the sidebar.
EXTRA: list[str] = []

# Markdown that must never be published, even when present on a maintainer's
# disk. Nothing in this repository is internal today — deploy/ is Bicep IaC
# and SQL, not markdown-rendered docs, and its own README files are reachable
# only as GitHub blob links via rewrite_links(), never rendered as pages. The
# mechanism is kept so an accidental future addition under docs/ fails safe
# the same way the engine and provider-hub sites do.
SKIP: set[str] = set()
SKIP_PREFIXES: tuple[str, ...] = ()

PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>{title} · vouchfx telemetry</title>
<meta name="description" content="{desc}" />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="{root}favicon.svg" type="image/svg+xml" />
<link rel="stylesheet" href="{root}styles.css" />
<link rel="stylesheet" href="{root}docs.css" />
<link rel="stylesheet" href="{root}pygments.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="{root}index.html" aria-label="vouchfx telemetry home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx telemetry</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="{root}index.html">Home</a>
      <a href="{root}docs.html">Docs</a>
      <a href="{root}docs/why-telemetry.html">Why telemetry</a>
      <a href="https://tomas-rampas.github.io/vouchfx/">Engine docs</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx-telemetry-backend" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="doc-shell">
  <aside class="doc-side">{sidebar}</aside>
  <main class="doc-main">
    <div class="doc-breadcrumb"><a href="{root}docs.html">Documentation</a> / {crumb}</div>
    <article class="prose">{body}</article>
  </main>
  <nav class="doc-toc"><h4>On this page</h4>{toc}</nav>
</div>
{mermaid_script}
</body>
</html>
"""

PORTAL = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>Documentation · vouchfx telemetry</title>
<meta name="description" content="vouchfx telemetry backend documentation — the opt-in overview, the privacy policy, the wire contract, architecture, and how to run or self-host the backend." />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="favicon.svg" type="image/svg+xml" />
<link rel="stylesheet" href="styles.css" />
<link rel="stylesheet" href="docs.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="index.html" aria-label="vouchfx telemetry home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx telemetry</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="index.html">Home</a>
      <a href="docs/why-telemetry.html">Why telemetry</a>
      <a href="https://tomas-rampas.github.io/vouchfx/">Engine docs</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx-telemetry-backend" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="container portal">
  <div class="portal__head">
    <p class="eyebrow">Documentation</p>
    <h1 class="section__title">Everything about vouchfx's opt-in telemetry.</h1>
    <p class="section__lede">These pages are rendered straight from the repository's markdown on every push,
      so they never drift from the service they describe.</p>
  </div>

  <section class="portal__group">
    <h2>For users</h2>
    <p>Should you opt in, what is collected, and your rights over your own data.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/why-telemetry.html">
        <span class="doc-card__k">START</span><h3>Why telemetry, and how to opt in</h3>
        <p>What is (and is never) collected, how to enable/disable, the local outbox, and the fail-silent guarantee.</p>
      </a>
      <a class="doc-card" href="docs/privacy.html">
        <span class="doc-card__k">POLICY</span><h3>Privacy policy &amp; data handling</h3>
        <p>The authoritative home of the 90-day retention window and the 30-day deletion commitment.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Reference</h2>
    <p>The frozen HTTP contract and the system design behind it.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/wire-contract.html">
        <span class="doc-card__k">API</span><h3>Wire contract</h3>
        <p>The <code>/v1/telemetry</code> and <code>/v1/telemetry/forget</code> endpoints, the allowlisted <code>TelemetryEvent</code> schema, dedup and rate limiting.</p>
      </a>
      <a class="doc-card" href="docs/architecture.html">
        <span class="doc-card__k">DESIGN</span><h3>Architecture &amp; system design</h3>
        <p>The five-component service, the partitioned PostgreSQL schema, and the Azure infrastructure topology.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Operating</h2>
    <p>Running a backend — on Azure, or anywhere else.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/operations.html">
        <span class="doc-card__k">RUNBOOK</span><h3>Operations runbook (Azure pilot)</h3>
        <p>Deployment via Bicep, GitHub Actions secrets, configuration reference, monitoring, and troubleshooting.</p>
      </a>
      <a class="doc-card" href="docs/self-hosting.html">
        <span class="doc-card__k">GUIDE</span><h3>Self-hosting without Azure</h3>
        <p>Docker, Docker Compose, and Kubernetes examples — bring your own PostgreSQL 16 and point the engine at it.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Project</h2>
    <p>How this repository is run.</p>
    <div class="doc-cards">
      <a class="doc-card" href="README.html"><span class="doc-card__k">README</span><h3>Repository README</h3><p>What the backend is, the repository layout, and the local build &amp; test commands.</p></a>
      <a class="doc-card" href="CONTRIBUTING.html"><span class="doc-card__k">HOW</span><h3>Contributing</h3><p>Where discussion belongs, the quality bar, and the two load-bearing contracts that change only deliberately.</p></a>
      <a class="doc-card" href="SECURITY.html"><span class="doc-card__k">SEC</span><h3>Security policy</h3><p>How to report a suspected vulnerability — always via private reporting, never a public issue.</p></a>
      <a class="doc-card" href="CODE_OF_CONDUCT.html"><span class="doc-card__k">CoC</span><h3>Code of conduct</h3><p>The standards this community holds itself to.</p></a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Ecosystem</h2>
    <p>This is the server half of a two-repository system, alongside the community provider hub and the sample applications.</p>
    <p class="note">Live: vouchfx engine {{fact:engine_release}} · <code>Vouchfx.Sdk</code> {{fact:sdk_version}} · community providers listed in the hub registry: {{fact:community_provider_count}} (latest: <code>Vouchfx.Community.JsonRpc</code> {{fact:community_jsonrpc_version}}).</p>
    <div class="doc-cards">
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx/" target="_blank" rel="noopener noreferrer">
        <span class="doc-card__k">ENGINE</span><h3>vouchfx project site</h3>
        <p>The engine that produces the telemetry this service ingests — its telemetry reference (docs/telemetry.html on the engine site) covers the CLI-side <code>vouchfx telemetry enable/disable</code> commands and configuration.</p>
      </a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-providers/" target="_blank" rel="noopener noreferrer">
        <span class="doc-card__k">HUB</span><h3>vouchfx providers</h3>
        <p>The community hub for vouchfx step providers — unrelated data path, same ecosystem.</p>
      </a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-samples/" target="_blank" rel="noopener noreferrer">
        <span class="doc-card__k">SAMPLES</span><h3>vouchfx-samples</h3>
        <p>Four production-grade sample applications with complete <code>.e2e.yaml</code> suites — the engine client that would, if you opt in, be the source of the events this service ingests.</p>
      </a>
    </div>
  </section>
</div>

<footer class="footer">
  <div class="container footer__inner">
    <div class="footer__brand">
      <span class="brand__mark" aria-hidden="true"></span>
      <div><strong>vouchfx telemetry</strong><p>Opt-in, allowlist-only-by-construction usage metrics for the vouchfx engine — this is the server half.</p></div>
    </div>
    <div class="footer__links">
      <a href="index.html">Home</a>
      <a href="https://github.com/tomas-rampas/vouchfx-telemetry-backend" target="_blank" rel="noopener noreferrer">Repository</a>
      <a href="https://tomas-rampas.github.io/vouchfx/" target="_blank" rel="noopener noreferrer">Engine docs</a>
      <a href="https://github.com/tomas-rampas/vouchfx-telemetry-backend/blob/main/LICENSE" target="_blank" rel="noopener noreferrer">Licence (Apache-2.0)</a>
    </div>
  </div>
</footer>
</body>
</html>
"""

CONFIG = SiteConfig(
    root=ROOT,
    default_repo="tomas-rampas/vouchfx-telemetry-backend",
    docs=DOCS,
    page_template=PAGE,
    portal_html=PORTAL,
    meta_description_prefix="vouchfx telemetry backend documentation",
    extra=EXTRA,
    skip=SKIP,
    skip_prefixes=SKIP_PREFIXES,
    delete_facts_fallback=False,
)


def main() -> None:
    build(CONFIG, OUT)


if __name__ == "__main__":
    main()
