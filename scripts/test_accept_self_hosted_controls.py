#!/usr/bin/env python3
"""Nine bounded failure controls for the real hosted-acceptance helper.

The process, pipe, clock and Docker boundaries are deterministic in-memory fakes.
No child, Docker daemon, listener, public Core package or network is used.
"""
import argparse
import copy
from collections import deque
from contextlib import ExitStack
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import signal
import sys
from types import SimpleNamespace
from unittest.mock import patch
import xml.etree.ElementTree as ET

REPOSITORY = "tomas-rampas/vouchfx-telemetry-backend"
DUMMY_SECRET = "CONTROL_FAKE_CREDENTIAL_71829"
EXPECTED_CASES = (
    "stdout-overflow", "stderr-overflow", "aggregate-overflow",
    "credential-interruption", "redaction-failure", "reap-uncertainty",
    "failed-build-with-iid", "missing-iid-owned-image", "unobserved-eof",
)


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def refused(action, expected):
    try:
        action()
    except BaseException as error:
        check(type(error) is expected, "Unexpected original exception type")
        return type(error).__name__
    raise AssertionError("Expected original failure was not raised")


def save(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def forbidden(*args, **kwargs):
    raise AssertionError("Control attempted an external effect")


class Pipe:
    def __init__(self, descriptor):
        self.descriptor, self.closed = descriptor, False

    def fileno(self):
        return self.descriptor

    def close(self):
        self.closed = True


class Child:
    def __init__(self, model):
        self.model, self.pid = model, 424242
        self.stdout, self.stderr = Pipe(71), Pipe(72)
        self.returncode = 0 if model.leader_exited else None

    def poll(self):
        self.model.tick()
        if not self.model.uncertain and self.returncode is None and not self.model.wait_live:
            if not any(self.model.chunks):
                self.returncode = 0
        return self.returncode

    def wait(self, timeout):
        check(timeout == 5, "Reap was not bounded")
        if self.model.uncertain or self.poll() is None:
            raise self.model.helper.subprocess.TimeoutExpired("inert-owned-child", timeout)
        self.model.waits += 1
        return self.returncode


class Selector:
    def __init__(self, model):
        self.model, self.entries = model, {}

    def register(self, pipe, events, data):
        check(events == self.model.helper.selectors.EVENT_READ, "Unexpected selector event")
        self.entries[pipe.fileno()] = SimpleNamespace(fileobj=pipe, data=data)

    def unregister(self, pipe):
        del self.entries[pipe.fileno()]

    def get_map(self):
        return self.entries

    def select(self, timeout):
        check(timeout == 0.1, "Collector poll was not bounded")
        self.model.selects += 1
        check(self.model.selects <= 512, "Collector exceeded finite control steps")
        self.model.now += timeout
        if self.model.selects == self.model.interrupt_at:
            raise KeyboardInterrupt()
        return [(entry, self.model.helper.selectors.EVENT_READ) for entry in list(self.entries.values())]

    def close(self):
        self.entries.clear()


class CaptureModel:
    def __init__(self, helper, chunks, *, wait_live=False, interrupt_at=None,
                 uncertain=False, leader_exited=False):
        self.helper, self.chunks = helper, [deque(stream) for stream in chunks]
        self.wait_live, self.interrupt_at = wait_live, interrupt_at
        self.uncertain, self.leader_exited = uncertain, leader_exited
        self.now, self.calls, self.selects, self.waits = 0.0, 0, 0, 0
        self.signals, self.launches = [], []
        self.child = Child(self)

    def tick(self):
        self.calls += 1
        check(self.calls <= 4096, "Control exceeded finite process probes")

    def monotonic(self):
        self.tick()
        self.now += 0.01
        return self.now

    def launch(self, argv, **kwargs):
        check(argv == ["inert-owned-child"], "Unexpected executable admitted")
        check(kwargs == {"stdout": self.helper.subprocess.PIPE,
                         "stderr": self.helper.subprocess.PIPE,
                         "env": None, "start_new_session": True}, "Owned process launch contract changed")
        check(not self.launches, "Control launched more than one child")
        self.launches.append({"argv": argv, "pid": self.child.pid, "owned": True})
        return self.child

    def read(self, descriptor, count):
        check(count == self.helper.CAPTURE_CHUNK_BYTES, "Read size changed")
        check(descriptor in (71, 72), "Attempted to read a foreign pipe")
        stream = self.chunks[descriptor - 71]
        return stream.popleft() if stream else b""

    def blocking(self, descriptor, value):
        check(descriptor in (71, 72) and value is False, "Unexpected pipe mode change")

    def kill(self, pid, requested_signal):
        check(pid == self.child.pid and self.child.returncode is None, "Signal targeted an unowned or exited leader")
        check(requested_signal in (signal.SIGTERM, signal.SIGKILL), "Unexpected signal")
        self.signals.append({"pid": pid, "signal": int(requested_signal)})
        if not self.uncertain:
            self.child.returncode = -int(requested_signal)
            for stream in self.chunks:
                stream.clear()

    def install(self, stack):
        for target, name, replacement in (
            (self.helper.subprocess, "Popen", self.launch),
            (self.helper.selectors, "DefaultSelector", lambda: Selector(self)),
            (self.helper.os, "read", self.read), (self.helper.os, "set_blocking", self.blocking),
            (self.helper.os, "killpg", self.kill), (self.helper.time, "monotonic", self.monotonic),
        ):
            stack.enter_context(patch.object(target, name, replacement))


def acceptance(helper, private_root, name):
    result = helper.Acceptance(SimpleNamespace(evidence_dir=str(private_root / name)))
    result.secrets = [DUMMY_SECRET]
    return result


def public_bytes_are_safe(instance):
    for directory in [instance.logs, instance.root / "published"]:
        if directory.exists():
            for item in directory.rglob("*"):
                if item.is_file():
                    check(DUMMY_SECRET.encode() not in item.read_bytes(), "Dummy credential escaped redaction")


def capture_record(instance, model):
    check(len(instance.commands) == len(model.launches) == 1, "Original command record was lost")
    record = instance.commands[0]
    on_disk = json.loads((instance.logs / "000.command.json").read_bytes())
    check(on_disk == record, "Original command record changed")
    raw = [instance.private / "000.stdout.raw", instance.private / "000.stderr.raw"]
    sizes = [item.stat().st_size for item in raw]
    check(sizes == record["retainedBytes"], "Retained byte counts differ from raw files")
    check(max(sizes) <= instance.commands[0]["streamByteLimit"], "Per-stream bound exceeded")
    check(sum(sizes) <= record["commandByteLimit"], "Command aggregate bound exceeded")
    check(record["rawCaptureUploadEligible"] is False, "Raw capture admitted for upload")
    check(model.child.stdout.closed and model.child.stderr.closed, "Reader closure missing")
    public_bytes_are_safe(instance)
    return {"command": record, "ownedSignals": model.signals, "boundedWaits": model.waits,
            "rawBytes": sizes, "foreignProcessEffects": 0}


def assert_no_publication(instance):
    refused(instance.publish_artifacts, RuntimeError)
    check(not instance.publication_safe, "Unsafe capture restored publication latch")
    check(not (instance.root / "published").exists(), "Unsafe evidence was published")
    check(not (instance.private / "sanitized-publication").exists(), "Unsafe capture reached artifact staging")


def overflow_control(helper, private_root, name, stream):
    block = b"x" * helper.CAPTURE_CHUNK_BYTES
    chunks = [[block] * 18, []] if stream == 0 else [[], [block] * 18]
    if stream is None:
        chunks = [[block] * 14, [block] * 14]
    instance = acceptance(helper, private_root, name)
    model = CaptureModel(helper, chunks, wait_live=True)
    with ExitStack() as stack:
        model.install(stack)
        original = refused(lambda: instance.command(["inert-owned-child"]), RuntimeError)
    observation = capture_record(instance, model)
    record = observation["command"]
    check(record["outputOverflow"] and record["childReaped"] and record["pipeDrainComplete"], "Overflow did not refuse with owned closure")
    check(record["streamEofObserved"] == [True, True] and record["integerExit"] == -int(signal.SIGTERM), "Overflow closure/exit evidence missing")
    expected = helper.MAX_COMMAND_BYTES if stream is None else helper.MAX_STREAM_BYTES
    check(sum(observation["rawBytes"]) == expected, "Overflow did not reach the actual configured bound")
    if stream is None:
        check(max(observation["rawBytes"]) < helper.MAX_STREAM_BYTES, "Aggregate case accidentally exercised a stream limit")
    instance.publish_artifacts()
    manifest = json.loads((instance.root / "published/artifact-manifest.json").read_bytes())
    check(manifest["sanitized"] and not manifest["rawCaptureIncluded"] and not manifest["acceptancePassed"], "Failure artifact misrepresented acceptance")
    check(manifest["payloadBytes"] < helper.MAX_ARTIFACT_BYTES, "Artifact exceeded its bound")
    public_bytes_are_safe(instance)
    observation.update(originalException=original, sanitizedFailureArtifactPublished=True,
                       artifactManifestSha256=hashlib.sha256((instance.root / "published/artifact-manifest.json").read_bytes()).hexdigest())
    return observation


def interruption_control(helper, private_root):
    instance = acceptance(helper, private_root, "credential-interruption")
    model = CaptureModel(helper, [[DUMMY_SECRET.encode(), b"pending"], [b"pending", b"pending"]],
                         wait_live=True, interrupt_at=2)
    with ExitStack() as stack:
        model.install(stack)
        original = refused(lambda: instance.command(["inert-owned-child"]), KeyboardInterrupt)
    result = capture_record(instance, model)
    record = result["command"]
    check(record["errorType"] == original and record["integerExit"] == -int(signal.SIGKILL), "Original interruption was replaced")
    check(record["childReaped"] and record["streamEofObserved"] == [False, False] and not record["pipeDrainComplete"], "Interrupted EOF was invented")
    check(DUMMY_SECRET.encode() in (instance.private / "000.stdout.raw").read_bytes(), "Credential control did not reach raw capture")
    check(record["sanitizedCaptureFinalized"], "Safe redacted command logs were not finalized")
    assert_no_publication(instance)
    result.update(originalException=original, credentialPresentOnlyInPrivateRaw=True, publicationRefused=True)
    return result


def redaction_control(helper, private_root):
    instance = acceptance(helper, private_root, "redaction-failure")
    model = CaptureModel(helper, [[DUMMY_SECRET.encode()], []])
    original_redact = instance.redact

    def fail_bytes(value):
        if isinstance(value, bytes):
            raise RuntimeError("Injected byte-redaction refusal")
        return original_redact(value)

    with ExitStack() as stack:
        model.install(stack)
        stack.enter_context(patch.object(instance, "redact", fail_bytes))
        original = refused(lambda: instance.command(["inert-owned-child"]), RuntimeError)
    result = capture_record(instance, model)
    check(result["command"]["childReaped"] and result["command"]["pipeDrainComplete"], "Redaction case failed before byte sanitization")
    check(not result["command"]["sanitizedCaptureFinalized"], "Failed redaction was marked finalized")
    assert_no_publication(instance)
    result.update(originalException=original, publicationRefused=True)
    return result


def reap_control(helper, private_root):
    instance = acceptance(helper, private_root, "reap-uncertainty")
    model = CaptureModel(helper, [[b"pending"], [b"pending"]], wait_live=True,
                         interrupt_at=2, uncertain=True)
    with ExitStack() as stack:
        model.install(stack)
        original = refused(lambda: instance.command(["inert-owned-child"]), KeyboardInterrupt)
    result = capture_record(instance, model)
    record = result["command"]
    check(record["errorType"] == original and record["integerExit"] is None and not record["childReaped"], "Uncertain reap became a measured exit")
    check(len(model.signals) == 1 and model.signals[0]["signal"] == int(signal.SIGKILL), "Owned bounded reap was not attempted")
    assert_no_publication(instance)
    result.update(originalException=original, boundedReapRefused=True, publicationRefused=True)
    return result


class BuildRefusal(RuntimeError):
    pass


class DockerModel:
    """Only the exact argv used by the real start/recovery/snapshot/cleanup is admitted."""
    def __init__(self, helper, instance, write_iid):
        self.helper, self.instance, self.write_iid = helper, instance, write_iid
        self.owned_id, self.foreign_id = "sha256:" + "a" * 64, "sha256:" + "b" * 64
        self.container, self.network = "c" * 64, "d" * 64
        self.images = {self.foreign_id: {"tags": ["foreign:original"], "digests": ["foreign@sha256:" + "e" * 64]}}
        self.calls, self.removals, self.build_exit = [], [], None

    def command(self, argv, timeout=30, env=None, check=True):
        # This seam replaces only Docker transport; real ownership and cleanup logic remain in use.
        check_fn = globals()["check"]
        check_fn(argv[0] == "docker" and env is None, "Unexpected model transport")
        args = argv[1:]
        check_fn(len(self.calls) < 128, "Docker control command bound exceeded")
        self.calls.append(args)
        h, a = self.helper, self.instance
        if args == ["info", "--format", "{{.OSType}}"]:
            return "linux"
        if args == ["ps", "-aq", "--no-trunc"]:
            return self.container
        if args == ["network", "ls", "-q", "--no-trunc"]:
            return self.network
        if args == ["volume", "ls", "-q"]:
            return "foreign-volume"
        if args == ["image", "ls", "-q", "--no-trunc"]:
            return "\n".join(sorted(self.images))
        if args == ["image", "ls", "--no-trunc", "--quiet", "--filter", "label=" + h.LABEL + "=" + a.owner]:
            return self.owned_id if self.owned_id in self.images else ""
        if len(args) == 4 and args[:2] == ["inspect", "--format"] and args[-1] == self.container:
            return json.dumps([self.container, "2020-01-01", self.foreign_id, "/foreign", 1234, True, {"foreign": {"NetworkID": self.network}}])
        if len(args) == 5 and args[:3] == ["network", "inspect", "--format"] and args[-1] == self.network:
            return json.dumps([self.network, "2020-01-01", "foreign", "bridge", {self.container: {"Name": "foreign"}}])
        if len(args) == 5 and args[:3] == ["volume", "inspect", "--format"] and args[-1] == "foreign-volume":
            return json.dumps(["foreign-volume", "2020-01-01", "local", "/inert/foreign", "local"])
        if len(args) == 5 and args[:3] == ["image", "inspect", "--format"] and args[-1] in self.images:
            identifier, template = args[-1], args[3]
            if template == "{{index .Config.Labels \"" + h.LABEL + "\"}}":
                return a.owner if identifier == self.owned_id else "foreign-owner"
            check_fn(template == "[{{json .Id}},{{json .RepoTags}},{{json .RepoDigests}}]", "Unexpected image projection")
            row = self.images[identifier]
            return json.dumps([identifier, row["tags"], row["digests"]])
        if args == ["build", "--label", h.LABEL + "=" + a.owner, "--label", "org.opencontainers.image.revision=" + os.environ["GITHUB_SHA"],
                    "--iidfile", str(a.private / "image.id"), "-f", "Dockerfile", "-t", a.prefix, "."]:
            check_fn(timeout == 600, "Build timeout changed")
            self.images[self.owned_id] = {"tags": [a.prefix + ":latest"], "digests": []}
            if self.write_iid:
                (a.private / "image.id").write_text(self.owned_id + "\n", encoding="ascii")
            self.build_exit = 1
            raise BuildRefusal("Injected original integer build exit 1")
        if args == ["image", "rm", self.owned_id]:
            check_fn(self.owned_id in a.owned_images, "Removal was not admitted by real ownership recovery")
            del self.images[self.owned_id]
            self.removals.append(self.owned_id)
            return "removed"
        raise AssertionError("Unexpected or foreign Docker operation")


def build_control(helper, private_root, name, write_iid):
    instance = acceptance(helper, private_root, name)
    model = DockerModel(helper, instance, write_iid)
    with patch.object(instance, "command", model.command):
        original = refused(instance.start, BuildRefusal)
        save(instance.root / "control-build-original.json",
             {"originalException": original, "originalBuildIntegerExit": model.build_exit,
              "iidWritten": write_iid, "recoveredExactImages": sorted(instance.owned_images),
              "buildImageRecoveryComplete": instance.image_recovery_complete, "actualDockerCommands": 0})
        check(model.build_exit == 1 and instance.image_recovery_complete, "Original failed build/recovery was lost")
        check(instance.owned_images == {model.owned_id} and instance.image == model.owned_id, "Exact owned image was not recovered")
        check((instance.private / "image.id").exists() is write_iid, "IID variant did not exercise its intended path")
        instance.cleanup()
        # Exercise the real label check, not a mirrored allowlist, on a retained foreign image.
        refused(lambda: instance.owned("image", model.foreign_id), RuntimeError)
        after = instance.snapshot()
    cleanup = json.loads((instance.root / "cleanup.json").read_bytes())
    check(cleanup["accepted"] and cleanup["buildImageRecoveryComplete"], "Recovered image cleanup failed")
    check(after == instance.before and model.removals == [model.owned_id], "Foreign baseline changed or removal broadened")
    check(model.foreign_id in model.images and model.owned_id not in model.images, "Foreign preservation/owned closure failed")
    return {"originalException": original, "originalBuildIntegerExit": model.build_exit,
            "iidWritten": write_iid, "recoveredExactImage": model.owned_id,
            "removedExactImages": model.removals, "foreignOwnershipRefused": True,
            "foreignContainerNetworkVolumeAndImagePreserved": True,
            "realCleanupRecord": cleanup, "inertDockerCommandCount": len(model.calls),
            "actualDockerCommands": 0}


def eof_control(helper, private_root):
    variants = []
    for label, exited in [("live", False), ("leader-exited-inherited-pipe", True)]:
        instance = acceptance(helper, private_root, "unobserved-eof-" + label)
        model = CaptureModel(helper, [[b"pending", b"pending"], []], wait_live=True,
                             interrupt_at=2, leader_exited=exited)
        with ExitStack() as stack:
            model.install(stack)
            original = refused(lambda: instance.command(["inert-owned-child"]), KeyboardInterrupt)
        result = capture_record(instance, model)
        record = result["command"]
        check(record["errorType"] == original and record["childReaped"], "Original EOF interruption/leader closure missing")
        check(record["streamEofObserved"] == [False, True] and not record["pipeDrainComplete"], "Unobserved stdout EOF was invented")
        check(record["integerExit"] == (0 if exited else -int(signal.SIGKILL)), "Original leader exit changed")
        check(len(model.signals) == (0 if exited else 1), "Exited leader was signalled or live owned leader was not closed")
        assert_no_publication(instance)
        result.update(variant=label, originalException=original, publicationRefused=True)
        variants.append(result)
    return {"variants": variants, "bothEofMissingAlsoCoveredBy": "credential-interruption"}


CANONICAL_RECORD_ENCODING = "utf8-json-sortkeys-compact-v1"
ADMISSION_CASES = (
    "missing-backend-source", "wrong-backend-source", "missing-helper-hash", "wrong-helper-hash",
    "missing-harness-hash", "wrong-harness-hash", "bool-schema-version", "float-schema-version",
    "bool-tests", "float-tests", "bool-passed", "float-passed", "bool-failed", "float-failed",
    "bool-skipped", "float-skipped", "bool-child-count", "float-child-count", "bool-docker-count",
    "float-docker-count", "bool-network-count", "float-network-count", "missing-record-hash",
    "wrong-record-hash", "missing-record-encoding", "wrong-record-encoding", "wrong-record-value",
    "wrong-record-value-rehashed", "wrong-record-variant", "wrong-record-path", "missing-record",
    "extra-record", "wrong-case-name", "missing-case", "extra-case", "wrong-case-order",
    "failed-case", "skipped-case", "bool-record-exit", "float-record-exit", "missing-original-records",
    "extra-root-field",
)


def canonical_record(value):
    # This binds sanitized JSON values, not original private-file bytes or formatting.
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                      allow_nan=False).encode("utf-8")


