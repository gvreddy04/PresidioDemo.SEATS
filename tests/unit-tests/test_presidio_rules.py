"""Unit checks for the local detection bridge: spans only, per-feed registries from the Presidio-style file built with or
without Presidio's predefined recognizers (Presidio:DefaultDetection), key-path context words and timeouts."""
from pathlib import Path
import json
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "PresidioDemo.Core" / "Protection" / "python"))
import presidio_demo


SPIKE = Path(__file__).resolve().parents[2] / "tests" / "DetectionParityChecks" / "bin" / "Debug" / "net10.0" / "DetectionParityChecks.dll"


def config(feed, disabled=(), recognizers=(), default_detection=False):
    """Return the bridge JSON for a feed: the default-detection switch, switched-off predefined recognizers and custom
    recognizers. Off by default, as Presidio:DefaultDetection ships."""
    return json.dumps({"feed": feed, "language": "en", "defaultDetection": default_detection, "disabled": list(disabled),
                       "recognizers": list(recognizers)})


def custom(name, entity, *patterns, deny_list=(), score=0.9, context=()):
    """Return one custom recognizer for the bridge JSON."""
    return {"name": name, "entity": entity, "patterns": [{"name": name, "regex": p, "score": score} for p in patterns],
            "context": list(context), "denyList": list(deny_list), "denyListScore": 1.0}


def spans(feed, *texts):
    """Return (entity, matched text, recognizer) per input string from the configured feed."""
    result = json.loads(presidio_demo.analyze_strings(feed, json.dumps(list(texts))))
    return [[(entity, text[start:end], name) for start, end, entity, score, name in found] for text, found in zip(texts, result)]


def recognizers(feed):
    """Return the recognizer names in a configured feed's registry, or an empty list for a feed without an engine."""
    engine = presidio_demo._feeds[feed][0]
    return [] if engine is None else sorted(r.name for r in engine.registry.recognizers)


def pipes_during(feed, *texts):
    """Return the spaCy components that were switched on while the bridge analyzed texts for a feed."""
    seen = []
    original = presidio_demo.nlp_engine.process_batch

    def spy(*args, **kwargs):
        seen.extend(presidio_demo.nlp_engine.get_nlp("en").pipe_names)
        return original(*args, **kwargs)

    presidio_demo.nlp_engine.process_batch = spy
    try:
        presidio_demo.analyze_strings(feed, json.dumps(list(texts)))
    finally:
        del presidio_demo.nlp_engine.process_batch
    return seen


def scores(feed, texts, contexts):
    """Return the highest score per input string, each analyzed with its own context words."""
    result = json.loads(presidio_demo.analyze_strings(feed, json.dumps(texts), json.dumps(contexts)))
    return [max((score for start, end, entity, score, name in found), default=0) for found in result]


