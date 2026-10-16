#!/usr/bin/env python3
"""Manual, isolated GitHub-hosted acceptance of the production telemetry image."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import selectors
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

LABEL = "io.vouchfx.telemetry-acceptance"
REPOSITORY = "tomas-rampas/vouchfx-telemetry-backend"
CORE = "tomas-rampas/vouchfx"

MAX_STREAM_BYTES = 1024 * 1024
MAX_COMMAND_BYTES = 1536 * 1024
MAX_CAPTURE_BYTES = 40 * 1024 * 1024
CLEANUP_RESERVE_BYTES = 8 * 1024 * 1024
CAPTURE_CHUNK_BYTES = 64 * 1024
MAX_COMMANDS = 640
CLEANUP_RESERVE_COMMANDS = 128
MAX_ARTIFACT_FILE_BYTES = 4 * 1024 * 1024
MAX_ARTIFACT_BYTES = 64 * 1024 * 1024
MAX_ARTIFACT_FILES = 1024
MAX_MANIFEST_BYTES = 512 * 1024


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


class RefuseMetadataRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError("GitHub metadata redirects are refused")


def github_metadata(path, token):
    require(bool(token) and not any(c.isspace() for c in token), "Require the workflow GitHub metadata token")
    require(re.fullmatch(r"/releases/tags/v[0-9]+\.[0-9]+\.[0-9]+|/git/ref/tags/v[0-9]+\.[0-9]+\.[0-9]+|/git/tags/[0-9a-f]{40}", path)
            is not None, "Unexpected GitHub metadata path")
    request = urllib.request.Request("https://api.github.com/repos/" + CORE + path,
                                     headers={"User-Agent": "vouchfx-selfhosted-acceptance"})
    request.add_unredirected_header("Authorization", "Bearer " + token)
    # This opener is never used for public package downloads or backend requests.
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}), RefuseMetadataRedirects())
    with http.open(request, timeout=30) as response:
        payload = response.read(2 * 1024 * 1024 + 1)
        require(len(payload) <= 2 * 1024 * 1024, "Oversized public metadata response")
        return json.loads(payload)


def write_json(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def terminate_owned_group(process, requested_signal):
    # The caller created this child with start_new_session=True. Never target
    # another process group or act after the retained child has exited.
    if process.poll() is None:
        try:
            os.killpg(process.pid, requested_signal)
        except ProcessLookupError:
            pass  # It exited between the liveness check and signal delivery.


class Acceptance:
    def __init__(self, args):
        # Consume the step-only token before launching any Docker, dotnet or CLI child.
        self.github_token = os.environ.pop("VOUCHFX_ACCEPTANCE_GITHUB_TOKEN", "")
        require(bool(self.github_token) and not any(c.isspace() for c in self.github_token),
                "Require the workflow GitHub metadata token")
        self.args = args
        self.root = Path(args.evidence_dir).resolve()
        temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
        require(self.root.is_relative_to(temporary) and self.root != temporary, "Evidence must be below RUNNER_TEMP")
        require(not self.root.exists(), "Use a fresh evidence directory")
        self.root.mkdir()
        self.private = self.root / "private"
        self.private.mkdir(mode=0o700)
        self.logs = self.root / "logs"
        self.logs.mkdir()
        self.owner = uuid.uuid4().hex
        self.prefix = "vfxtel-accept-" + self.owner
        self.token, self.password = secrets.token_hex(32), secrets.token_hex(32)
        self.secrets = [self.token, self.password, self.github_token]
        self.commands = []
        self.capture_bytes = 0
        self.cleaning = False
        self.publication_safe = True
        self.owned_images = set()
        self.image_recovery_started = False
        self.image_recovery_complete = True
        self.network = None
        self.image = None
        self.containers = {}
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def command(self, argv, timeout=30, env=None, check=True):
        # Pipes are drained concurrently; raw bytes never enter an upload path.
        limit = MAX_CAPTURE_BYTES if self.cleaning else MAX_CAPTURE_BYTES - CLEANUP_RESERVE_BYTES
        command_limit = MAX_COMMANDS if self.cleaning else MAX_COMMANDS - CLEANUP_RESERVE_COMMANDS
        require(len(self.commands) < command_limit, "Command evidence count exceeded its bound")
        number = len(self.commands)
        raw_paths = [self.private / f"{number:03d}.stdout.raw", self.private / f"{number:03d}.stderr.raw"]
        public_paths = [self.logs / f"{number:03d}.stdout", self.logs / f"{number:03d}.stderr"]
        started = time.monotonic()
        process, collector, original_error = None, None, None
        counts, timed_out, overflow, incomplete = [0, 0], False, False, False
        eof_observed = [False, False]
        stop_at, exited_at, killed, code = None, None, False, None
        closed, capture_finalized = True, False
        publication_before = self.publication_safe
        self.publication_safe = False  # Restore only after complete sanitized finalization.
        try:
            with raw_paths[0].open("xb") as out, raw_paths[1].open("xb") as err:
                process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                           env=env, start_new_session=True)
                closed = False
                collector = selectors.DefaultSelector()
                for index, pipe in enumerate([process.stdout, process.stderr]):
                    os.set_blocking(pipe.fileno(), False)
                    collector.register(pipe, selectors.EVENT_READ, (index, [out, err][index]))
                while collector.get_map() or process.poll() is None:
                    now = time.monotonic()
                    if now - started >= timeout:
                        timed_out = True
                    if (timed_out or overflow) and stop_at is None:
                        stop_at = now
                        terminate_owned_group(process, signal.SIGTERM)
                    if stop_at is not None and now - stop_at >= 5 and not killed:
                        terminate_owned_group(process, signal.SIGKILL)
                        killed = True
                    if stop_at is not None and now - stop_at >= 10:
                        incomplete = True
                        break
                    if process.poll() is not None:
                        if exited_at is None:
                            exited_at = now
                        if now - exited_at >= 5 and collector.get_map():
                            incomplete = True  # An inherited pipe did not close; never admit it.
                            break
                    for key, _ in collector.select(timeout=0.1):
                        try:
                            chunk = os.read(key.fileobj.fileno(), CAPTURE_CHUNK_BYTES)
                        except BlockingIOError:
                            continue
                        if not chunk:
                            eof_observed[key.data[0]] = True
                            collector.unregister(key.fileobj)
                            key.fileobj.close()
                            continue
                        index, destination = key.data
                        remaining = min(MAX_STREAM_BYTES - counts[index],
                                        MAX_COMMAND_BYTES - sum(counts), limit - self.capture_bytes)
                        retained = chunk[:max(0, remaining)]
                        destination.write(retained)
                        counts[index] += len(retained)
                        self.capture_bytes += len(retained)
                        if len(retained) != len(chunk):
                            overflow = True
                code = process.wait(timeout=5)
                closed = True
        except BaseException as error:
            original_error = error
        finally:
            # This path also runs on KeyboardInterrupt and on wait/reap failures.
            try:
                if process is not None and process.poll() is None:
                    terminate_owned_group(process, signal.SIGKILL)
                    process.wait(timeout=5)
                closed = process is None or process.poll() is not None
                if process is not None:
                    code = process.returncode
            except BaseException as error:
                closed = False
                if original_error is None:
                    original_error = error
            finally:
                # Closing readers is not evidence that either stream reached EOF.
                incomplete = incomplete or not all(eof_observed)
                try:
                    if collector is not None:
                        incomplete = incomplete or bool(collector.get_map())
                        collector.close()
                    if process is not None:
                        for pipe in [process.stdout, process.stderr]:
                            if pipe is not None:
                                pipe.close()
                except BaseException as error:
                    closed = False
                    if original_error is None:
                        original_error = error
            publication_candidate = publication_before and closed and not incomplete
            record = {"argv": [self.redact(argument) for argument in argv], "integerExit": code,
                      "launched": process is not None, "timedOut": timed_out, "outputOverflow": overflow,
                      "childReaped": closed, "pipeDrainComplete": not incomplete,
                      "streamEofObserved": eof_observed,
                      "errorType": type(original_error).__name__ if original_error is not None else None,
                      "seconds": round(time.monotonic() - started, 3), "retainedBytes": counts,
                      "streamByteLimit": MAX_STREAM_BYTES, "commandByteLimit": MAX_COMMAND_BYTES,
                      "runCaptureByteLimit": MAX_CAPTURE_BYTES, "rawCaptureUploadEligible": False}
            self.commands.append(record)
            try:
                for raw, public in zip(raw_paths, public_paths):
                    if raw.exists():
                        with raw.open("rb") as source:
                            bounded = source.read(MAX_STREAM_BYTES + 1)
                        require(len(bounded) <= MAX_STREAM_BYTES, "Raw capture exceeded its bound")
                        with public.open("xb") as output:
                            output.write(self.redact(bounded))
                capture_finalized = True
            finally:
                record["sanitizedCaptureFinalized"] = capture_finalized
                write_json(self.logs / f"{number:03d}.command.json", record)
                self.publication_safe = publication_candidate and capture_finalized
        if original_error is not None:
            raise original_error
        require(closed and not incomplete and not overflow, f"Command {number} failed its bounded capture/closure contract")
        if check:
            require(code == 0 and not timed_out, f"Command {number} failed; inspect its recorded exit and logs")
        with public_paths[0].open("rb") as source:
            return source.read(MAX_STREAM_BYTES + 1).decode("utf-8", errors="replace").strip()

    def redact(self, value):
        for secret in self.secrets:
            value = value.replace(secret.encode() if isinstance(value, bytes) else secret,
                                  b"<redacted>" if isinstance(value, bytes) else "<redacted>")
        return value

    def publish_artifacts(self):
        # Finalize every eligible byte before atomically exposing the upload tree.
        require(self.publication_safe, "Unsafe command closure/capture: evidence publication refused")
        candidates = sorted([p for p in self.root.iterdir() if p.suffix in {".json", ".ndjson", ".xml"}]
                            + list(self.logs.iterdir()))
        require(len(candidates) <= MAX_ARTIFACT_FILES, "Too many evidence files")
        stage = self.private / "sanitized-publication"
        stage.mkdir()
        total, manifest = 0, []
        for source in candidates:
            require(source.is_file() and not source.is_symlink(), "Refuse non-regular evidence")
            with source.open("rb") as stream:
                raw = stream.read(MAX_ARTIFACT_FILE_BYTES + 1)
            require(len(raw) <= MAX_ARTIFACT_FILE_BYTES, "Evidence file exceeds its publication limit")
            sanitized = self.redact(raw)
            total += len(sanitized)
            require(total <= MAX_ARTIFACT_BYTES, "Evidence exceeds its total publication limit")
            relative = source.relative_to(self.root)
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as stream:
                stream.write(sanitized)
            manifest.append({"path": relative.as_posix(), "bytes": len(sanitized),
                             "sha256": hashlib.sha256(sanitized).hexdigest()})
        manifest_bytes = (json.dumps({"sanitized": True, "files": manifest,
                          "payloadBytes": total, "totalByteLimit": MAX_ARTIFACT_BYTES,
                          "fileByteLimit": MAX_ARTIFACT_FILE_BYTES, "fileCountLimit": MAX_ARTIFACT_FILES,
                          "manifestByteLimit": MAX_MANIFEST_BYTES, "rawCaptureIncluded": False,
                          "acceptancePassed": (self.root / "acceptance.json").is_file()}, indent=2) + "\n").encode()
        require(len(manifest_bytes) <= MAX_MANIFEST_BYTES and total + len(manifest_bytes) <= MAX_ARTIFACT_BYTES,
                "Final artifact including its manifest exceeds its bound")
        with (stage / "artifact-manifest.json").open("xb") as stream:
            stream.write(manifest_bytes)
        stage.rename(self.root / "published")

    def docker(self, *args, **kwargs):
        return self.command(["docker", *args], **kwargs)

    def snapshot(self):
        resources = {}
        for kind, listing, fields in [
            ("containers", ["ps", "-aq", "--no-trunc"], ["Id", "Created", "Image", "Name", "State.Pid", "State.Running", "NetworkSettings.Networks"]),
            ("networks", ["network", "ls", "-q", "--no-trunc"], ["Id", "Created", "Name", "Driver", "Containers"]),
        ]:
            ids = self.docker(*listing).splitlines()
            template = "[" + ",".join("{{json ." + field + "}}" for field in fields) + "]"
            rows = []
            for identifier in sorted(ids):
                args = ["inspect", "--format", template, identifier] if kind == "containers" else ["network", "inspect", "--format", template, identifier]
                rows.append(json.loads(self.docker(*args)))
            resources[kind] = rows
        resources["volumes"] = []
        for name in sorted(self.docker("volume", "ls", "-q").splitlines()):
            template = "[{{json .Name}},{{json .CreatedAt}},{{json .Driver}},{{json .Mountpoint}},{{json .Scope}}]"
            resources["volumes"].append(json.loads(self.docker("volume", "inspect", "--format", template, name)))
        resources["images"] = {}
        for identifier in sorted(set(self.docker("image", "ls", "-q", "--no-trunc").splitlines())):
            template = "[{{json .Id}},{{json .RepoTags}},{{json .RepoDigests}}]"
            row = json.loads(self.docker("image", "inspect", "--format", template, identifier))
            resources["images"][row[0]] = {"tags": sorted(row[1] or []), "digests": sorted(row[2] or [])}
        return resources

    def owned(self, kind, identifier):
        require(bool(re.fullmatch(r"(?:sha256:)?[0-9a-f]{64}", identifier)), "Refuse non-exact Docker identity")
        template = "{{index .Config.Labels \"" + LABEL + "\"}}" if kind != "network" else "{{index .Labels \"" + LABEL + "\"}}"
        args = ["inspect", "--format", template, identifier] if kind == "container" else [kind, "inspect", "--format", template, identifier]
        require(self.docker(*args) == self.owner, "Refuse an unowned Docker object")

    def create_container(self, name, *args):
        cid = self.private / (name + ".cid")
        try:
            self.docker("create", "--cidfile", str(cid), "--name", self.prefix + "-" + name,
                        "--label", LABEL + "=" + self.owner, "--network", self.network, *args)
        finally:
            # Docker writes the exact identity even if later create/setup work fails.
            if cid.is_file() and not cid.is_symlink():
                identifier = cid.read_text().strip()
                self.owned("container", identifier)
                self.containers[name] = identifier
        require(name in self.containers, "Missing owned container identity")
        self.docker("start", self.containers[name])

    def request(self, path, body=None, token=None, key=None, content_type="application/x-ndjson"):
        headers = {}
        if body is not None:
            headers["Content-Type"] = content_type
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        if key is not None:
            headers["Idempotency-Key"] = key
        request = urllib.request.Request(self.endpoint + path, data=body, headers=headers)
        try:
            with self.http.open(request, timeout=5) as response:
                status, payload = response.status, response.read(8193)
        except urllib.error.HTTPError as response:
            status, payload = response.code, response.read(8193)
        require(len(payload) <= 8192, "Oversized HTTP response")
        write_json(self.logs / f"http-{time.monotonic_ns()}.json",
                   {"path": path, "status": status, "authorizationPresent": token is not None,
                    "idempotencyKey": key, "responseBody": payload.decode("utf-8", errors="replace")})
        return status

    def backend_identity(self):
        require(self.containers.get("backend") == self.listener_container, "Owned backend identity changed")
        self.owned("container", self.listener_container)
        return self.listener_container

    def refresh_endpoint(self, phase):
        identifier = self.backend_identity()
        ports = json.loads(self.docker("inspect", "--format", "{{json .NetworkSettings.Ports}}", identifier))
        bindings = ports.get("8080/tcp") if isinstance(ports, dict) else None
        require(isinstance(bindings, list) and len(bindings) == 1, "Require one backend listener binding")
        binding = bindings[0]
        require(isinstance(binding, dict) and set(binding) == {"HostIp", "HostPort"}
                and binding["HostIp"] == "127.0.0.1", "Listener is not loopback-only")
        port = binding["HostPort"]
        require(isinstance(port, str) and re.fullmatch(r"[0-9]{1,5}", port) is not None
                and 1 <= int(port) <= 65535, "Invalid backend listener port")
        previous = getattr(self, "endpoint", None)
        self.endpoint = "http://127.0.0.1:" + str(int(port))
        write_json(self.root / ("listener-" + phase + ".json"),
                   {"phase": phase, "ownedContainer": identifier, "previousEndpoint": previous,
                    "endpoint": self.endpoint, "portChanged": previous is not None and previous != self.endpoint})

    def restart_backend(self):
        identifier = self.backend_identity()
        self.docker("restart", "--time", "10", identifier, timeout=30)
        self.refresh_endpoint("restarted")
        self.wait_ready("restarted")

    def wait_ready(self, phase):
        deadline = time.monotonic() + 90
        attempts, last_failure = 0, None
        def diagnostic(ready):
            write_json(self.root / ("readiness-" + phase + ".json"),
                       {"phase": phase, "ownedContainer": self.listener_container, "endpoint": self.endpoint,
                        "ready": ready, "timeoutSeconds": 90, "attempts": attempts, "lastHttpFailure": last_failure})
        while time.monotonic() < deadline:
            attempts += 1
            path = "/readyz"
            try:
                status = self.request(path)
                if status == 200:
                    path = "/healthz"
                    status = self.request(path)
                    if status != 200:
                        last_failure = {"path": path, "status": status}
                        diagnostic(False)
                    require(status == 200, "Liveness failed")
                    diagnostic(True)
                    return
                last_failure = {"path": path, "status": status}
            except (OSError, urllib.error.URLError) as error:
                last_failure = {"path": path, "errorType": type(error).__name__}
            time.sleep(1)
        diagnostic(False)
        raise RuntimeError("Production listener did not become ready within 90 seconds")

    def sql(self, statement):
        self.owned("container", self.containers["postgres"])
        return self.docker("exec", self.containers["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                           "-U", "postgres", "-d", "vfxtel", "-tA", "-c", statement, timeout=15)

    def rows(self, install_id):
        uuid.UUID(install_id)
        text = self.sql("SELECT row_to_json(t) FROM (SELECT * FROM telemetry_event WHERE install_id='" + install_id + "' ORDER BY event_timestamp) t;")
        return [json.loads(line) for line in text.splitlines() if line]

    def compare_row(self, event, row):
        names = {"schemaVersion": "schema_version", "installId": "install_id", "toolVersion": "tool_version",
                 "engineVersion": "engine_version", "dotnetVersion": "dotnet_version", "runCount": "run_count",
                 "scenarioCount": "scenario_count", "stepFamilies": "step_families", "stepProviders": "step_providers",
                 "startupMs": "startup_ms", "timeToFirstTestMs": "time_to_first_test_ms", "skippedEventLines": "skipped_event_lines"}
        for key, column in names.items():
            require(event[key] == row[column], "Persisted payload differs at " + key)
        for prefix, key in [("step", "stepVerdicts"), ("scenario", "scenarioVerdicts")]:
            for verdict, suffix in [("pass", "pass"), ("fail", "fail"), ("envError", "env_error"), ("inconclusive", "inconclusive")]:
                require(event[key][verdict] == row[prefix + "_" + suffix], "Persisted verdict differs")
        stamp = lambda value: dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(stamp(event["timestamp"]) == stamp(row["event_timestamp"]), "Persisted timestamp differs")

    def public_cli(self):
        version, commit = self.args.core_version, self.args.core_commit
        def api(path):
            return github_metadata(path, self.github_token)
        release = api("/releases/tags/v" + version)
        require(not release["draft"] and not release["prerelease"] and release["published_at"], "Require a stable published Core release")
        reference = api("/git/ref/tags/v" + version)
        obj = reference["object"]
        for _ in range(3):
            if obj["type"] == "commit":
                break
            require(obj["type"] == "tag", "Unexpected public tag object")
            obj = api("/git/tags/" + obj["sha"])["object"]
        require(obj["type"] == "commit" and obj["sha"] == commit, "Public tag differs from the accepted commit")
        package = self.private / ("vouchfx." + version + ".nupkg")
        url = "https://api.nuget.org/v3-flatcontainer/vouchfx/" + version + "/" + package.name
        size = 0
        download_deadline = time.monotonic() + 240
        with self.http.open(url, timeout=60) as response, package.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                require(time.monotonic() < download_deadline, "Public package download exceeded 240 seconds")
                size += len(chunk)
                require(size <= 300 * 1024 * 1024, "Public package exceeds its bounded download cap")
                output.write(chunk)
        with package.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        require(digest == self.args.core_package_sha256, "Official NuGet bytes differ from the independently accepted package")
        with zipfile.ZipFile(package) as archive:
            require(archive.getinfo("vouchfx.nuspec").file_size <= 2 * 1024 * 1024, "Oversized package metadata")
            nuspec = ET.fromstring(archive.read("vouchfx.nuspec"))
        metadata = next(node for node in nuspec if node.tag.split("}")[-1] == "metadata")
        fields = {node.tag.split("}")[-1]: node for node in metadata}
        require(fields["id"].text == "vouchfx" and fields["version"].text == version, "Wrong public tool package identity")
        require(fields["repository"].get("commit") == commit, "NuGet repository commit differs from the accepted public tuple")
        require(fields["repository"].get("url", "").rstrip("/").lower() == "https://github.com/" + CORE, "Wrong package repository")
        write_json(self.root / "public-cli-tuple.json", {"version": version, "commit": commit, "packageSha256": digest,
                   "packageBytes": size, "release": release, "tagReference": reference, "repositoryCommit": fields["repository"].get("commit")})
        self.home = self.private / "cli-home"
        config_home = self.home / "config"
        config_home.mkdir(parents=True)
        self.cli_env = os.environ.copy()
        for key in list(self.cli_env):
            if key.lower().endswith("_proxy") or key.startswith("VOUCHFX_TELEMETRY_") or key == "VOUCHFX_NO_TELEMETRY":
                self.cli_env.pop(key)
        self.cli_env.update(HOME=str(self.home), XDG_CONFIG_HOME=str(config_home), DOTNET_CLI_HOME=str(self.home),
                            NUGET_PACKAGES=str(self.private / "nuget-cache"), DOTNET_CLI_TELEMETRY_OPTOUT="1",
                            VOUCHFX_TELEMETRY_ENDPOINT=self.endpoint, VOUCHFX_TELEMETRY_TOKEN=self.token)
        self.state_dir = config_home / "vouchfx"
        feed = self.private / "nuget.config"
        feed.write_text('<configuration><packageSources><clear/><add key="accepted-public" value="' + str(self.private) + '"/></packageSources></configuration>')
        tools = self.private / "tools"
        self.command(["dotnet", "tool", "install", "vouchfx", "--version", version, "--tool-path", str(tools),
                      "--configfile", str(feed), "--no-cache"], timeout=180, env=self.cli_env)
        self.cli = str(tools / "vouchfx")
        require(self.command([self.cli, "--version"], env=self.cli_env) == version + "+" + commit, "Installed CLI version/source mismatch")

    def run_cli(self, token, label):
        env = self.cli_env.copy()
        if token is None:
            env.pop("VOUCHFX_TELEMETRY_TOKEN", None)
        else:
            env["VOUCHFX_TELEMETRY_TOKEN"] = token
        junit = self.root / (label + ".junit.xml")
        self.command([self.cli, "run", str(self.suite), "--fail-on-env-error", "--fail-on-inconclusive", "--junit", str(junit)],
                     timeout=180, env=env)
        tests = ET.parse(junit).getroot()
        suites = [tests] if tests.tag == "testsuite" else list(tests.iter("testsuite"))
        require(suites and sum(int(s.get("tests", "0")) for s in suites) == 1, "Require one real business assertion")
        require(all(int(s.get(key, "0")) == 0 for s in suites for key in ["failures", "errors", "skipped"]), "Business assertion was not an unskipped PASS")

    def exercise(self):
        fixture = json.loads(Path("tests/Vouchfx.Telemetry.Backend.UnitTests/Fixtures/event-line-v2.json").read_bytes())
        fixture["timestamp"] = dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds")
        fixture["installId"] = str(uuid.uuid4())
        body = json.dumps(fixture, separators=(",", ":")).encode()
        key = hashlib.sha256(body).hexdigest()
        for token in [None, "not-the-ingest-token"]:
            require(self.request("/v1/telemetry", body, token, key) == 401, "Unauthorized ingest was accepted")
        require(not self.rows(fixture["installId"]), "Unauthorized ingest persisted a row")
        for _ in range(2):
            require(self.request("/v1/telemetry", body, self.token, key) == 200, "Schema2 fixture ingest/replay failed")
        rows = self.rows(fixture["installId"])
        require(len(rows) == 1, "Fixture replay duplicated the persisted row")
        self.compare_row(fixture, rows[0])
        canary = "TELEMETRY_PRIVACY_CANARY_" + self.owner
        bad = dict(fixture, suiteName=canary)
        bad_body = json.dumps(bad).encode()
        require(self.request("/v1/telemetry", bad_body, self.token, hashlib.sha256(bad_body).hexdigest()) == 400,
                "Known schema2 accepted an unallowlisted field")
        require(len(self.rows(fixture["installId"])) == 1, "Refused privacy payload persisted data")
        write_json(self.root / "fixture-schema2.json", fixture)
        self.public_cli()
        self.command([self.cli, "telemetry", "enable"], env=self.cli_env)
        consent = self.state_dir / "telemetry.json"
        require(consent.is_file() and not consent.is_symlink(), "CLI did not use the isolated consent directory")
        install_id = json.loads(consent.read_bytes())["installId"]
        uuid.UUID(install_id)
        self.suite = self.private / (canary + ".e2e.yaml")
        body = ('var client = new System.Net.Http.HttpClient();\ntry\n{\n'
                + '    var response = await client.GetAsync("' + self.endpoint + '/readyz");\n'
                + '    try { if (!response.IsSuccessStatusCode) throw new System.InvalidOperationException("Backend readiness assertion failed"); }\n'
                + '    finally { response.Dispose(); }\n'
                + '    System.Console.WriteLine("' + canary + '");\n}\nfinally { client.Dispose(); }')
        self.suite.write_text('metadata:\n  name: ' + canary + '\nsteps:\n  - id: proof\n    type: script.csharp\n    code: |\n'
                              + ''.join('      ' + line + '\n' for line in body.splitlines()))
        # Capture locally without a transport: an HTTP 401 is a permanent refusal
        # in the public CLI and correctly discards its batch.
        self.run_cli(None, "producer-local-retained")
        outbox = self.state_dir / "telemetry-outbox.jsonl"
        lines = outbox.read_bytes().splitlines()
        require(len(lines) == 1, "Require one retained real engine telemetry event")
        event = json.loads(lines[0])
        require(set(event) == set(fixture) and event["schemaVersion"] == 2, "Real engine schema2 allowlist differs")
        require(event["installId"] == install_id and event["toolVersion"] == self.args.core_version
                and event["engineVersion"] == self.args.core_version, "Wrong real producer identity")
        require(canary.encode() not in lines[0] and self.endpoint.encode() not in lines[0], "Suite/source/endpoint data leaked into real telemetry")
        require(not self.rows(install_id), "The local-only CLI run persisted remote data")
        (self.root / "engine-generated-schema2.ndjson").write_bytes(lines[0] + b"\n")
        self.run_cli(self.token, "producer-delivered")
        require(not outbox.read_bytes().strip(), "Actual CLI transport did not drain its retained batch")
        rows = self.rows(install_id)
        require(len(rows) == 2 and all(row["schema_version"] == 2 and row["tool_version"] == self.args.core_version
                and row["engine_version"] == self.args.core_version for row in rows), "Real CLI did not persist both final-version schema2 events")
        self.compare_row(event, rows[0])
        require(self.sql("SELECT line_count FROM ingest_batch WHERE install_id='" + install_id + "';") == "2", "Actual transport batch count differs")
        replay_key = hashlib.sha256(lines[0]).hexdigest()
        for _ in range(2):
            require(self.request("/v1/telemetry", lines[0], self.token, replay_key) == 200, "Real payload replay failed")
        require(len(self.rows(install_id)) == 2, "Real payload replay duplicated a row")
        self.command([self.cli, "telemetry", "disable"], env=self.cli_env)
        require(self.sql("SELECT count(*) FROM forget_queue WHERE install_id='" + install_id + "';") == "1", "Actual CLI forget request was not delivered")
        forgotten = json.loads(consent.read_bytes())
        require(forgotten.get("installId") is None and not outbox.exists(), "Local CLI opt-out did not erase identity/outbox")
        require(self.request("/v1/telemetry/forget", json.dumps({"installId": fixture["installId"]}).encode(), self.token,
                             content_type="application/json") == 200, "Fixture forget enqueue failed")
        self.restart_backend()
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if self.sql("SELECT count(*) FROM telemetry_event;") == "0" and self.sql("SELECT count(*) FROM ingest_batch;") == "0":
                require(self.sql("SELECT count(*) FROM forget_queue WHERE processed_at IS NOT NULL;") == "2", "Production forget drain did not finish both requests")
                break
            time.sleep(1)
        else:
            raise RuntimeError("Production startup forget drain did not erase stored data")
        write_json(self.root / "scenario-checks.json", {"checksPassed": True, "backendSource": os.environ["GITHUB_SHA"],
                   "coreVersion": self.args.core_version, "coreCommit": self.args.core_commit,
                   "fixtureSchema2": True, "realCliLocalOutboxRetention": True, "realCliSchema2Transport": True, "idempotency": True,
                   "privacyRefusal": True, "realCliOptOutAndForget": True, "forgetDrain": "actual backend startup maintenance"})

    def recover_build_images(self):
        # Recover even when build wrote its IID before a checked failure/timeout.
        self.image_recovery_complete = False
        iid = self.private / "image.id"
        if iid.exists():
            require(iid.is_file() and not iid.is_symlink(), "Refuse non-regular build IID")
            with iid.open("rb") as stream:
                value = stream.read(129)
            require(len(value) <= 128, "Oversized build IID")
            identifier = value.decode("ascii").strip()
            self.owned("image", identifier)
            self.owned_images.add(identifier)
            self.image = identifier
        # A late exporter can produce an image/tag without leaving an IID file.
        candidates = self.docker("image", "ls", "--no-trunc", "--quiet", "--filter", "label=" + LABEL + "=" + self.owner).splitlines()
        for identifier in candidates:
            self.owned("image", identifier)
            self.owned_images.add(identifier)
        if self.image is None and len(self.owned_images) == 1:
            self.image = next(iter(self.owned_images))
        self.image_recovery_complete = True

    def start(self):
        require(self.docker("info", "--format", "{{.OSType}}") == "linux", "Require Linux Docker")
        self.before = self.snapshot()
        write_json(self.root / "foreign-before.json", self.before)
        self.image_recovery_started = True
        self.image_recovery_complete = False
        try:
            self.docker("build", "--label", LABEL + "=" + self.owner, "--label", "org.opencontainers.image.revision=" + os.environ["GITHUB_SHA"],
                        "--iidfile", str(self.private / "image.id"), "-f", "Dockerfile", "-t", self.prefix, ".", timeout=600)
        finally:
            self.recover_build_images()
        require(self.image is not None, "Successful build did not expose an exact owned image")
        self.owned("image", self.image)
        try:
            self.network = self.docker("network", "create", "--label", LABEL + "=" + self.owner, self.prefix)
        finally:
            if self.network is None:
                # Recover only this unique name with this run's unpredictable label.
                text = self.docker("network", "inspect", "--format", "{{json .}}", self.prefix, check=False)
                if text:
                    network = json.loads(text)
                    if network.get("Labels", {}).get(LABEL) == self.owner:
                        self.network = network["Id"]
        self.owned("network", self.network)
        pg_env, backend_env = self.private / "postgres.env", self.private / "backend.env"
        pg_env.write_text("POSTGRES_PASSWORD=" + self.password + "\nPOSTGRES_DB=vfxtel\n")
        backend_env.write_text("ASPNETCORE_ENVIRONMENT=Production\nVOUCHFX_TELEMETRY_INGEST_TOKENS=" + self.token
                               + "\nConnectionStrings__Telemetry=Server=postgres;Port=5432;Database=vfxtel;User Id=postgres;Password="
                               + self.password + ";SslMode=Disable;\n")
        pg_image = self.docker("image", "inspect", "--format", "{{.Id}}", "postgres:16", check=False)
        if not pg_image:
            self.docker("pull", "postgres:16", timeout=180)
            pg_image = self.docker("image", "inspect", "--format", "{{.Id}}", "postgres:16")
        require(bool(re.fullmatch(r"sha256:[0-9a-f]{64}", pg_image)), "Missing exact PostgreSQL image identity")
        self.create_container("postgres", "--network-alias", "postgres", "--tmpfs", "/var/lib/postgresql/data:rw,noexec,nosuid,size=256m",
                              "--env-file", str(pg_env), pg_image)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            output = self.docker("exec", self.containers["postgres"], "pg_isready", "-U", "postgres", "-d", "vfxtel", check=False)
            if self.commands[-1]["integerExit"] == 0 and not self.commands[-1]["timedOut"] and output.startswith("/var/run/postgresql:5432 - accepting connections"):
                break
            time.sleep(1)
        else:
            raise RuntimeError("Owned PostgreSQL did not become ready")
        server_version = int(self.sql("SHOW server_version_num;"))
        require(160000 <= server_version < 170000, "Require PostgreSQL 16 as documented")
        self.create_container("backend", "--env-file", str(backend_env), "--publish", "127.0.0.1::8080", self.image)
        self.listener_container = self.containers["backend"]
        self.refresh_endpoint("initial")
        self.wait_ready("initial")

    def cleanup(self):
        self.cleaning = True
        failures = []
        if self.image_recovery_started and not self.image_recovery_complete:
            try:
                self.recover_build_images()
            except Exception as error:
                failures.append("Build image recovery: " + type(error).__name__)
        for name, identifier in reversed(list(self.containers.items())):
            try:
                self.owned("container", identifier)
            except Exception as error:
                failures.append(type(error).__name__ + ": " + str(error))
                continue  # Never stop/remove an identity whose ownership is unverified.
            for args, timeout in [("logs --tail 400", 30), ("stop --time 10", 30), ("rm --force", 30)]:
                try:
                    # A log capture refusal must not bypass bounded removal attempts.
                    self.docker(*args.split(), identifier, timeout=timeout)
                except Exception as error:
                    failures.append(type(error).__name__ + ": " + str(error))
        for kind, identifier in [("network", self.network)] + [("image", identifier) for identifier in sorted(self.owned_images)]:
            if identifier:
                try:
                    self.owned(kind, identifier)
                    self.docker(kind, "rm", identifier)
                except Exception as error:
                    failures.append(type(error).__name__ + ": " + str(error))
        if hasattr(self, "before"):
            after = self.snapshot()
            write_json(self.root / "foreign-after.json", after)
            for key in ["containers", "networks", "volumes"]:
                if after[key] != self.before[key]:
                    failures.append("Foreign or leaked " + key + " changed")
            if any(identifier in after["images"] for identifier in self.owned_images):
                failures.append("A known owned build image remains")
            if any(self.prefix in metadata["tags"] or self.prefix + ":latest" in metadata["tags"]
                   for metadata in after["images"].values()):
                failures.append("The owned build tag remains")
            for identifier, metadata in self.before["images"].items():
                current = after["images"].get(identifier)
                if current is None or not set(metadata["tags"]).issubset(current["tags"]) or not set(metadata["digests"]).issubset(current["digests"]):
                    failures.append("A pre-existing image or tag/digest binding changed")
        if self.image_recovery_started and not self.image_recovery_complete:
            failures.append("Build image recovery was incomplete")
        write_json(self.root / "cleanup.json", {"accepted": not failures, "failures": failures, "exactOwnedIds": self.containers,
                   "ownedNetwork": self.network, "ownedImage": self.image, "knownOwnedImages": sorted(self.owned_images),
                   "buildImageRecoveryComplete": self.image_recovery_complete, "noGlobalCleanup": True})
        require(not failures, "Owned cleanup or foreign-resource preservation failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core-version", required=True)
    parser.add_argument("--core-commit", required=True)
    parser.add_argument("--core-package-sha256", required=True)
    parser.add_argument("--evidence-dir", required=True)
    args = parser.parse_args()
    require(sys.platform == "linux" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch"
            and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "This acceptance runs only in its isolated manual GitHub-hosted workflow")
    require(bool(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.core_version))
            and tuple(map(int, args.core_version.split("."))) >= (1, 0, 2), "Require an accepted stable public Core >=1.0.2")
    require(bool(re.fullmatch(r"[0-9a-f]{40}", args.core_commit)) and bool(re.fullmatch(r"[0-9a-f]{64}", args.core_package_sha256)), "Require full public commit and package digest")
    acceptance = Acceptance(args)
    try:
        try:
            require(acceptance.command(["git", "rev-parse", "HEAD"]) == os.environ["GITHUB_SHA"], "Backend checkout/source mismatch")
            require(not acceptance.command(["git", "status", "--porcelain", "--untracked-files=all"]), "Backend acceptance requires a clean committed checkout")
            acceptance.start()
            acceptance.exercise()
        except BaseException as error:
            write_json(acceptance.root / "failure.json", {"accepted": False, "originalErrorType": type(error).__name__})
            raise
        finally:
            acceptance.cleanup()
        write_json(acceptance.root / "acceptance.json", {"accepted": True,
                   "backendSource": os.environ["GITHUB_SHA"], "coreVersion": args.core_version,
                   "coreCommit": args.core_commit, "corePackageSha256": args.core_package_sha256,
                   "productionScenarioChecksPassed": True, "ownedCleanupAndForeignPreservationPassed": True})
    finally:
        acceptance.publish_artifacts()


if __name__ == "__main__":
    main()