def exact_keys(value, names):
    check(type(value) is dict and set(value) == set(names), "JSON object schema mismatch")


def integer(value, expected=None, minimum=0, maximum=None):
    check(type(value) is int and value >= minimum and (maximum is None or value <= maximum),
          "Strict integer field refused")
    if expected is not None:
        check(value == expected, "Integer count mismatch")


def boolean(value, expected=None):
    check(type(value) is bool and (expected is None or value is expected), "Strict boolean field refused")


def digest(value):
    check(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) is not None, "Full SHA-256 required")


def validate_capture(observation, name):
    common = {"command", "ownedSignals", "boundedWaits", "rawBytes", "foreignProcessEffects", "originalException"}
    if name in EXPECTED_CASES[:3]:
        extra = {"sanitizedFailureArtifactPublished", "artifactManifestSha256"}
    elif name == "credential-interruption":
        extra = {"credentialPresentOnlyInPrivateRaw", "publicationRefused"}
    elif name == "reap-uncertainty":
        extra = {"boundedReapRefused", "publicationRefused"}
    elif name == "redaction-failure":
        extra = {"publicationRefused"}
    else:
        extra = {"variant", "publicationRefused"}
    exact_keys(observation, common | extra)
    integer(observation["foreignProcessEffects"], 0)
    integer(observation["boundedWaits"], maximum=2)
    check(type(observation["ownedSignals"]) is list and len(observation["ownedSignals"]) <= 2, "Owned signal record bound refused")
    for item in observation["ownedSignals"]:
        exact_keys(item, {"pid", "signal"})
        integer(item["pid"], 424242)
        integer(item["signal"], minimum=1)
        check(item["signal"] in {int(signal.SIGTERM), int(signal.SIGKILL)}, "Unexpected owned signal")
    record = observation["command"]
    exact_keys(record, {"argv", "integerExit", "launched", "timedOut", "outputOverflow", "childReaped",
                        "pipeDrainComplete", "streamEofObserved", "errorType", "seconds", "retainedBytes",
                        "streamByteLimit", "commandByteLimit", "runCaptureByteLimit",
                        "rawCaptureUploadEligible", "sanitizedCaptureFinalized"})
    check(type(record["argv"]) is list and record["argv"] == ["inert-owned-child"], "Unexpected original command identity")
    integer(record["streamByteLimit"], 1048576)
    integer(record["commandByteLimit"], 1572864)
    integer(record["runCaptureByteLimit"], 41943040)
    integer(record["integerExit"], minimum=-128, maximum=128) if record["integerExit"] is not None else None
    boolean(record["launched"], True)
    boolean(record["timedOut"], False)
    boolean(record["rawCaptureUploadEligible"], False)
    for key in ["outputOverflow", "childReaped", "pipeDrainComplete", "sanitizedCaptureFinalized"]:
        boolean(record[key])
    check(type(record["seconds"]) is float and math.isfinite(record["seconds"]) and 0 <= record["seconds"] <= 60,
          "Bounded collector duration refused")
    check(type(record["streamEofObserved"]) is list and len(record["streamEofObserved"]) == 2, "EOF schema mismatch")
    for value in record["streamEofObserved"]:
        boolean(value)
    check(type(record["retainedBytes"]) is list and len(record["retainedBytes"]) == 2, "Retained byte schema mismatch")
    for value in record["retainedBytes"]:
        integer(value, maximum=1048576)
    check(type(observation["rawBytes"]) is list and canonical_record(observation["rawBytes"]) == canonical_record(record["retainedBytes"]),
          "Original retained byte counts mismatch")
    check(sum(record["retainedBytes"]) <= 1572864, "Original aggregate capture bound refused")
    if name in EXPECTED_CASES[:3]:
        boolean(record["outputOverflow"], True)
        boolean(record["childReaped"], True)
        boolean(record["pipeDrainComplete"], True)
        check(record["streamEofObserved"] == [True, True] and record["errorType"] is None,
              "Overflow original EOF/error mismatch")
        check(record["integerExit"] == -int(signal.SIGTERM), "Original overflow exit mismatch")
        integer(sum(record["retainedBytes"]), 1572864 if name == "aggregate-overflow" else 1048576)
        boolean(observation["sanitizedFailureArtifactPublished"], True)
        digest(observation["artifactManifestSha256"])
    else:
        boolean(record["outputOverflow"], False)
        boolean(observation["publicationRefused"], True)
        boolean(record["childReaped"], name != "reap-uncertainty")
        boolean(record["pipeDrainComplete"], name == "redaction-failure")
        if name == "redaction-failure":
            check(record["errorType"] is None and record["integerExit"] == 0 and record["streamEofObserved"] == [True, True],
                  "Original redaction control capture mismatch")
        else:
            check(record["errorType"] == "KeyboardInterrupt", "Original interruption was not retained")
            expected_exit = None if name == "reap-uncertainty" else 0 if name == "leader-exited-inherited-pipe" else -int(signal.SIGKILL)
            check(record["integerExit"] == expected_exit, "Original interruption exit mismatch")
            check(record["streamEofObserved"] == ([False, True] if name in {"live", "leader-exited-inherited-pipe"} else [False, False]),
                  "Unobserved EOF was admitted")
        if name == "credential-interruption":
            boolean(observation["credentialPresentOnlyInPrivateRaw"], True)
        if name == "reap-uncertainty":
            boolean(observation["boundedReapRefused"], True)
        if name in {"live", "leader-exited-inherited-pipe"}:
            check(observation["variant"] == name, "EOF variant identity mismatch")
    boolean(record["sanitizedCaptureFinalized"], name != "redaction-failure")
    check(observation["originalException"] == ("RuntimeError" if name in EXPECTED_CASES[:3] or name == "redaction-failure" else "KeyboardInterrupt"),
          "Original exception identity mismatch")
    return record