class DetectionBridgeTests(unittest.TestCase):
    """The batch detection bridge (T-006): spans only, per-feed registries, no protection decisions."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run(["dotnet", str(SPIKE), "bridge-json"], capture_output=True, text=True, timeout=300)
        if run.returncode != 0:
            raise AssertionError("Build tests/DetectionParityChecks first: " + run.stdout + run.stderr)
        lines = [line for line in run.stdout.splitlines() if line.strip()]
        cls.bridge_json = lines[0]
        cls.feeds = [presidio_demo.configure_feed(line) for line in lines]
        # The shipped file again under another feed name, with Presidio:DefaultDetection on.
        shipped = json.loads(lines[0])
        presidio_demo.configure_feed(json.dumps(dict(shipped, feed="WithDefaults", defaultDetection=True)))
        presidio_demo.configure_feed(config("OnlyA", recognizers=[custom("demoCode", "DEMO_CODE", r"DEMO-\d{4}")]))
        presidio_demo.configure_feed(config("OnlyB", default_detection=True))
        presidio_demo.configure_feed(config("NothingDefined"))
        presidio_demo.configure_feed(config("NoCards", disabled=["CreditCardRecognizer"], default_detection=True))
        presidio_demo.configure_feed(config("Titles", recognizers=[custom("titles", "TITLE", deny_list=["MR", "DR"])]))
        presidio_demo.configure_feed(config("Context", disabled=["SpacyRecognizer"],
                                            recognizers=[custom("memberCode", "MEMBER_CODE", r"ZX\d{4}", score=0.3, context=["membership"])]))

    def test_the_seats_feed_configures(self):
        self.assertEqual(sorted(self.feeds), ["Seats"])
        self.assertFalse(json.loads(self.bridge_json)["defaultDetection"], "the shipped Presidio:DefaultDetection is off")

    def test_names_in_free_text_return_surname_given_spans(self):
        found = spans("Seats", "REF 1234 MILLER/JORDAN, 23C REED/AVA")[0]
        self.assertEqual([t for e, t, n in found if n == "passengerName"], ["MILLER/JORDAN", "REED/AVA"])
        self.assertTrue(all(e == "PERSON" for e, t, n in found if n == "passengerName"))

    def test_key_mappings_stay_in_dotnet(self):
        # The name keys are mapped by property name in .NET; the bridge never sees a key, so a bare name is no span.
        self.assertEqual(spans("Seats", "JORDAN", "MILLER"), [[], []])

    def test_key_path_context_words_raise_a_matching_recognizer(self):
        # Each string's property names reach Presidio's context enhancer, as BatchAnalyzerEngine.analyze_dict passes a key,
        # with default detection off as shipped.
        member, remark, none = scores("Context", ["ZX1234", "ZX1234", "ZX1234"], [["Customers", "MembershipCode"], ["Remarks"], []])
        self.assertGreaterEqual(member, 0.5)
        self.assertAlmostEqual(remark, 0.3)
        self.assertAlmostEqual(none, 0.3)

    def test_a_context_word_in_the_text_still_raises_a_match_with_default_detection_off(self):
        # spaCy's lemmas reach the context enhancer, so a context word next to a match works as with default detection on.
        near, far = scores("Context", ["membership ZX1234", "ZX1234"], [[], []])
        self.assertGreaterEqual(near, 0.5)
        self.assertAlmostEqual(far, 0.3)

    def test_contexts_must_be_one_list_of_words_per_string(self):
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("Context", json.dumps(["ZX1234"]), json.dumps([]))
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("Context", json.dumps(["ZX1234"]), json.dumps([[1]]))

    def test_default_detection_off_runs_only_the_files_custom_recognizers(self):
        found = spans("Seats", "mail jordan.miller@example.com", "Contact Alex Sample", "REF MILLER/JORDAN")
        self.assertEqual(found[0], [], "no predefined recognizer reads the e-mail address")
        self.assertEqual(found[1], [], "spaCy PERSON does not run")
        self.assertEqual({name for entity, text, name in found[2]}, {"passengerName"})
        self.assertEqual(recognizers("Seats"), ["passengerName"])

    def test_default_detection_on_runs_the_custom_and_the_predefined_recognizers(self):
        found = spans("WithDefaults", "mail jordan.miller@example.com", "REF MILLER/JORDAN")
        self.assertIn(("EMAIL_ADDRESS", "jordan.miller@example.com", "EmailRecognizer"), found[0])
        self.assertIn("passengerName", [name for entity, text, name in found[1]])
        self.assertIn("SpacyRecognizer", recognizers("WithDefaults"))

    def test_default_detection_on_without_definitions_runs_only_presidio(self):
        found = spans("OnlyB", "mail jordan.miller@example.com")[0]
        self.assertIn(("EMAIL_ADDRESS", "jordan.miller@example.com", "EmailRecognizer"), found)
        self.assertNotIn("passengerName", recognizers("OnlyB"))

    def test_default_detection_off_without_definitions_detects_nothing(self):
        self.assertEqual(recognizers("NothingDefined"), [], "no registry is built, so Presidio is not called")
        self.assertEqual(spans("NothingDefined", "mail jordan.miller@example.com", "Contact Alex Sample"), [[], []])

    def test_spacy_ner_runs_only_with_default_detection_on(self):
        # Off, spaCy runs only for a custom recognizer with context words, and then without its named-entity component;
        # the shipped passengerName recognizer has none, so spaCy does not run at all.
        self.assertEqual(pipes_during("Seats", "REF MILLER/JORDAN"), [], "spaCy does not run")
        context = pipes_during("Context", "membership ZX1234")
        self.assertIn("lemmatizer", context)
        self.assertNotIn("ner", context)
        self.assertIn("ner", pipes_during("WithDefaults", "REF MILLER/JORDAN"))
        self.assertIn("ner", presidio_demo.nlp_engine.get_nlp("en").pipe_names, "the shared pipeline is restored after the call")

    def test_default_detection_must_be_true_or_false(self):
        with self.assertRaises(ValueError):
            presidio_demo.configure_feed(json.dumps({"feed": "Typo", "language": "en", "defaultDetection": "yes", "disabled": [], "recognizers": []}))

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
        # Checked with default detection off too, although no predefined recognizer runs then.
        for default_detection in (False, True):
            with self.assertRaises(ValueError):
                presidio_demo.configure_feed(config("Typo", disabled=["CreditCardRecogniser"], default_detection=default_detection))

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
