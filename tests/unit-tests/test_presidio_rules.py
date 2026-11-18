"""Unit checks for the local detection bridge: spans only, per-feed registries from the Presidio-style file, timeouts."""
from pathlib import Path
import json
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "PresidioDemo.Core" / "Protection" / "python"))
import presidio_demo


SPIKE = Path(__file__).resolve().parents[2] / "tests" / "DetectionParityChecks" / "bin" / "Debug" / "net10.0" / "DetectionParityChecks.dll"


def config(feed, disabled=(), recognizers=()):
    """Return the bridge JSON for a feed: switched-off predefined recognizers and custom recognizers."""
    return json.dumps({"feed": feed, "language": "en", "disabled": list(disabled), "recognizers": list(recognizers)})


def custom(name, entity, *patterns, deny_list=(), score=0.9):
    """Return one custom recognizer for the bridge JSON."""
    return {"name": name, "entity": entity, "patterns": [{"name": name, "regex": p, "score": score} for p in patterns],
            "context": [], "denyList": list(deny_list), "denyListScore": 1.0}


def spans(feed, *texts):
    """Return (entity, matched text, recognizer) per input string from the configured feed."""
    result = json.loads(presidio_demo.analyze_strings(feed, json.dumps(list(texts))))
    return [[(entity, text[start:end], name) for start, end, entity, score, name in found] for text, found in zip(texts, result)]


class DetectionBridgeTests(unittest.TestCase):
    """The batch detection bridge (T-006): spans only, per-feed registries, no protection decisions."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run(["dotnet", str(SPIKE), "bridge-json"], capture_output=True, text=True, timeout=300)
        if run.returncode != 0:
            raise AssertionError("Build tests/DetectionParityChecks first: " + run.stdout + run.stderr)
        cls.feeds = [presidio_demo.configure_feed(line) for line in run.stdout.splitlines() if line.strip()]
        presidio_demo.configure_feed(config("OnlyA", recognizers=[custom("demoCode", "DEMO_CODE", r"DEMO-\d{4}")]))
        presidio_demo.configure_feed(config("OnlyB"))
        presidio_demo.configure_feed(config("NoCards", disabled=["CreditCardRecognizer"]))
        presidio_demo.configure_feed(config("Titles", recognizers=[custom("titles", "TITLE", deny_list=["MR", "DR"])]))

    def test_the_seats_feed_configures(self):
        self.assertEqual(sorted(self.feeds), ["Seats"])

    def test_names_in_free_text_return_surname_given_spans(self):
        found = spans("Seats", "REF 1234 MILLER/JORDAN, 23C REED/AVA")[0]
        self.assertEqual([t for e, t, n in found if n == "passengerName"], ["MILLER/JORDAN", "REED/AVA"])
        self.assertTrue(all(e == "PERSON" for e, t, n in found if n == "passengerName"))

    def test_key_only_recognizers_stay_in_dotnet(self):
        # PaxId, OrderId, EmpId and the attribute keys are decided by property name in .NET; the bridge never sees them.
        found = spans("Seats", "PAX-1001", "DA012K7QX2M00", "PETINCABIN")
        self.assertFalse(any(n in ("passengerId", "orderId", "employeeId", "serviceAttributes") for spans_ in found for e, t, n in spans_))

    def test_built_in_email_is_found_in_free_text(self):
        found = spans("Seats", "Contact emily.carter@example.com today")[0]
        self.assertIn("EMAIL_ADDRESS", [entity for entity, text, name in found])

    def test_a_custom_recognizer_applies_only_to_its_own_feed(self):
        self.assertIn(("DEMO_CODE", "DEMO-1234", "demoCode"), spans("OnlyA", "ref DEMO-1234")[0])
        self.assertNotIn("demoCode", [n for e, t, n in spans("OnlyB", "ref DEMO-1234")[0]])
        self.assertNotIn("passengerName", [n for e, t, n in spans("OnlyB", "REF 1234 MILLER/JORDAN")[0]])

    def test_an_empty_file_runs_every_predefined_recognizer(self):
        found = spans("OnlyB", "Card 4111 1111 1111 1111", "Contact Alex Sample")
        self.assertIn(("CREDIT_CARD", "4111 1111 1111 1111", "CreditCardRecognizer"), found[0])
        self.assertIn(("PERSON", "Alex Sample", "SpacyRecognizer"), found[1])

    def test_a_switched_off_predefined_recognizer_detects_nothing(self):
        self.assertNotIn("CREDIT_CARD", [e for e, t, n in spans("NoCards", "Card 4111 1111 1111 1111")[0]])

    def test_an_unknown_predefined_recognizer_name_is_rejected(self):
        with self.assertRaises(ValueError):
            presidio_demo.configure_feed(config("Typo", disabled=["CreditCardRecogniser"]))

    def test_a_deny_list_recognizer_detects_its_words(self):
        self.assertIn(("TITLE", "DR", "titles"), spans("Titles", "SEE DR LI")[0])

    def test_a_pattern_timeout_raises_instead_of_skipping_the_pattern(self):
        # Presidio would skip a timed-out pattern and return no span; the bridge raises so .NET dead-letters (WR-016).
        presidio_demo.configure_feed(config("Slow", recognizers=[custom("slow", "DEMO_CODE", r"(A|AA)+$")]))
        with self.assertRaises(TimeoutError):
            presidio_demo.analyze_strings("Slow", json.dumps(["A" * 40 + "!"]))
        self.assertIn("passengerName", [n for e, t, n in spans("Seats", "MILLER/JORDAN")[0]], "the next call works normally")

    def test_offsets_are_utf16_so_dotnet_masks_the_right_characters(self):
        text = "\U0001F600 CONTACT MILLER/JORDAN"
        found = json.loads(presidio_demo.analyze_strings("Seats", json.dumps([text])))[0]
        name = [span for span in found if span[4] == "passengerName"][0]
        utf16 = text.encode("utf-16-le")
        # The name pattern allows spaces, so the match starts at CONTACT, right after the emoji (two UTF-16 units) and a space.
        self.assertEqual(name[0], 3)
        self.assertEqual(utf16[name[0] * 2:name[1] * 2].decode("utf-16-le"), "CONTACT MILLER/JORDAN")

    def test_batch_keeps_one_result_per_string_and_rejects_bad_input(self):
        self.assertEqual(len(spans("Seats", "20261005", "MILLER/JORDAN", "")), 3)
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("Seats", json.dumps([1, 2]))
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("NoSuchFeed", "[]")


if __name__ == "__main__":
    unittest.main()