def validate_build(observation, name):
    exact_keys(observation, {"originalException", "originalBuildIntegerExit", "iidWritten", "recoveredExactImage",
                             "removedExactImages", "foreignOwnershipRefused", "foreignContainerNetworkVolumeAndImagePreserved",
                             "realCleanupRecord", "inertDockerCommandCount", "actualDockerCommands"})
    check(observation["originalException"] == "BuildRefusal", "Original build failure type mismatch")
    integer(observation["originalBuildIntegerExit"], 1)
    boolean(observation["iidWritten"], name == "failed-build-with-iid")
    boolean(observation["foreignOwnershipRefused"], True)
    boolean(observation["foreignContainerNetworkVolumeAndImagePreserved"], True)
    integer(observation["actualDockerCommands"], 0)
    integer(observation["inertDockerCommandCount"], minimum=1, maximum=128)
    image = "sha256:" + "a" * 64
    check(observation["recoveredExactImage"] == image and type(observation["removedExactImages"]) is list
          and observation["removedExactImages"] == [image], "Exact owned image binding mismatch")
    cleanup = observation["realCleanupRecord"]
    exact_keys(cleanup, {"accepted", "failures", "exactOwnedIds", "ownedNetwork", "ownedImage", "knownOwnedImages",
                         "buildImageRecoveryComplete", "noGlobalCleanup"})
    for key in ["accepted", "buildImageRecoveryComplete", "noGlobalCleanup"]:
        boolean(cleanup[key], True)
    check(type(cleanup["failures"]) is list and not cleanup["failures"] and type(cleanup["exactOwnedIds"]) is dict
          and not cleanup["exactOwnedIds"] and cleanup["ownedNetwork"] is None and cleanup["ownedImage"] == image
          and type(cleanup["knownOwnedImages"]) is list and cleanup["knownOwnedImages"] == [image], "Original cleanup identities mismatch")
    original = {"originalException": "BuildRefusal", "originalBuildIntegerExit": 1,
                "iidWritten": name == "failed-build-with-iid", "recoveredExactImages": [image],
                "buildImageRecoveryComplete": True, "actualDockerCommands": 0}
    return original, cleanup


