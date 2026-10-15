#!/usr/bin/env python3
"""Metadata authentication and public package admission without network or children."""
import copy
from email.message import Message
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
import urllib.response
import zipfile

import accept_self_hosted as helper


TOKEN = "fixture-metadata-credential"
COMMIT = "a" * 40
VERSION = "1.0.2"


class MetadataTransport(urllib.request.HTTPSHandler):
    def __init__(self, status=200, location=None, payload=b'{}'):
        super().__init__()
        self.status, self.location, self.payload = status, location, payload
        self.requests = []

    def https_open(self, request):
        self.requests.append(request)
        headers = Message()
        if self.location:
            headers['Location'] = self.location
        response = urllib.response.addinfourl(io.BytesIO(self.payload), headers, request.full_url, self.status)
        response.msg = "fixture response"
        return response


class MetadataTests(unittest.TestCase):
    def metadata(self, transport, path="/releases/tags/v1.0.2", token=TOKEN):
        original = urllib.request.build_opener
        with patch.object(helper.urllib.request, "build_opener",
                          side_effect=lambda *handlers: original(*handlers, transport)):
            return helper.github_metadata(path, token)

    def test_authentication_is_confined_to_fixed_metadata_origin(self):
        for path in ["/releases/tags/v1.0.2", "/git/ref/tags/v1.0.2", "/git/tags/" + COMMIT]:
            transport = MetadataTransport()
            self.assertEqual(self.metadata(transport, path), {})
            request = transport.requests[0]
            self.assertEqual(request.full_url, "https://api.github.com/repos/" + helper.CORE + path)
            self.assertTrue(request.get_header("Authorization") == "Bearer " + TOKEN)
            self.assertNotIn("Authorization", request.headers)

    def test_missing_or_malformed_token_is_refused_without_request(self):
        for token in ["", " ", "fixture\ncredential"]:
            transport = MetadataTransport()
            with self.assertRaisesRegex(RuntimeError, "workflow GitHub metadata token"):
                self.metadata(transport, token=token)
            self.assertEqual(transport.requests, [])

    def test_arbitrary_paths_origins_queries_and_fragments_are_refused(self):
        for path in ["https://other.invalid/", "//other.invalid/", "/releases/tags/v1.0.2?token=x",
                     "/releases/tags/v1.0.2#x", "/git/tags/../other", "/releases/tags/v1.0.2\n"]:
            with self.assertRaisesRegex(RuntimeError, "Unexpected GitHub metadata path"):
                self.metadata(MetadataTransport(), path)

    def test_all_redirects_are_refused_before_a_second_request(self):
        for status in [301, 302, 303, 307, 308]:
            for target in ["https://other.invalid/asset", "http://api.github.com/asset",
                           "https://api.nuget.org/asset", "https://api.github.com/other", "/other"]:
                with self.subTest(status=status, target=target):
                    transport = MetadataTransport(status, target)
                    with self.assertRaisesRegex(RuntimeError, "metadata redirects are refused") as caught:
                        self.metadata(transport)
                    self.assertEqual(len(transport.requests), 1)
                    self.assertNotIn(TOKEN, str(caught.exception))

    def test_metadata_error_and_size_limits_still_fail(self):
        with self.assertRaises(urllib.error.HTTPError):
            self.metadata(MetadataTransport(403))
        with self.assertRaisesRegex(RuntimeError, "Oversized public metadata"):
            self.metadata(MetadataTransport(payload=b" " * (2 * 1024 * 1024 + 1)))

    def test_token_is_consumed_before_children_and_redacted_from_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {"RUNNER_TEMP": directory, "VOUCHFX_ACCEPTANCE_GITHUB_TOKEN": TOKEN}):
                acceptance = helper.Acceptance(SimpleNamespace(evidence_dir=str(Path(directory) / "evidence")))
                self.assertNotIn("VOUCHFX_ACCEPTANCE_GITHUB_TOKEN", os.environ)
                self.assertTrue(acceptance.github_token == TOKEN)
                self.assertEqual(acceptance.redact(TOKEN), "<redacted>")
                helper.write_json(acceptance.root / "receipt.json", {"unexpectedEcho": TOKEN})
                acceptance.publish_artifacts()
                self.assertNotIn(TOKEN, (acceptance.root / "published/receipt.json").read_text())

    def test_missing_workflow_token_refuses_before_creating_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory) / "evidence"
            with patch.dict(os.environ, {"VOUCHFX_ACCEPTANCE_GITHUB_TOKEN": ""}):
                with self.assertRaisesRegex(RuntimeError, "workflow GitHub metadata token"):
                    helper.Acceptance(SimpleNamespace(evidence_dir=str(evidence)))
            self.assertFalse(evidence.exists())


class PublicPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.a = helper.Acceptance.__new__(helper.Acceptance)
        self.a.root = Path(self.temp.name)
        self.a.private = self.a.root / "private"
        self.a.private.mkdir()
        self.a.github_token = TOKEN
        self.a.endpoint, self.a.token = "http://127.0.0.1:12345", "fixture-ingest-credential"
        self.release = {"draft": False, "prerelease": False, "published_at": "2026-01-01T00:00:00Z"}
        self.reference = {"object": {"type": "commit", "sha": COMMIT}}
        self.metadata_calls, self.public_calls, self.commands = [], [], []
        self.package = self.package_bytes()
        self.a.args = SimpleNamespace(core_version=VERSION, core_commit=COMMIT,
                                      core_package_sha256=hashlib.sha256(self.package).hexdigest())
        self.a.http = SimpleNamespace(open=self.public_open)
        self.a.command = self.command

    def package_bytes(self, **overrides):
        values = dict(id="vouchfx", version=VERSION, commit=COMMIT,
                      url="https://github.com/" + helper.CORE)
        values.update(overrides)
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            archive.writestr("vouchfx.nuspec", '<package><metadata><id>{id}</id><version>{version}</version>'
                             '<repository commit="{commit}" url="{url}"/></metadata></package>'.format(**values))
        return data.getvalue()

    def public_open(self, url, timeout):
        self.public_calls.append(url)
        self.assertIsInstance(url, str)  # No authenticated Request reaches the public opener.
        self.assertEqual(url, "https://api.nuget.org/v3-flatcontainer/vouchfx/1.0.2/vouchfx.1.0.2.nupkg")
        return io.BytesIO(self.package)

    def metadata(self, path, token):
        self.assertTrue(token == TOKEN)
        self.metadata_calls.append(path)
        return copy.deepcopy(self.release if path.startswith("/releases/") else self.reference)

    def command(self, argv, **kwargs):
        self.commands.append((argv, kwargs))
        self.assertNotIn("VOUCHFX_ACCEPTANCE_GITHUB_TOKEN", kwargs['env'])
        return VERSION + "+" + COMMIT if argv[-1] == "--version" else ""

    def run_public_cli(self):
        with patch.object(helper, "github_metadata", side_effect=self.metadata):
            self.a.public_cli()

    def test_public_package_remains_anonymous_and_exact_hash_bound(self):
        self.run_public_cli()
        self.assertEqual(len(self.public_calls), 1)
        self.assertEqual(len(self.commands), 2)
        receipt = (self.a.root / "public-cli-tuple.json").read_text()
        self.assertNotIn(TOKEN, receipt)
        self.assertEqual(json.loads(receipt)['packageSha256'], self.a.args.core_package_sha256)

    def test_unpublished_draft_or_prerelease_never_downloads(self):
        for field, value in [("draft", True), ("prerelease", True), ("published_at", None)]:
            with self.subTest(field=field):
                self.release[field] = value
                with self.assertRaisesRegex(RuntimeError, "stable published Core release"):
                    self.run_public_cli()
                self.release[field] = False if field != "published_at" else "2026-01-01T00:00:00Z"
        self.assertEqual(self.public_calls, [])

    def test_different_public_tag_source_never_downloads(self):
        self.reference['object']['sha'] = "b" * 40
        with self.assertRaisesRegex(RuntimeError, "Public tag differs"):
            self.run_public_cli()
        self.assertEqual(self.public_calls, [])

    def test_changed_public_package_bytes_never_install(self):
        self.a.args.core_package_sha256 = "0" * 64
        with self.assertRaisesRegex(RuntimeError, "Official NuGet bytes differ"):
            self.run_public_cli()
        self.assertEqual(self.commands, [])

    def test_wrong_package_identity_or_repository_never_installs(self):
        for change in [dict(id="other"), dict(version="1.0.3"), dict(commit="b" * 40),
                       dict(url="https://other.invalid/repository")]:
            with self.subTest(field=next(iter(change))):
                self.package = self.package_bytes(**change)
                self.a.args.core_package_sha256 = hashlib.sha256(self.package).hexdigest()
                with self.assertRaises(RuntimeError):
                    self.run_public_cli()
                (self.a.private / "vouchfx.1.0.2.nupkg").unlink()
        self.assertEqual(self.commands, [])


if __name__ == "__main__":
    unittest.main()
