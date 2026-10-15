#!/usr/bin/env python3
"""Pure admission regressions; no helper runtime, child, Docker or network is used."""
import unittest
import xml.etree.ElementTree as ET

import test_accept_self_hosted_controls as controls


def suite():
    root = ET.Element("testsuite", name="telemetry-acceptance-failure-controls",
                      tests="9", failures="0", errors="0", skipped="0")
    for name in controls.EXPECTED_CASES:
        ET.SubElement(root, "testcase", name=name)
    return root


def encoded(root):
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


class JUnitAdmissionTests(unittest.TestCase):
    def test_generated_compact_suite_is_admitted(self):
        controls.validate_junit(encoded(suite()))

    def test_pretty_printed_formatting_whitespace_is_admitted(self):
        root = suite()
        root.text, root.tail = "\n  ", "\n"
        for case in root:
            case.text, case.tail = " \t\r\n", "\n  "
        controls.validate_junit(encoded(root))

    def test_suite_text_payloads_are_refused(self):
        for payload in ("unexpected payload", controls.DUMMY_SECRET, "private-π-value", "\u00a0"):
            with self.subTest(payload=payload):
                root = suite()
                root.text = payload
                controls.refused(lambda: controls.validate_junit(encoded(root)), AssertionError)

    def test_every_testcase_text_and_tail_payload_is_refused(self):
        for index in range(len(controls.EXPECTED_CASES)):
            for field in ("text", "tail"):
                for payload in ("unexpected payload", controls.DUMMY_SECRET):
                    with self.subTest(case=index, field=field, sentinel=payload == controls.DUMMY_SECRET):
                        root = suite()
                        setattr(root[index], field, payload)
                        controls.refused(lambda: controls.validate_junit(encoded(root)), AssertionError)

    def test_nonwhitespace_after_the_root_is_refused(self):
        for payload in ("unexpected payload", controls.DUMMY_SECRET):
            with self.subTest(sentinel=payload == controls.DUMMY_SECRET):
                root = suite()
                root.tail = payload
                with self.assertRaises((AssertionError, ET.ParseError)):
                    controls.validate_junit(encoded(root))

    def test_character_reference_credential_is_refused(self):
        root = suite()
        root.text = controls.DUMMY_SECRET
        reference = "".join("&#x" + format(ord(c), "x") + ";" for c in controls.DUMMY_SECRET)
        xml = encoded(root).replace(controls.DUMMY_SECRET.encode(), reference.encode())
        self.assertNotIn(controls.DUMMY_SECRET.encode(), xml)
        with self.assertRaisesRegex(AssertionError, "formatting whitespace only"):
            controls.validate_junit(xml)

    def test_unexpected_children_are_refused_even_without_text(self):
        for tag in ("failure", "system-out", "properties"):
            for payload in (None, "unexpected payload", controls.DUMMY_SECRET):
                with self.subTest(tag=tag, payload=payload):
                    root = suite()
                    ET.SubElement(root[0], tag).text = payload
                    controls.refused(lambda: controls.validate_junit(encoded(root)), AssertionError)

    def test_comments_and_processing_instructions_are_never_discarded(self):
        for node in (ET.Comment, lambda text: ET.ProcessingInstruction("control", text)):
            for payload in ("unexpected payload", controls.DUMMY_SECRET):
                for location in ("before-root", "suite", "testcase", "after-root"):
                    with self.subTest(node=node, location=location, sentinel=payload == controls.DUMMY_SECRET):
                        root = suite()
                        extra = node(payload)
                        if location in ("suite", "testcase"):
                            (root if location == "suite" else root[0]).append(extra)
                            xml = encoded(root)
                        else:
                            body = ET.tostring(root, encoding="utf-8")
                            token = ET.tostring(extra, encoding="utf-8")
                            xml = token + body if location == "before-root" else body + token
                        controls.refused(lambda: controls.validate_junit(xml), AssertionError)

    def test_empty_comments_and_processing_instructions_are_refused(self):
        for node in (ET.Comment(""), ET.ProcessingInstruction("control", "")):
            with self.subTest(node=node.tag):
                root = suite()
                root.append(node)
                controls.refused(lambda: controls.validate_junit(encoded(root)), AssertionError)

    def test_non_utf8_reports_are_refused(self):
        text = ET.tostring(suite(), encoding="unicode")
        for encoding in ("utf-16", "utf-32"):
            with self.subTest(encoding=encoding):
                with self.assertRaises((UnicodeError, ET.ParseError)):
                    controls.validate_junit(text.encode(encoding))

    def test_size_and_entity_guards_remain_enforced(self):
        controls.refused(lambda: controls.validate_junit(b" " * (128 * 1024 + 1)), AssertionError)
        for prefix in (b'<!DOCTYPE testsuite>', b'<!ENTITY credential "secret">'):
            with self.subTest(prefix=prefix):
                controls.refused(lambda: controls.validate_junit(prefix + encoded(suite())), AssertionError)

    def test_summary_and_case_identity_guards_remain_enforced(self):
        for mutation in ("summary", "name", "order", "missing", "extra"):
            with self.subTest(mutation=mutation):
                root = suite()
                if mutation == "summary":
                    root.set("failures", "1")
                elif mutation == "name":
                    root[0].set("name", "unknown-case")
                elif mutation == "order":
                    root[0].set("name", controls.EXPECTED_CASES[1])
                elif mutation == "missing":
                    root.remove(root[-1])
                else:
                    ET.SubElement(root, "testcase", name="unknown-case")
                controls.refused(lambda: controls.validate_junit(encoded(root)), AssertionError)


if __name__ == "__main__":
    unittest.main()