def validate_report(report, source, helper_hash, harness_hash, require_admission=True):
    base_fields = {"schemaVersion", "backendSource", "helperSha256", "harnessSha256", "requiredCases", "tests",
                   "passed", "failed", "skipped", "actualChildLaunches", "actualDockerCommands", "networkRequests",
                   "publicCoreRuntimeAccepted", "privateRawUploaded", "cases"}
    exact_keys(report, base_fields | ({"admissionControls"} if require_admission else set()))
    integer(report["schemaVersion"], 2)
    check(type(report["backendSource"]) is str and re.fullmatch(r"[0-9a-f]{40}", report["backendSource"]) is not None
          and report["backendSource"] == source, "Backend checkout source mismatch")
    for key, expected in [("helperSha256", helper_hash), ("harnessSha256", harness_hash)]:
        digest(report[key])
        check(report[key] == expected, "Actual checkout file digest mismatch")
    for key, count in [("tests", 9), ("passed", 9), ("failed", 0), ("skipped", 0),
                       ("actualChildLaunches", 0), ("actualDockerCommands", 0), ("networkRequests", 0)]:
        integer(report[key], count)
    boolean(report["publicCoreRuntimeAccepted"], False)
    boolean(report["privateRawUploaded"], False)
    check(type(report["requiredCases"]) is list and report["requiredCases"] == list(EXPECTED_CASES), "Required case set/order mismatch")
    check(type(report["cases"]) is list and len(report["cases"]) == 9, "Case count mismatch")
    for case, name in zip(report["cases"], EXPECTED_CASES):
        exact_keys(case, {"name", "status", "observation", "originalRecords"})
        check(case["name"] == name and case["status"] == "PASS", "Exact case identity/verdict mismatch")
        observation = case["observation"]
        if name in EXPECTED_CASES[6:8]:
            original, cleanup = validate_build(observation, name)
            expected_records = [(name, "control-build-original.json", original), (name, "cleanup.json", cleanup)]
        elif name == "unobserved-eof":
            exact_keys(observation, {"variants", "bothEofMissingAlsoCoveredBy"})
            check(observation["bothEofMissingAlsoCoveredBy"] == "credential-interruption"
                  and type(observation["variants"]) is list and len(observation["variants"]) == 2, "EOF variant set mismatch")
            expected_records = [("unobserved-eof-" + variant, "logs/000.command.json", validate_capture(item, variant))
                                for item, variant in zip(observation["variants"], ["live", "leader-exited-inherited-pipe"])]
        else:
            expected_records = [(name, "logs/000.command.json", validate_capture(observation, name))]
        records = case["originalRecords"]
        check(type(records) is list and len(records) == len(expected_records), "Original record set mismatch")
        for record, (variant, path, value) in zip(records, expected_records):
            exact_keys(record, {"variant", "record", "canonicalEncoding", "canonicalSha256", "value"})
            check(record["variant"] == variant and record["record"] == path, "Original case/variant/path identity mismatch")
            check(record["canonicalEncoding"] == CANONICAL_RECORD_ENCODING, "Canonical record encoding mismatch")
            digest(record["canonicalSha256"])
            encoded = canonical_record(record["value"])
            check(record["canonicalSha256"] == hashlib.sha256(encoded).hexdigest(), "Original canonical record digest mismatch")
            check(encoded == canonical_record(value) and DUMMY_SECRET.encode() not in encoded, "Original record/observation mismatch")
    if require_admission:
        admission = report["admissionControls"]
        exact_keys(admission, {"tests", "passed", "failed", "skipped", "cases"})
        for key, count in [("tests", len(ADMISSION_CASES)), ("passed", len(ADMISSION_CASES)), ("failed", 0), ("skipped", 0)]:
            integer(admission[key], count)
        check(type(admission["cases"]) is list and len(admission["cases"]) == len(ADMISSION_CASES), "Admission fixture count mismatch")
        for case, name in zip(admission["cases"], ADMISSION_CASES):
            exact_keys(case, {"name", "status"})
            check(case["name"] == name and case["status"] == "PASS", "Required negative admission fixture failed")


