#!/usr/bin/env python3
"""Owned listener lifecycle regressions with inert Docker and HTTP boundaries."""
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import urllib.error

import accept_self_hosted as helper


class EndpointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        with patch.dict(os.environ, {"RUNNER_TEMP": self.temp.name,
                                     "VOUCHFX_ACCEPTANCE_GITHUB_TOKEN": "inert-fixture-credential"}):
            self.a = helper.Acceptance(SimpleNamespace(evidence_dir=str(Path(self.temp.name) / "evidence")))
        self.identifier = "a" * 64
        self.a.containers['backend'] = self.a.listener_container = self.identifier
        self.owner_label = self.a.owner
        self.initial_port, self.restarted_port = "32768", "32769"
        self.restarted = False
        self.binding_override = None
        self.docker_calls, self.http_calls = [], []
        self.ready_status = self.health_status = 200
        self.a.docker = self.docker
        self.a.http = SimpleNamespace(open=self.http)

    def docker(self, *args, **kwargs):
        self.docker_calls.append((args, kwargs))
        self.assertEqual(args[-1], self.identifier)
        if args == ('inspect', '--format', '{{index .Config.Labels "' + helper.LABEL + '"}}', self.identifier):
            return self.owner_label
        if args == ('inspect', '--format', '{{json .NetworkSettings.Ports}}', self.identifier):
            binding = self.binding_override
            if binding is None:
                binding = {'8080/tcp': [{'HostIp': '127.0.0.1',
                                         'HostPort': self.restarted_port if self.restarted else self.initial_port}]}
            return json.dumps(binding)
        if args == ('restart', '--time', '10', self.identifier):
            self.assertEqual(kwargs, {'timeout': 30})
            self.restarted = True
            return self.identifier
        self.fail('Unexpected Docker boundary')

    def http(self, request, timeout):
        self.assertEqual(timeout, 5)
        self.assertIsNone(request.get_header('Authorization'))
        self.http_calls.append(request.full_url)
        port = self.restarted_port if self.restarted else self.initial_port
        if not request.full_url.startswith('http://127.0.0.1:' + port + '/'):
            raise urllib.error.URLError('sensitive diagnostic canary must never be serialized')
        response = io.BytesIO(b'')
        response.status = self.ready_status if request.full_url.endswith('/readyz') else self.health_status
        return response

    def record(self, name):
        return json.loads((self.a.root / name).read_bytes())

    def initial_ready(self):
        self.a.refresh_endpoint('initial')
        self.a.wait_ready('initial')

    def test_repeated_monotonic_timestamp_retains_both_http_diagnostics(self):
        self.a.endpoint = 'http://127.0.0.1:32768'
        with patch.object(helper.time, 'monotonic_ns', return_value=123):
            self.assertEqual(self.a.request('/readyz'), 200)
            self.assertEqual(self.a.request('/healthz'), 200)

        diagnostics = [json.loads(path.read_bytes()) for path in self.a.logs.glob('http-*.json')]
        self.assertEqual(len(diagnostics), 2)
        self.assertCountEqual(
            [(item['path'], item['status']) for item in diagnostics],
            [('/readyz', 200), ('/healthz', 200)],
        )

    def test_restart_refreshes_changed_port_before_readiness_on_same_owned_container(self):
        self.initial_ready()
        self.a.restart_backend()
        record = self.record('listener-restarted.json')
        self.assertEqual(record['ownedContainer'], self.identifier)
        self.assertEqual(record['previousEndpoint'], 'http://127.0.0.1:32768')
        self.assertEqual(record['endpoint'], 'http://127.0.0.1:32769')
        self.assertTrue(record['portChanged'])
        self.assertEqual(self.http_calls[-2:], ['http://127.0.0.1:32769/readyz', 'http://127.0.0.1:32769/healthz'])
        self.assertTrue(self.record('readiness-restarted.json')['ready'])
        label_reads = [args for args, _ in self.docker_calls if helper.LABEL in str(args)]
        self.assertEqual(len(label_reads), 3)  # Initial lookup, before restart, after restart.

    def test_restart_with_same_port_still_rechecks_ownership_and_binding(self):
        self.restarted_port = self.initial_port
        self.initial_ready()
        self.a.restart_backend()
        self.assertFalse(self.record('listener-restarted.json')['portChanged'])
        port_reads = [args for args, _ in self.docker_calls if 'NetworkSettings.Ports' in str(args)]
        self.assertEqual(len(port_reads), 2)
        self.assertTrue(self.record('readiness-restarted.json')['ready'])

    def test_cached_port_counterfactual_fails_then_refresh_succeeds(self):
        self.initial_ready()
        self.a.docker('restart', '--time', '10', self.identifier, timeout=30)
        # Model the original cached-endpoint behavior without waiting or a real child/network.
        with patch.object(helper.time, 'monotonic', side_effect=[0, 0, 30, 60, 90]), \
                patch.object(helper.time, 'sleep'):
            with self.assertRaisesRegex(RuntimeError, 'within 90 seconds'):
                self.a.wait_ready('cached-counterfactual')
        failure = self.record('readiness-cached-counterfactual.json')
        self.assertEqual(failure['timeoutSeconds'], 90)
        self.assertEqual(failure['lastHttpFailure'], {'path': '/readyz', 'errorType': 'URLError'})
        encoded = json.dumps(failure)
        self.assertNotIn('sensitive diagnostic canary', encoded)
        self.assertLess(len(encoded), 600)
        self.a.refresh_endpoint('restarted')
        self.a.wait_ready('restarted')
        self.assertTrue(self.record('readiness-restarted.json')['ready'])

    def test_missing_multiple_wildcard_foreign_and_malformed_bindings_are_refused(self):
        invalid = [[], {}, {'8080/tcp': None}, {'8080/tcp': []}, {'8080/tcp': [None]},
                   {'8080/tcp': [{}, {}]}, {'8080/tcp': [{'HostIp': '127.0.0.1'}]}]
        for host in ['0.0.0.0', '::', '::1', '127.0.0.2', 'other.invalid']:
            invalid.append({'8080/tcp': [{'HostIp': host, 'HostPort': '32768'}]})
        for port in [None, True, 32768, '', '0', '-1', '65536', '12.3', '1e3', '1\n', '1:80']:
            invalid.append({'8080/tcp': [{'HostIp': '127.0.0.1', 'HostPort': port}]})
        for binding in invalid:
            with self.subTest(binding=binding):
                self.binding_override = binding
                with self.assertRaises(RuntimeError):
                    self.a.refresh_endpoint('initial')
                self.assertFalse(hasattr(self.a, 'endpoint'))
        self.assertEqual(self.http_calls, [])

    def test_changed_backend_identity_is_refused_before_any_docker_or_http(self):
        self.a.containers['backend'] = 'b' * 64
        with self.assertRaisesRegex(RuntimeError, 'Owned backend identity changed'):
            self.a.refresh_endpoint('initial')
        with self.assertRaisesRegex(RuntimeError, 'Owned backend identity changed'):
            self.a.restart_backend()
        self.assertEqual(self.docker_calls, [])
        self.assertEqual(self.http_calls, [])

    def test_foreign_run_label_is_refused_at_each_lookup_and_before_restart(self):
        self.initial_ready()
        count = len(self.http_calls)
        for label in ['', 'another-run-owner']:
            with self.subTest(label=label):
                self.owner_label = label
                with self.assertRaisesRegex(RuntimeError, 'unowned Docker object'):
                    self.a.refresh_endpoint('restarted')
                with self.assertRaisesRegex(RuntimeError, 'unowned Docker object'):
                    self.a.restart_backend()
        self.assertFalse(self.restarted)
        self.assertEqual(len(self.http_calls), count)

    def test_same_port_readiness_failure_preserves_status_and_original_timeout(self):
        self.a.refresh_endpoint('initial')
        self.ready_status = 503
        with patch.object(helper.time, 'monotonic', side_effect=[0, 0, 30, 60, 90]), \
                patch.object(helper.time, 'sleep'):
            with self.assertRaisesRegex(RuntimeError, 'within 90 seconds'):
                self.a.wait_ready('initial')
        diagnostic = self.record('readiness-initial.json')
        self.assertFalse(diagnostic['ready'])
        self.assertEqual(diagnostic['lastHttpFailure'], {'path': '/readyz', 'status': 503})
        self.assertEqual(diagnostic['attempts'], 3)
        self.assertEqual(diagnostic['timeoutSeconds'], 90)

    def test_failed_liveness_remains_immediate_and_has_bounded_diagnostics(self):
        self.a.refresh_endpoint('initial')
        self.health_status = 500
        with patch.object(helper.time, 'sleep') as sleep:
            with self.assertRaisesRegex(RuntimeError, 'Liveness failed'):
                self.a.wait_ready('initial')
            sleep.assert_not_called()
        diagnostic = self.record('readiness-initial.json')
        self.assertFalse(diagnostic['ready'])
        self.assertEqual(diagnostic['lastHttpFailure'], {'path': '/healthz', 'status': 500})
        self.assertNotIn('responseBody', json.dumps(diagnostic))


if __name__ == '__main__':
    unittest.main()
