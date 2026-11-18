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
from presidio_analyzer.nlp_engine import NlpEngineProvider, NoOpNlpEngine

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

# Per-feed detection engines built by configure_feed: feed name -> (analyzer engine or None, language, NLP mode). The NLP
# mode says what of spaCy a feed's recognizers read: "full" (named entities, for the predefined recognizers), "lemmas"
# (tokens and lemmas, for the context words of a custom recognizer) or "none".
_feeds = {}
# Empty NLP artifacts by language, for feeds whose recognizers read nothing from spaCy.
_no_nlp = {}


def configure_feed(config_json):
    """Build one feed's Presidio registry from the .NET recognizer file (WR-014).

    config_json holds feed, language, defaultDetection (Presidio:DefaultDetection), disabled and enabled (names of Presidio
    predefined recognizers switched off or listed as on) and recognizers (custom: name, entity, patterns of name, regex and
    score, context, denyList, denyListScore). With defaultDetection true every predefined recognizer runs beside the custom
    ones unless it is switched off; spaCy NER provides PERSON, LOCATION, NRP and DATE_TIME. With defaultDetection false
    the registry holds the custom recognizers only, and spaCy runs without its named-entity component, only when a custom
    recognizer has context words (Presidio's context enhancer reads the lemmas around a match). Predefined names are checked either way, so a typo stops startup. The
    score threshold and every action are applied in .NET. Returns the feed name.
    """
    config = json.loads(config_json)
    language = config["language"]
    default_detection = config["defaultDetection"]
    if not isinstance(default_detection, bool):
        raise ValueError("defaultDetection must be true or false")
    registry = RecognizerRegistry(supported_languages=[language])
    registry.load_predefined_recognizers(languages=[language], nlp_engine=nlp_engine)
    disabled = set(config["disabled"])
    unknown = (disabled | set(config.get("enabled", []))) - {r.name for r in registry.recognizers}
    if unknown:
        raise ValueError("Unknown predefined recognizer: " + ", ".join(sorted(unknown)))
    registry.recognizers = [r for r in registry.recognizers if default_detection and r.name not in disabled]
    for item in config["recognizers"]:
        patterns = [Pattern(p["name"], p["regex"], float(p["score"])) for p in item["patterns"]]
        registry.add_recognizer(PatternRecognizer(supported_entity=item["entity"], name=item["name"], supported_language=language,
                                                  patterns=patterns or None, deny_list=item["denyList"] or None,
                                                  context=item["context"] or None, deny_list_score=float(item["denyListScore"])))
    # An empty registry would make AnalyzerEngine load every default recognizer, so a feed whose every recognizer is
    # switched off, or that defines none while defaultDetection is false, gets no engine and detects nothing.
    engine = AnalyzerEngine(registry=registry, nlp_engine=nlp_engine, supported_languages=[language]) if registry.recognizers else None
    nlp = "full" if default_detection else "lemmas" if any(item["context"] for item in config["recognizers"]) else "none"
    if nlp == "none" and language not in _no_nlp:
        _no_nlp[language] = NoOpNlpEngine(models=[{"lang_code": language, "model_name": "none"}])
        _no_nlp[language].load()
    _feeds[config["feed"]] = (engine, language, nlp)
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
    engine, language, nlp = _feeds[feed]
    if engine is None or not texts:
        return json.dumps([[] for _ in texts])
    _timeouts.timed_out = False
    # spaCy processes the batch at once, as BatchAnalyzerEngine.analyze_iterator does; each string is then analyzed with
    # its own context. Every span is returned, including those below the feed threshold: .NET applies the threshold and
    # lists a string with only weak detections for review (WR-018). Without default detection no recognizer reads spaCy's
    # named entities: spaCy runs without its NER component when a custom recognizer has context words, so the lemmas
    # around a match still reach the context enhancer, and not at all otherwise. Key-path context words are passed either
    # way; the enhancer skips a recognizer without context words, so empty artifacts change no result.
    batch_size = max(1, min(len(texts), 64))
    if nlp == "none":
        artifacts = list(_no_nlp[language].process_batch(texts=texts, language=language))
    elif nlp == "lemmas":
        spacy = engine.nlp_engine.get_nlp(language)
        with spacy.select_pipes(disable=[name for name in spacy.pipe_names if name == "ner"]):
            artifacts = list(engine.nlp_engine.process_batch(texts=texts, language=language, batch_size=batch_size))
    else:
        artifacts = list(engine.nlp_engine.process_batch(texts=texts, language=language, batch_size=batch_size))
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