def admission_controls(report, source, helper_hash, harness_hash):
    # Mutations use the real successful helper result, not a synthetic replica of it.
    try:
        validate_report(report, source, helper_hash, harness_hash, require_admission=False)
    except (AssertionError, ValueError, TypeError):
        # Preserve the actual helper records even when positive admission itself refuses.
        return {"tests": 1, "passed": 0, "failed": 1, "skipped": len(ADMISSION_CASES),
                "cases": [{"name": "positive-report-admission", "status": "FAIL"}]}
    results = []
    numeric = {"schema-version": "schemaVersion", "tests": "tests", "passed": "passed", "failed": "failed",
               "skipped": "skipped", "child-count": "actualChildLaunches", "docker-count": "actualDockerCommands",
               "network-count": "networkRequests"}
    identities = {"backend-source": "backendSource", "helper-hash": "helperSha256", "harness-hash": "harnessSha256"}
    for name in ADMISSION_CASES:
        candidate = copy.deepcopy(report)
        record = candidate["cases"][0]["originalRecords"][0]
        if name.startswith(("missing-", "wrong-")) and name.split("-", 1)[1] in identities:
            key = identities[name.split("-", 1)[1]]
            if name.startswith("missing-"):
                del candidate[key]
            else:
                candidate[key] = ("0" if candidate[key][0] != "0" else "1") + candidate[key][1:]
        elif name.startswith(("bool-", "float-")) and name.split("-", 1)[1] in numeric:
            key = numeric[name.split("-", 1)[1]]
            candidate[key] = bool(candidate[key]) if name.startswith("bool-") else float(candidate[key])
        elif name == "missing-record-hash":
            del record["canonicalSha256"]
        elif name == "wrong-record-hash":
            record["canonicalSha256"] = ("0" if record["canonicalSha256"][0] != "0" else "1") + record["canonicalSha256"][1:]
        elif name == "missing-record-encoding":
            del record["canonicalEncoding"]
        elif name == "wrong-record-encoding":
            record["canonicalEncoding"] = "unspecified-json"
        elif name in {"wrong-record-value", "wrong-record-value-rehashed", "bool-record-exit", "float-record-exit"}:
            record["value"]["integerExit"] = True if name == "bool-record-exit" else float(record["value"]["integerExit"]) if name == "float-record-exit" else -9
            if name != "wrong-record-value":
                record["canonicalSha256"] = hashlib.sha256(canonical_record(record["value"])).hexdigest()
        elif name == "wrong-record-variant":
            record["variant"] = "stderr-overflow"
        elif name == "wrong-record-path":
            record["record"] = "private/000.stdout.raw"
        elif name == "missing-record":
            candidate["cases"][0]["originalRecords"] = []
        elif name == "extra-record":
            candidate["cases"][0]["originalRecords"].append(copy.deepcopy(record))
        elif name == "wrong-case-name":
            candidate["cases"][0]["name"] = "stderr-overflow"
        elif name == "missing-case":
            candidate["cases"].pop()
        elif name == "extra-case":
            candidate["cases"].append(copy.deepcopy(candidate["cases"][0]))
        elif name == "wrong-case-order":
            candidate["cases"][0], candidate["cases"][1] = candidate["cases"][1], candidate["cases"][0]
        elif name in {"failed-case", "skipped-case"}:
            candidate["cases"][0]["status"] = "FAIL" if name == "failed-case" else "SKIP"
        elif name == "missing-original-records":
            del candidate["cases"][0]["originalRecords"]
        elif name == "extra-root-field":
            candidate["unbound"] = True
        else:
            raise AssertionError("Unknown finite admission fixture")
        try:
            validate_report(candidate, source, helper_hash, harness_hash, require_admission=False)
        except (AssertionError, ValueError, TypeError):
            results.append({"name": name, "status": "PASS"})
        else:
            results.append({"name": name, "status": "FAIL"})
    passed = sum(case["status"] == "PASS" for case in results)
    return {"tests": len(results), "passed": passed, "failed": len(results) - passed, "skipped": 0, "cases": results}


