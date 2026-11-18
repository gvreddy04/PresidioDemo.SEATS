"""Value-free verification of decoded-text protection against the frozen parity baseline (WR-019), names only.

Shared by test_decoded_text_feeds.py and tests/run_sample_verification.py. The recognizer file defines passenger names
only, so only the baseline's PERSON values are checked; every other baseline value is counted as out of scope and never
fails a check. Nothing here prints or returns a sample value; failures name the sample, location and check only.
"""
from pathlib import Path
import json
import re

PROJECT = Path(__file__).resolve().parents[2]
FIXTURES = PROJECT / "tests" / "fixtures"
# SEATS_02 is the SEATS export with CCSID 437: a JSON feed needs UTF-8, so it is rejected before detection.
EXPECTED_REJECTIONS = {
    "SEATS_02_Negative_Testing.txt": "UTF-8 CCSID 1208",
}
# The one baseline entity in scope: the recognizer file defines passenger names only.
SCOPE_ENTITY = "PERSON"
# A JSON string literal, with its escapes, as it appears in the payload bytes.
STRING_TOKEN = re.compile(rb'"(?:[^"\\]|\\.)*"')


def payload(message):
    """Return the payload bytes of an IBM MQ export (X rows) or a raw payload."""
    lines = message.decode("utf-8", errors="strict").splitlines() if message[:2] == b"A " else None
    if lines is None:
        return message
    return bytes.fromhex("".join(line[2:].strip() for line in lines if line.startswith("X ")))


def header(message):
    """Return the MQ metadata rows of an export."""
    return [line for line in message.decode("utf-8").splitlines() if line.startswith("A ")]


def in_scope(document):
    """Return the baseline values in the names-only scope."""
    return [entry for entry in document["protected"] if entry["entity"] == SCOPE_ENTITY]


def baseline():
    """Return the frozen baseline and the approved exceptions as (sample, location) keys."""
    document = json.loads((FIXTURES / "parity-baseline.json").read_text(encoding="utf-8"))
    exceptions = {(e["sample"], e["location"]) for e in json.loads((FIXTURES / "parity-exceptions.json").read_text(encoding="utf-8"))}
    return document, exceptions


def json_value(document, location):
    """Return the scalar at a JSON Pointer such as /OrderChangeNotif/Old/Customers/0/FirstName."""
    node = document
    for segment in [s for s in location.split("/") if s]:
        key = segment.replace("~1", "/").replace("~0", "~")
        node = node[int(key)] if isinstance(node, list) else node[key]
    return node


def tokens(data):
    """Split a JSON payload into the bytes between its string literals and the string literals themselves, each marked
    as a property name (followed by a colon) or a value."""
    gaps, strings, cursor = [], [], 0
    for match in STRING_TOKEN.finditer(data):
        gaps.append(data[cursor:match.start()])
        strings.append((match.group(0), data[match.end():].lstrip()[:1] == b":"))
        cursor = match.end()
    gaps.append(data[cursor:])
    return gaps, strings


def verify_published(feed, name, original, output, document=None, exceptions=None):
    """Check one published sample; return value-free counts, raise AssertionError on a failed check. Baseline values
    outside the names-only scope are counted as out_of_scope and not checked."""
    if document is None:
        document, exceptions = baseline()
    before, after = payload(original), payload(output)
    checks = {"protected": 0, "approved_exceptions": 0, "out_of_scope": 0}
    if original[:2] == b"A ":
        if header(original) != header(output):
            raise AssertionError(f"{name}: MQ metadata changed")
        if original.endswith(b"\n") != output.endswith(b"\n"):
            raise AssertionError(f"{name}: final newline changed")
        rows = [len(line) for line in original.splitlines() if line.startswith(b"X ")]
        # Every protection keeps the string's width, so an ASCII payload keeps identical hex rows (WR-004).
        if before.isascii() and rows != [len(line) for line in output.splitlines() if line.startswith(b"X ")]:
            raise AssertionError(f"{name}: MQ row framing changed")
    # Bytes outside the string values stay exact: structure, whitespace, numbers, booleans, nulls and property names.
    gaps_before, strings_before = tokens(before)
    gaps_after, strings_after = tokens(after)
    if gaps_before != gaps_after or len(strings_before) != len(strings_after):
        raise AssertionError(f"{name}: bytes outside the JSON strings changed")
    for index, ((left, key), (right, _)) in enumerate(zip(strings_before, strings_after)):
        if key and left != right:
            raise AssertionError(f"{name}: property name {index} changed")
        if not key and len(json.loads(left)) != len(json.loads(right)):
            raise AssertionError(f"{name}: string {index} changed its width (WR-015)")
        # Every action's result is fitted printable ASCII (WR-015, WR-016), whatever operator the recognizer file chooses.
        if not key and left != right and not all(32 <= ord(c) < 127 for c in json.loads(right)):
            raise AssertionError(f"{name}: string {index} changed to a character that is not printable ASCII")
    left_json, right_json = json.loads(before), json.loads(after)
    for entry in document["protected"]:
        if entry["sample"] != name:
            continue
        if entry["entity"] != SCOPE_ENTITY:
            checks["out_of_scope"] += 1
            continue
        old, new = json_value(left_json, entry["location"]), json_value(right_json, entry["location"])
        # Only characters that were not already * need to change; a value that is all * was masked before it arrived.
        caught = len(old) == len(new) and all(b == "*" or b != a for b, a in zip(old, new))
        if caught:
            checks["protected"] += 1
        elif (name, entry["location"]) in exceptions:
            checks["approved_exceptions"] += 1
        else:
            raise AssertionError(f"{name}: baseline {entry['entity']} at {entry['location']} was published unprotected")
    return checks
