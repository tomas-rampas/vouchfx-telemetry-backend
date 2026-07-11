#!/usr/bin/env python3
"""Build the vouchfx-telemetry-backend GitHub Pages site.

Copies the static landing page (site/) into the output directory, then renders
the repository's markdown — the user-facing telemetry overview, the privacy
policy, the wire contract, the architecture and operations references, and the
project documents — into styled HTML that matches the engine and provider-hub
sites. The markdown files remain the single source of truth; this generates
their HTML on every run, so a CI deploy keeps the published pages current with
every push.

    python scripts/build_site.py [output_dir]   # default: _site

Requires: markdown, pygments  (pip install markdown pygments)
"""
from __future__ import annotations

import html
import json
import os
import posixpath
import re
import shutil
import sys
import urllib.error
import urllib.request
from pathlib import Path

import markdown
from markdown.extensions.codehilite import CodeHiliteExtension
from markdown.extensions.toc import TocExtension
from pygments.formatters import HtmlFormatter

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "site"
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"

# Markdown files to render, in sidebar order. (source path relative to ROOT, nav group, label)
#
# SUPERSET INVARIANT: every source path listed here (and everything under
# docs/**/*.md, picked up automatically below) must fall under one of the
# `paths:` globs in .github/workflows/pages.yml, or a change to that file will
# not trigger a rebuild. docs/** and the four root project documents already
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


def out_path(rel: str) -> Path:
    """Mirror the repo layout under OUT, with .html extension."""
    return OUT / (rel[:-3] + ".html")


def rel_root(target: Path) -> str:
    """Relative path from a generated file back to OUT root, e.g. '../'.
    Forward slashes always, so Windows and CI builds emit identical HTML."""
    rp = os.path.relpath(OUT, target.parent).replace(os.sep, "/")
    return "" if rp == "." else rp + "/"


GITHUB_URL = f"https://github.com/{os.environ.get('GITHUB_REPOSITORY', 'tomas-rampas/vouchfx-telemetry-backend')}/"
ENGINE_SITE = "https://tomas-rampas.github.io/vouchfx/"
PUBLISHED: set[str] = set()


def compute_published() -> set[str]:
    rels = {rel for rel, _group, _label in DOCS} | set(EXTRA)
    for src in ROOT.glob("docs/**/*.md"):
        rel = src.relative_to(ROOT).as_posix()
        if rel not in SKIP and not rel.startswith(SKIP_PREFIXES):
            rels.add(rel)
    return rels


def rewrite_links(body: str, src_rel: str) -> str:
    """Rewrite relative links: published .md pages become .html; any other
    repo-relative target (including everything under deploy/) becomes an
    absolute GitHub URL, since it has no page on the site. Absolute URLs,
    anchors and mailto links pass through untouched."""
    src_dir = posixpath.dirname(src_rel)

    def repl(m: re.Match) -> str:
        href = m.group(1)
        if re.match(r"[a-z]+://", href) or href.startswith("#") or href.startswith("mailto:"):
            return m.group(0)
        path, sep, frag = href.partition("#")
        target = posixpath.normpath(posixpath.join(src_dir, path))
        if path.endswith(".md") and target in PUBLISHED:
            return f'href="{path[:-3] + ".html"}{sep}{frag}"'
        kind = "tree" if (ROOT / target).is_dir() else "blob"
        return f'href="{GITHUB_URL}{kind}/main/{target}{sep}{frag}"'

    return re.sub(r'href="([^"]+)"', repl, body)


def extract_mermaid(text: str) -> tuple[str, list[str]]:
    """Pull ```mermaid fenced blocks out before markdown processing."""
    blocks: list[str] = []

    def grab(m: re.Match) -> str:
        blocks.append(m.group(1))
        return f"\n@@MERMAID{len(blocks) - 1}@@\n"

    text = re.sub(r"```mermaid\r?\n(.*?)```", grab, text, flags=re.DOTALL)
    return text, blocks


def sidebar(active_rel: str, root: str) -> str:
    groups: dict[str, list[str]] = {}
    for rel, group, label in DOCS:
        href = root + rel[:-3] + ".html"
        cls = ' class="active"' if rel == active_rel else ""
        groups.setdefault(group, []).append(f'<a href="{href}"{cls}>{html.escape(label)}</a>')
    parts = [f'<a href="{root}docs.html">← All documentation</a>']
    for group, links in groups.items():
        parts.append(f"<h4>{html.escape(group)}</h4>")
        parts.extend(links)
    return "\n".join(parts)


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