def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        check(key not in result, "Duplicate JSON key refused")
        result[key] = value
    return result


class InertJUnitTreeBuilder(ET.TreeBuilder):
    def start_ns(self, prefix, uri):
        raise ValueError("JUnit namespace declarations are not admitted")

    def comment(self, text):
        raise AssertionError("JUnit comments are not admitted")

    def pi(self, target, text):
        raise AssertionError("JUnit processing instructions are not admitted")


def validate_junit(xml):
    check(len(xml) <= 128 * 1024, "Bounded JUnit required")
    text = xml.decode("utf-8")
    if "\x00" in text:
        raise ValueError("NUL-containing XML is not strict UTF-8 JUnit")
    declaration_text = text.removeprefix("\ufeff")
    if declaration_text.startswith("<?xml"):
        end = declaration_text.find("?>")
        declaration = declaration_text[:end + 2] if end != -1 else declaration_text
        supported = re.fullmatch(
            r"""<\?xml[ \t\r\n]+version[ \t\r\n]*=[ \t\r\n]*(["'])1\.0\1"""
            r"""(?:[ \t\r\n]+encoding[ \t\r\n]*=[ \t\r\n]*(["'])(?i:utf-8)\2)?"""
            r"""(?:[ \t\r\n]+standalone[ \t\r\n]*=[ \t\r\n]*(["'])(?:yes|no)\3)?[ \t\r\n]*\?>""",
            declaration,
        )
        if supported is None:
            raise ValueError("JUnit declaration must use XML 1.0 and UTF-8")
    check(DUMMY_SECRET not in text and "<!DOCTYPE" not in text and "<!ENTITY" not in text,
          "Sanitized inert UTF-8 JUnit required")
    suite = ET.fromstring(text, parser=ET.XMLParser(target=InertJUnitTreeBuilder()))
    check(all(not (value or "").strip(" \t\r\n")
              for element in suite.iter() for value in (element.text, element.tail)),
          "JUnit text and tails must contain formatting whitespace only")
    check(suite.tag == "testsuite" and suite.attrib == {"name": "telemetry-acceptance-failure-controls",
          "tests": "9", "failures": "0", "errors": "0", "skipped": "0"}, "Strict JUnit summary mismatch")
    check([case.attrib for case in suite] == [{"name": name} for name in EXPECTED_CASES]
          and all(case.tag == "testcase" and not list(case) for case in suite), "Strict JUnit cases/verdicts mismatch")


