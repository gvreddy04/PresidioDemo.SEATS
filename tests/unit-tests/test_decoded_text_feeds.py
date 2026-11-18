"""The Seats feed through the real workers, the JSON codec and local Presidio (V-07, V-08, V-05 through the harness)."""
import json
import re
import unittest

from decoded_text_support import EXPECTED_REJECTIONS, PROJECT, baseline, in_scope, json_value, payload, verify_published
from pipeline_harness import run
from test_feed_logs import blocks, leaked_values

SAMPLES = PROJECT / "docs" / "sample-docs"


def compact(document):
    """Serialize JSON as a raw payload, independently of the codec under test."""
    return json.dumps(document, separators=(",", ":")).encode("utf-8")


class DecodedTextFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        inputs = {f"{p.parent.name}/{p.name}": p.read_bytes() for p in sorted(SAMPLES.rglob("*.txt"))}
        seats = json.loads(payload((SAMPLES / "Seats" / "SEATS.txt").read_bytes()))
        # Template changes (AC-007): the producer adds keys, removes one, and reverses every key order. Values are made up.
        cls.added_note, cls.added_code = "CALL MILLER/JORDAN 3125550142", "NEWFIELD9"
        added = json.loads(json.dumps(seats))
        added["OrderChangeNotif"]["NewContactNote"] = cls.added_note
        added["OrderChangeNotif"]["NewCode"] = cls.added_code
        inputs["Seats/template-added-key.bin"] = compact(added)
        removed = json.loads(json.dumps(seats))
        del removed["OrderChangeNotif"]["Old"]["Customers"][0]["LastName"]
        inputs["Seats/template-removed-key.bin"] = compact(removed)

        def reverse(node):
            if isinstance(node, dict):
                return {key: reverse(value) for key, value in reversed(list(node.items()))}
            return [reverse(item) for item in node] if isinstance(node, list) else node

        inputs["Seats/template-reordered-keys.bin"] = compact(reverse(seats))
        cls.inputs = inputs
        cls.harness = run(inputs, extra_args=["--detection-log", "on"], expect=2 if EXPECTED_REJECTIONS else 0, prefix="decoded-text-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_both_production_samples_published_and_the_negative_sample_rejected_with_its_reason(self):
        items = [i for i in self.harness.report["Items"] if "template-" not in i["Source"]]
        published = [i for i in items if i["Status"] == "Published"]
        rejected = {i["Source"].replace("\\", "/").rsplit("/", 1)[-1]: i["Reason"] for i in items if i["Status"] == "Rejected"}
        self.assertEqual(len(published), 2)
        self.assertEqual(set(rejected), set(EXPECTED_REJECTIONS))
        for name, reason in EXPECTED_REJECTIONS.items():
            self.assertIn(reason, rejected[name])

    def test_published_samples_keep_framing_and_protect_every_name_of_the_baseline(self):
        document, exceptions = baseline()
        totals = {"protected": 0, "approved_exceptions": 0, "out_of_scope": 0}
        for key, original in self.inputs.items():
            feed, name = key.split("/")
            if "template-" in name or name in EXPECTED_REJECTIONS:
                continue
            counts = verify_published(feed, name, original, self.harness.published(feed, name), document, exceptions)
            for k in totals:
                totals[k] += counts[k]
        self.assertEqual(totals["protected"] + totals["approved_exceptions"], len(in_scope(document)))
        self.assertEqual(totals["out_of_scope"], len(document["protected"]) - len(in_scope(document)), "every other baseline value is out of scope")
        self.assertEqual(totals["approved_exceptions"], len(exceptions))

    def test_template_changes_still_publish_with_detected_text_masked_and_undetected_text_unchanged(self):
        # WR-014: a string under a new key goes to Presidio; a string in which nothing is detected is published unchanged
        # (and listed for review in the log).
        added = json.loads(self.harness.published("Seats", "template-added-key.bin"))["OrderChangeNotif"]
        self.assertNotIn("JORDAN", added["NewContactNote"])
        self.assertEqual(len(added["NewContactNote"]), len(self.added_note))
        self.assertEqual(added["NewCode"], self.added_code, "the added, undetected string is published unchanged")

    def test_removed_and_reordered_template_keys_still_publish_protected(self):
        document, exceptions = baseline()
        original = json.loads(payload((SAMPLES / "Seats" / "SEATS.txt").read_bytes()))
        for name in ("template-removed-key.bin", "template-reordered-keys.bin"):
            output = json.loads(self.harness.published("Seats", name))
            checked = 0
            for e in in_scope(document):
                if e["sample"] != "SEATS.txt" or ("SEATS.txt", e["location"]) in exceptions or e["location"].endswith("/LastName"):
                    continue
                self.assertNotEqual(json_value(output, e["location"]), json_value(original, e["location"]),
                                    f"{name}: {e['entity']} at {e['location']} was published unprotected")
                checked += 1
            self.assertGreaterEqual(checked, 2)
            for e in document["pnrs"]:
                if e["sample"] == "SEATS.txt":
                    self.assertEqual(json_value(output, e["location"]), json_value(original, e["location"]), f"{name}: the PNR changed")

    def test_json_names_are_masked_at_their_width(self):
        customer = json.loads(payload(self.harness.published("Seats", "SEATS.txt")))["OrderChangeNotif"]["Old"]["Customers"][0]
        original = json.loads(payload((SAMPLES / "Seats" / "SEATS.txt").read_bytes()))["OrderChangeNotif"]["Old"]["Customers"][0]
        for key in ("FirstName", "LastName", "BookedAsName"):
            self.assertEqual(customer[key], "*" * len(original[key]), key)
        self.assertEqual(customer["PaxId"], original["PaxId"], "the passenger ID is outside the names-only file and published unchanged")
        self.assertEqual(customer["UARecloc"], original["UARecloc"], "the PNR stays unchanged")

    def test_detection_log_through_the_harness_is_value_free(self):
        found = blocks(self.harness.console)
        self.assertGreater(sum(len(b["entries"]) for b in found), 200)
        leaked = leaked_values("\n".join("\n".join(b["lines"][1:]) for b in found))
        self.assertEqual(leaked, [])


class DefaultDetectionHarnessTests(unittest.TestCase):
    """Presidio:DefaultDetection through the real workers (--default-detection): off acts on the file's definitions only and
    says so in the run summary, with no predefined or spaCy decision in the detection log; on adds Presidio's own."""

    MESSAGE = b'{"FirstName":"JORDAN","Note":"mail jordan.miller@example.com"}'  # made up

    @classmethod
    def setUpClass(cls):
        inputs = {"Seats/flag.bin": cls.MESSAGE}
        cls.off = run(inputs, extra_args=["--detection-log", "on"], expect=0, prefix="default-detection-off-")
        cls.addClassCleanup(cls.off.cleanup)
        cls.on = run(inputs, extra_args=["--detection-log", "on", "--default-detection", "on"], expect=0, prefix="default-detection-on-")
        cls.addClassCleanup(cls.on.cleanup)

    def test_off_masks_the_mapped_name_only(self):
        self.assertEqual(self.off.published("Seats", "flag.bin"), b'{"FirstName":"******","Note":"mail jordan.miller@example.com"}')

    def test_on_masks_the_email_address_too(self):
        self.assertEqual(self.on.published("Seats", "flag.bin"), b'{"FirstName":"******","Note":"mail *************************"}')

    def test_the_run_shows_its_mode_and_off_logs_no_predefined_decision(self):
        self.assertIn("HARNESS Default detection off:", self.off.console)
        self.assertIn("HARNESS Default detection on:", self.on.console)
        self.assertIn("Default detection off:", (self.off.output_root / "detection-summary.txt").read_text(encoding="utf-8"))
        entries = [line for b in blocks(self.off.console) for _, line in b["entries"]]
        self.assertEqual(len(entries), 2)
        self.assertTrue(all(re.search(r" recognizer=(entity_mapping|none) ", line) for line in entries), "only the mapped key decided")
        self.assertNotIn("spacy-guess", self.off.console)
        self.assertIn("recognizer=EmailRecognizer", self.on.console)


if __name__ == "__main__":
    unittest.main()
