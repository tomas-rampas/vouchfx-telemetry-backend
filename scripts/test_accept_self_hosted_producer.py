#!/usr/bin/env python3
"""Real producer orchestration with inert CLI, HTTP and storage boundaries."""
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import accept_self_hosted as helper


class DeliveryBoundaryReached(Exception):
    pass


class ProducerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        with patch.dict(os.environ, {"RUNNER_TEMP": self.temp.name,
                                     "VOUCHFX_ACCEPTANCE_GITHUB_TOKEN": "inert-metadata-token"}):
            self.a = helper.Acceptance(SimpleNamespace(evidence_dir=str(Path(self.temp.name) / "evidence"),
                                                       core_version="1.0.5"))
        self.a.endpoint = "http://127.0.0.1:32768"
        self.install_id = "e2f7b6e5-a5fb-4e63-8dc9-c66877abda38"
        self.event_count = 1
        self.runs = []
        self.fixture = None
        self.a.public_cli = self.public_cli
        self.a.command = self.command
        self.a.request = self.request
        self.a.rows = lambda identity: [dict()] if self.fixture and identity == self.fixture["installId"] else []
        self.a.compare_row = lambda event, row: None

    def public_cli(self):
        self.a.cli = "inert-public-cli"
        self.a.cli_env = {"VOUCHFX_TELEMETRY_ENDPOINT": self.a.endpoint,
                          "VOUCHFX_TELEMETRY_TOKEN": self.a.token,
                          "UNCHANGED_MARKER": "preserve-child-environment"}
        self.a.state_dir = self.a.private / "cli-state"
        self.a.state_dir.mkdir()

    def request(self, path, body, token, key):
        self.assertEqual(path, "/v1/telemetry")
        payload = json.loads(body)
        if token != self.a.token:
            return 401
        if "suiteName" in payload:
            return 400
        self.fixture = payload
        return 200

    def command(self, argv, timeout=None, env=None):
        if argv[1:] == ["telemetry", "enable"]:
            (self.a.state_dir / "telemetry.json").write_text(json.dumps({"installId": self.install_id}))
            return ""
        self.assertEqual(argv[1], "run")
        self.assertIn("--fail-on-env-error", argv)
        self.assertIn("--fail-on-inconclusive", argv)
        self.assertEqual(env["UNCHANGED_MARKER"], "preserve-child-environment")
        self.runs.append((argv, env.copy()))
        if "VOUCHFX_TELEMETRY_TOKEN" in env:
            raise DeliveryBoundaryReached()
        event = dict(self.fixture, installId=self.install_id, toolVersion="1.0.5", engineVersion="1.0.5")
        self.line = json.dumps(event).encode()
        (self.a.state_dir / "telemetry-outbox.jsonl").write_bytes((self.line + b"\n") * self.event_count)
        junit = Path(argv[argv.index("--junit") + 1])
        ET.ElementTree(ET.Element("testsuite", tests="1", failures="0", errors="0", skipped="0")).write(junit)
        return ""

    def test_local_capture_precedes_configured_delivery_without_mutating_base_environment(self):
        with self.assertRaises(DeliveryBoundaryReached):
            self.a.exercise()
        self.assertEqual(len(self.runs), 2)
        self.assertNotIn("VOUCHFX_TELEMETRY_TOKEN", self.runs[0][1])
        self.assertEqual(self.runs[0][1]["VOUCHFX_TELEMETRY_ENDPOINT"], self.a.endpoint)
        self.assertEqual(self.runs[1][1]["VOUCHFX_TELEMETRY_TOKEN"], self.a.token)
        self.assertEqual(self.a.cli_env["VOUCHFX_TELEMETRY_TOKEN"], self.a.token)
        self.assertEqual((self.a.root / "engine-generated-schema2.ndjson").read_bytes(), self.line + b"\n")
        self.assertTrue((self.a.root / "producer-local-retained.junit.xml").is_file())
        self.assertFalse((self.a.state_dir / "telemetry-drain.json").exists())

    def assert_retained_count_refused(self, count):
        self.event_count = count
        with self.assertRaisesRegex(RuntimeError, "Require one retained real engine telemetry event"):
            self.a.exercise()
        self.assertEqual(len(self.runs), 1)
        self.assertFalse((self.a.root / "engine-generated-schema2.ndjson").exists())

    def test_zero_retained_events_refuse_before_delivery(self):
        self.assert_retained_count_refused(0)

    def test_multiple_retained_events_refuse_before_delivery(self):
        self.assert_retained_count_refused(2)


if __name__ == "__main__":
    unittest.main()