def verify_evidence(evidence, source, helper_path, harness_path):
    path = evidence / "results.json"
    check(path.is_file() and not path.is_symlink(), "Regular result record required")
    with path.open("rb") as stream:
        encoded = stream.read(128 * 1024 + 1)
    check(len(encoded) <= 128 * 1024 and DUMMY_SECRET.encode() not in encoded, "Bounded sanitized report required")
    report = json.loads(encoded, object_pairs_hook=no_duplicate_keys, parse_constant=forbidden)
    validate_report(report, source, hashlib.sha256(helper_path.read_bytes()).hexdigest(),
                    hashlib.sha256(harness_path.read_bytes()).hexdigest())
    path = evidence / "junit.xml"
    check(path.is_file() and not path.is_symlink() and path.stat().st_size <= 128 * 1024, "Bounded regular JUnit required")
    with path.open("rb") as stream:
        xml = stream.read(128 * 1024 + 1)
    validate_junit(xml)


def original_records(private_root, name):
    names = [name] if name != "unobserved-eof" else ["unobserved-eof-live", "unobserved-eof-leader-exited-inherited-pipe"]
    records = []
    for case_name in names:
        root = private_root / case_name
        for relative in ["logs/000.command.json", "control-build-original.json", "cleanup.json"]:
            source = root / relative
            if source.exists():
                check(source.is_file() and not source.is_symlink(), "Original control record was not regular")
                with source.open("rb") as stream:
                    encoded = stream.read(128 * 1024 + 1)
                check(len(encoded) <= 128 * 1024 and DUMMY_SECRET.encode() not in encoded, "Original control record was unsafe")
                value = json.loads(encoded, object_pairs_hook=no_duplicate_keys, parse_constant=forbidden)
                records.append({"variant": case_name, "record": relative,
                                "canonicalEncoding": CANONICAL_RECORD_ENCODING,
                                "canonicalSha256": hashlib.sha256(canonical_record(value)).hexdigest(), "value": value})
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", required=True)
    parser.add_argument("--verify-evidence", action="store_true")
    args = parser.parse_args()
    check(sys.platform == "linux" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
          and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY
          and os.environ.get("GITHUB_EVENT_NAME") in {"pull_request", "push", "workflow_dispatch"},
          "Controls run only in the isolated GitHub-hosted workflow")
    check(re.fullmatch(r"[0-9a-f]{40}", os.environ.get("GITHUB_SHA", "")) is not None, "Full backend source identity required")
    helper_path = Path(__file__).resolve().with_name("accept_self_hosted.py")
    if args.verify_evidence:
        verify_evidence(Path(args.evidence_dir), os.environ["GITHUB_SHA"], helper_path, Path(__file__))
        return
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    evidence = Path(args.evidence_dir).resolve()
    check(evidence.is_relative_to(temporary) and evidence != temporary and not evidence.exists(), "Fresh result directory below RUNNER_TEMP required")
    evidence.mkdir()
    private_root = temporary / "telemetry-control-private"
    check(not private_root.exists(), "Fresh private control directory required")
    private_root.mkdir(mode=0o700)
    helper_path = Path(__file__).resolve().with_name("accept_self_hosted.py")
    spec = importlib.util.spec_from_file_location("telemetry_acceptance_under_test", helper_path)
    check(spec is not None and spec.loader is not None, "Helper loader unavailable")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)  # __main__ is never invoked.
    actions = (
        lambda: overflow_control(helper, private_root, "stdout-overflow", 0),
        lambda: overflow_control(helper, private_root, "stderr-overflow", 1),
        lambda: overflow_control(helper, private_root, "aggregate-overflow", None),
        lambda: interruption_control(helper, private_root),
        lambda: redaction_control(helper, private_root),
        lambda: reap_control(helper, private_root),
        lambda: build_control(helper, private_root, "failed-build-with-iid", True),
        lambda: build_control(helper, private_root, "missing-iid-owned-image", False),
        lambda: eof_control(helper, private_root),
    )
    results = []
    # Any accidental unstubbed product/native/network boundary is an immediate refusal.
    with ExitStack() as isolation:
        for target, name in [(helper.subprocess, "Popen"), (helper.subprocess, "run"),
                             (helper.subprocess, "check_output"), (helper.os, "killpg"),
                             (helper.os, "system"), (helper.os, "read"), (helper.os, "set_blocking"),
                             (helper.selectors, "DefaultSelector"), (helper.time, "sleep"),
                             (helper.urllib.request, "urlopen"), (helper.urllib.request.OpenerDirector, "open")]:
            isolation.enter_context(patch.object(target, name, forbidden))
        for name, action in zip(EXPECTED_CASES, actions):
            try:
                observation = action()
                originals = original_records(private_root, name)
                check(originals, "Original control result records were absent")
                results.append({"name": name, "status": "PASS", "observation": observation,
                                "originalRecords": originals})
            except BaseException as error:
                # Do not serialize arbitrary exception text, locals, private paths or raw output.
                try:
                    originals = original_records(private_root, name)
                except BaseException:
                    originals = []  # Refuse unsafe records instead of publishing their bytes.
                results.append({"name": name, "status": "FAIL", "exceptionType": type(error).__name__,
                                "originalRecords": originals})
    passed = sum(item["status"] == "PASS" for item in results)
    failed = len(results) - passed
    report = {"schemaVersion": 2, "backendSource": os.environ["GITHUB_SHA"],
              "helperSha256": hashlib.sha256(helper_path.read_bytes()).hexdigest(),
              "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "requiredCases": list(EXPECTED_CASES), "tests": len(results), "passed": passed,
              "failed": failed, "skipped": 0, "actualChildLaunches": 0, "actualDockerCommands": 0,
              "networkRequests": 0, "publicCoreRuntimeAccepted": False, "privateRawUploaded": False,
              "cases": results}
    report["admissionControls"] = (admission_controls(report, os.environ["GITHUB_SHA"],
                                  report["helperSha256"], report["harnessSha256"])
                                  if passed == 9 and failed == 0 else
                                  {"tests": 0, "passed": 0, "failed": 0, "skipped": len(ADMISSION_CASES), "cases": []})
    suite = ET.Element("testsuite", name="telemetry-acceptance-failure-controls", tests=str(len(results)),
                       failures=str(failed), errors="0", skipped="0")
    for result in results:
        case = ET.SubElement(suite, "testcase", name=result["name"])
        if result["status"] != "PASS":
            ET.SubElement(case, "failure", type=result["exceptionType"]).text = "Control assertion or boundary refused"
    # Only these two bounded sanitized records are upload eligible. Never upload private_root.
    encoded = (json.dumps(report, indent=2) + "\n").encode()
    check(len(encoded) <= 128 * 1024 and DUMMY_SECRET.encode() not in encoded, "Control result publication refused")
    with (evidence / "results.json").open("xb") as stream:
        stream.write(encoded)
    with (evidence / "junit.xml").open("xb") as stream:
        stream.write(ET.tostring(suite, encoding="utf-8", xml_declaration=True))
    print(json.dumps({"tests": len(results), "passed": passed, "failed": failed, "skipped": 0}))
    check(tuple(item["name"] for item in results) == EXPECTED_CASES and passed == 9 and failed == 0,
          "All nine failure controls must pass with zero failures and zero skips")
    validate_report(report, os.environ["GITHUB_SHA"], hashlib.sha256(helper_path.read_bytes()).hexdigest(),
                    hashlib.sha256(Path(__file__).read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