def render_markdown(rel: str, label: str) -> None:
    src = ROOT / rel
    text = src.read_text(encoding="utf-8")
    text, mermaid = extract_mermaid(text)

    md = markdown.Markdown(
        extensions=[
            "extra",
            "sane_lists",
            "admonition",
            TocExtension(permalink=True, permalink_class="headerlink", permalink_title="", baselevel=2),
            CodeHiliteExtension(css_class="codehilite", guess_lang=False),
        ]
    )
    body = md.convert(text)
    body = rewrite_links(body, rel)

    # Re-insert mermaid blocks as divs.
    for i, block in enumerate(mermaid):
        body = body.replace(f"<p>@@MERMAID{i}@@</p>", f'<div class="mermaid">{html.escape(block)}</div>')
        body = body.replace(f"@@MERMAID{i}@@", f'<div class="mermaid">{html.escape(block)}</div>')

    toc = getattr(md, "toc", "") or ""
    has_mermaid = bool(mermaid)
    mermaid_script = (
        '<script type="module">import mermaid from "https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs";'
        'mermaid.initialize({startOnLoad:true,theme:"dark"});</script>'
        if has_mermaid
        else ""
    )

    dst = out_path(rel)
    dst.parent.mkdir(parents=True, exist_ok=True)
    root = rel_root(dst)
    desc = f"vouchfx telemetry backend documentation — {label}"
    dst.write_text(
        PAGE.format(
            title=html.escape(label),
            desc=html.escape(desc),
            root=root,
            sidebar=sidebar(rel, root),
            crumb=html.escape(label),
            body=body,
            toc=toc,
            mermaid_script=mermaid_script,
        ),
        encoding="utf-8",
    )
    print(f"  rendered {rel} -> {dst.relative_to(OUT)}")


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
    <p class="note">{facts_line}</p>
    <div class="doc-cards">
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx/" target="_blank" rel="noopener noreferrer">
        <span class="doc-card__k">ENGINE</span><h3>vouchfx project site</h3>
        <p>The engine that produces the telemetry this service ingests — its telemetry reference (docs/telemetry.html on the engine site) covers the CLI-side <code>vouchfx telemetry enable/disable</code> commands and configuration.</p>
      </a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-providers/" target="_blank" rel="noopener noreferrer">
        <span class="doc-card__k">HUB</span><h3>vouchfx providers</h3>
        <p>The community hub for vouchfx step providers — unrelated data path, same ecosystem.</p>
      </a>
      <a class="doc-card" href="https://github.com/tomas-rampas/vouchfx-samples" target="_blank" rel="noopener noreferrer">
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


def build_portal(facts: dict[str, str]) -> None:
    # Plain strings, deliberately not f-strings: an f-string would collapse
    # the doubled braces below to a single brace before substitute_facts()
    # ever sees them, silently defeating the {{fact:KEY}} substitution.
    facts_line = (
        "Live: vouchfx engine {{fact:engine_release}} · "
        "<code>Vouchfx.Sdk</code> {{fact:sdk_version}} · "
        "community providers listed in the hub registry: {{fact:community_provider_count}} "
        "(latest: <code>Vouchfx.Community.JsonRpc</code> {{fact:community_jsonrpc_version}})."
    )
    (OUT / "docs.html").write_text(PORTAL.format(facts_line=facts_line), encoding="utf-8")
    print("  built docs.html portal")


def derive_label(src: Path) -> str:
    """Best-effort page label from the first heading, else the file stem."""
    for line in src.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return src.stem


# ===================== Fact injection =====================
#
# A handful of pages quote live ecosystem numbers (the current engine release,
# the current Vouchfx.Sdk / Vouchfx.Community.JsonRpc NuGet versions, how many
# community providers are listed). Re-typing those by hand goes stale the
# moment the engine or the provider hub ships again. Instead, every HTML file
# in the output may contain a `{{fact:KEY}}` token; fetch_facts() resolves
# each KEY independently from a live source with a short timeout, falls back
# per-key to the checked-in site/facts-fallback.json on any failure (network
# error, non-200, malformed JSON, missing key), and never raises — a totally
# offline build still produces a complete, correct site from the fallback.
FACT_KEYS: list[str] = [
    "engine_release",
    "sdk_version",
    "community_jsonrpc_version",
    "community_provider_count",
]


