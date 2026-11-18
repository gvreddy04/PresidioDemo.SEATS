"""Embedded local Presidio. .NET supplies one validated recognizer set per feed.
Decoding, serialization and every protection decision stay in .NET; detection returns spans only.
No remote processing or runtime model downloads.
"""
import importlib.util
import json
import logging
import os

# Presidio reads its regex time limit (whole seconds) once, at import. A pattern that times out is skipped with a warning,
# which would publish what it should have found; the handler below turns that warning into a detection failure so the
# message is dead-lettered instead (WR-016). One second keeps a runaway pattern from holding the engine for long.
os.environ.setdefault("REGEX_TIMEOUT_SECONDS", "1")

from presidio_analyzer import AnalyzerEngine, Pattern, PatternRecognizer, RecognizerRegistry, RecognizerResult
from presidio_analyzer.nlp_engine import NlpEngineProvider

if importlib.util.find_spec("en_core_web_lg") is None:
    raise RuntimeError("Install the bundled en_core_web_lg model before starting the listener.")
# One spaCy engine (en_core_web_lg, Presidio's default configuration) shared by every feed's registry.
nlp_engine = NlpEngineProvider().create_engine()


class _TimeoutWatch(logging.Handler):
    """Records Presidio's 'timed out ... skipping' warnings during one analysis call."""

    def __init__(self):
        super().__init__(level=logging.WARNING)
        self.timed_out = False

    def emit(self, record):
        if "timed out" in record.getMessage():
            self.timed_out = True


_timeouts = _TimeoutWatch()
logging.getLogger("presidio-analyzer").addHandler(_timeouts)

# Per-feed detection engines built by configure_feed: feed name -> (analyzer engine or None, language).
_feeds = {}


def configure_feed(config_json):
    """Build one feed's Presidio registry from the .NET recognizer file (WR-014).

    config_json holds feed, language, disabled (names of Presidio predefined recognizers switched off) and recognizers
    (custom: name, entity, patterns of name, regex and score, context, denyList, denyListScore). Every predefined
    recognizer runs unless it is switched off; spaCy NER provides PERSON, LOCATION, NRP and DATE_TIME. The score
    threshold and every action are applied in .NET. Returns the feed name.
    """
    config = json.loads(config_json)
    language = config["language"]
    registry = RecognizerRegistry(supported_languages=[language])
    registry.load_predefined_recognizers(languages=[language], nlp_engine=nlp_engine)
    disabled = set(config["disabled"])
    unknown = (disabled | set(config.get("enabled", []))) - {r.name for r in registry.recognizers}
    if unknown:
        raise ValueError("Unknown predefined recognizer: " + ", ".join(sorted(unknown)))
    registry.recognizers = [r for r in registry.recognizers if r.name not in disabled]
    for item in config["recognizers"]:
        patterns = [Pattern(p["name"], p["regex"], float(p["score"])) for p in item["patterns"]]
        registry.add_recognizer(PatternRecognizer(supported_entity=item["entity"], name=item["name"], supported_language=language,
                                                  patterns=patterns or None, deny_list=item["denyList"] or None,
                                                  context=item["context"] or None, deny_list_score=float(item["denyListScore"])))
    # An empty registry would make AnalyzerEngine load every default recognizer, so a feed whose every recognizer is
    # switched off gets no engine and detects nothing.
    engine = AnalyzerEngine(registry=registry, nlp_engine=nlp_engine, supported_languages=[language]) if registry.recognizers else None
    _feeds[config["feed"]] = (engine, language)
    return config["feed"]


def analyze_strings(feed, texts_json, contexts_json="null"):
    """Detect personal-data spans in each decoded string of one message, in one batch.

    texts_json is a JSON list of strings. contexts_json is null or a JSON list with one list of context words per string:
    the property names of a JSON string's key path, which Presidio's context enhancer matches against the recognizers'
    context words, as BatchAnalyzerEngine.analyze_dict passes a key. Returns a JSON list with one list per string of
    [start, end, entity, score, recognizer name], with UTF-16 offsets. The recognizer name is the custom recognizer's
    name or the predefined recognizer's class name, such as SpacyRecognizer.
    Raises when a pattern timed out, so the caller dead-letters the message rather than publish unchecked text.
    """
    if feed not in _feeds:
        raise ValueError("Feed recognizers are not configured")
    texts = json.loads(texts_json)
    if not isinstance(texts, list) or not all(isinstance(text, str) for text in texts):
        raise ValueError("A JSON list of strings is required")
    contexts = json.loads(contexts_json)
    if contexts is None:
        contexts = [[] for _ in texts]
    if (not isinstance(contexts, list) or len(contexts) != len(texts)
            or not all(isinstance(words, list) and all(isinstance(word, str) for word in words) for words in contexts)):
        raise ValueError("One JSON list of context words per string is required")
    engine, language = _feeds[feed]
    if engine is None or not texts:
        return json.dumps([[] for _ in texts])
    _timeouts.timed_out = False
    # spaCy processes the batch at once, as BatchAnalyzerEngine.analyze_iterator does; each string is then analyzed with
    # its own context. Every span is returned, including those below the feed threshold: .NET applies the threshold and
    # lists a string with only weak detections for review (WR-018).
    artifacts = engine.nlp_engine.process_batch(texts=texts, language=language, batch_size=max(1, min(len(texts), 64)))
    results = [engine.analyze(text=text, language=language, nlp_artifacts=nlp_artifacts, score_threshold=0.0, context=words or None)
               for (text, nlp_artifacts), words in zip(artifacts, contexts)]
    if _timeouts.timed_out:
        raise TimeoutError("A recognizer pattern timed out")
    return json.dumps([[[_utf16(text, r.start), _utf16(text, r.end), r.entity_type, round(float(r.score), 4),
                         (r.recognition_metadata or {}).get(RecognizerResult.RECOGNIZER_NAME_KEY, "")]
                        for r in spans if r.end > r.start] for text, spans in zip(texts, results)])


def _utf16(text, index):
    """Convert a code-point offset to the UTF-16 offset .NET strings use (they differ after characters such as emoji)."""
    return len(text[:index].encode("utf-16-le")) // 2
