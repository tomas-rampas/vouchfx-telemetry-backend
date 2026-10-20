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

    def prepare_opt_out(self, mutation=None):
        self.a.args.core_version = "1.1.0"
        self.public_cli()
        self.consent = self.a.state_dir / "telemetry.json"
        self.consent.write_text(json.dumps({"consent": 1, "installId": self.install_id}))
        self.outbox = self.a.state_dir / "telemetry-outbox.jsonl"
        self.outbox.write_bytes(b"")
        self.a.suite = self.a.private / "business.e2e.yaml"
        self.a.suite.write_text("metadata:\n  name: business-proof\nsteps:\n  - id: proof\n    type: script.csharp\n    code: 'return;'\n")
        self.a.rows = lambda identity: [dict(), dict()]
        self.a.sql = lambda query: "3"

        def command(argv, timeout=None, env=None):
            self.runs.append((argv, env.copy()))
            self.assertEqual(argv[1], "run")
            selected = Path(argv[2])
            if selected.is_dir():
                self.assertEqual(len(list(selected.glob("*.e2e.yaml"))), 2)
                self.assertNotIn("telemetry:", (selected / "legacy.e2e.yaml").read_text())
                self.assertIn("  telemetry: false\n", (selected / "opted-out.e2e.yaml").read_text())
            else:
                self.assertIn("metadata:\n  schemaVersion: v2\n  telemetry: false\n", selected.read_text())
            count = "2" if selected.is_dir() else "1"
            if mutation == "mixed-count" and selected.is_dir():
                count = "1"
            junit = Path(argv[argv.index("--junit") + 1])
            ET.ElementTree(ET.Element("testsuite", tests=count, failures="0", errors="0",
                                      skipped="1" if mutation == "business-skip" else "0")).write(junit)
            if mutation == "append":
                self.outbox.write_bytes(b"unexpected-event\n")
            elif mutation == "remote-row":
                self.a.sql = lambda query: "4"
            elif mutation == "consent":
                self.consent.write_text(json.dumps({"consent": 2, "installId": None}))
            return ""

        self.a.command = command

    def test_per_file_opt_out_checks_both_transports_and_mixed_business_passes(self):
        self.prepare_opt_out()
        result = self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)
        self.assertEqual(result, {"withoutTransport": True, "configuredTransport": True, "mixedSelection": True})
        self.assertEqual(len(self.runs), 3)
        self.assertNotIn("VOUCHFX_TELEMETRY_TOKEN", self.runs[0][1])
        self.assertEqual([env["VOUCHFX_TELEMETRY_TOKEN"] for _, env in self.runs[1:]], [self.a.token] * 2)
        self.assertTrue(all(env["VOUCHFX_TELEMETRY_ENDPOINT"] == self.a.endpoint for _, env in self.runs))
        self.assertEqual(self.a.cli_env["VOUCHFX_TELEMETRY_TOKEN"], self.a.token)

    def test_old_release_records_not_applicable_without_attempting_opt_out(self):
        result = self.a.per_file_opt_out(None, None, self.install_id)
        self.assertEqual(set(result.values()), {"not-applicable: Core before 1.1.0"})
        self.assertEqual(self.runs, [])

    def test_local_append_is_refused_before_configured_run(self):
        self.prepare_opt_out("append")
        with self.assertRaisesRegex(RuntimeError, "local outbox"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)
        self.assertEqual(len(self.runs), 1)

    def test_remote_row_increase_is_refused(self):
        self.prepare_opt_out("remote-row")
        with self.assertRaisesRegex(RuntimeError, "additional remote telemetry"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)

    def test_consent_or_identity_change_is_refused(self):
        self.prepare_opt_out("consent")
        with self.assertRaisesRegex(RuntimeError, "consent or the install identity"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)

    def test_opted_out_business_skip_is_not_accepted_as_pass(self):
        self.prepare_opt_out("business-skip")
        with self.assertRaisesRegex(RuntimeError, "unskipped PASS"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)

    def test_mixed_selection_requires_both_business_assertions(self):
        self.prepare_opt_out("mixed-count")
        with self.assertRaisesRegex(RuntimeError, "business assertion count"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)
        self.assertEqual(len(self.runs), 3)

    def test_opt_out_requires_previously_enabled_consent(self):
        self.prepare_opt_out()
        self.consent.write_text(json.dumps({"consent": 2, "installId": self.install_id}))
        with self.assertRaisesRegex(RuntimeError, "enabled consent"):
            self.a.per_file_opt_out(self.consent, self.outbox, self.install_id)
        self.assertEqual(self.runs, [])


if __name__ == "__main__":
    unittest.main()