def _fetch_json(url: str, timeout: float = 5.0) -> object:
    headers = {"User-Agent": "vouchfx-telemetry-pages-build"}
    if "github.com" in url:
        token = os.environ.get("GITHUB_TOKEN")
        if token:
            headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310 (fixed https:// literals below)
        return json.loads(resp.read().decode("utf-8"))


def fetch_facts() -> dict[str, str]:
    """Best-effort live facts for {{fact:KEY}} substitution. See module notes
    above. Prints a live-vs-fallback summary; never raises."""
    live: dict[str, str] = {}

    try:
        releases = _fetch_json("https://api.github.com/repos/tomas-rampas/vouchfx/releases")
        # Deliberately keeps pre-releases (the alpha series IS the release line
        # today); only drafts are skipped. Do not add a prerelease filter.
        release = next(r for r in releases if not r.get("draft"))  # type: ignore[union-attr]
        live["engine_release"] = release["tag_name"]
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError, StopIteration, TypeError, AttributeError) as exc:
        print(f"  fact engine_release: live fetch failed ({exc}); using fallback")

    try:
        idx = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.sdk/index.json")
        live["sdk_version"] = idx["versions"][-1]  # type: ignore[index]
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError, IndexError, TypeError) as exc:
        print(f"  fact sdk_version: live fetch failed ({exc}); using fallback")

    try:
        idx = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.community.jsonrpc/index.json")
        live["community_jsonrpc_version"] = idx["versions"][-1]  # type: ignore[index]
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError, IndexError, TypeError) as exc:
        print(f"  fact community_jsonrpc_version: live fetch failed ({exc}); using fallback")

    try:
        registry = _fetch_json(
            "https://raw.githubusercontent.com/tomas-rampas/vouchfx-providers/main/registry/community-providers.json"
        )
        if not isinstance(registry, list):
            raise ValueError("registry JSON is no longer a top-level array")
        live["community_provider_count"] = str(len(registry))
    except (urllib.error.URLError, TimeoutError, ValueError, TypeError) as exc:
        print(f"  fact community_provider_count: live fetch failed ({exc}); using fallback")

    fallback_path = SITE / "facts-fallback.json"
    fallback: dict[str, str] = {}
    if fallback_path.exists():
        try:
            fallback = json.loads(fallback_path.read_text(encoding="utf-8"))
        except (ValueError, OSError) as exc:
            print(f"  facts-fallback.json unreadable ({exc}); fallback disabled")

    result: dict[str, str] = {}
    print("facts:")
    for key in FACT_KEYS:
        if key in live:
            result[key] = live[key]
            print(f"  {key}: {live[key]} (live)")
        elif key in fallback:
            result[key] = fallback[key]
            print(f"  {key}: {fallback[key]} (fallback)")
        else:
            result[key] = f"?{key}?"
            print(f"  {key}: MISSING — no live value and no fallback entry")
    return result


def substitute_facts(facts: dict[str, str]) -> None:
    """Replace every {{fact:KEY}} token in every generated HTML file."""

    def repl(m: re.Match) -> str:
        key = m.group(1)
        # html.escape defensively (values are version strings today) and keep
        # unknown tokens literal so a typo is visible in the output.
        return html.escape(facts[key]) if key in facts else m.group(0)

    pattern = re.compile(r"\{\{fact:(\w+)\}\}")
    for html_file in OUT.glob("**/*.html"):
        text = html_file.read_text(encoding="utf-8")
        if "{{fact:" not in text:
            continue
        html_file.write_text(pattern.sub(repl, text), encoding="utf-8")


def main() -> None:
    # Safety: only ever build into a subdirectory of the repo, never ROOT or an
    # outside path — main() removes OUT with rmtree before rebuilding.
    if OUT == ROOT or ROOT not in OUT.parents:
        raise SystemExit(f"refusing to build into {OUT}: must be a subdirectory of {ROOT}")
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SITE, OUT)
    print(f"copied {SITE.relative_to(ROOT)}/ -> {OUT.name}/")

    # Pygments stylesheet (dark) for fenced code blocks.
    (OUT / "pygments.css").write_text(
        HtmlFormatter(style="monokai").get_style_defs(".codehilite") + "\n.codehilite{background:transparent}",
        encoding="utf-8",
    )

    PUBLISHED.update(compute_published())

    rendered: set[str] = set()
    for rel, _group, label in DOCS:
        render_markdown(rel, label)
        rendered.add(rel)
    for rel in EXTRA:
        if (ROOT / rel).exists():
            render_markdown(rel, derive_label(ROOT / rel))
            rendered.add(rel)

    # Auto-render any markdown under docs/ not explicitly listed, so a newly
    # added file is published (linkable) rather than silently omitted.
    for src in sorted(ROOT.glob("docs/**/*.md")):
        rel = src.relative_to(ROOT).as_posix()
        if rel in rendered or rel in SKIP or rel.startswith(SKIP_PREFIXES):
            continue
        print(f"  (auto) {rel} not in DOCS — rendering with derived label")
        render_markdown(rel, derive_label(src))
        rendered.add(rel)

    facts = fetch_facts()
    build_portal(facts)
    substitute_facts(facts)
    print(f"done -> {OUT}")


if __name__ == "__main__":
    main()
