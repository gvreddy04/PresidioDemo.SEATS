using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Protection;

#region Enumerations

/// <summary>
/// What happened to one decoded string (WR-014).
/// </summary>
public enum DetectionDecision
{
    /// <summary>
    /// A configured action changed at least one detected span.
    /// </summary>
    Changed = 0,
    /// <summary>
    /// Spans were detected, but their actions (such as <c>keep</c>) left the string unchanged.
    /// </summary>
    Kept = 1,
    /// <summary>
    /// The whole string matched an allow-list item, or sits under a skipped JSON key, and was published unchanged without
    /// analysis.
    /// </summary>
    Allowed = 2,
    /// <summary>
    /// Nothing was detected; the string is published unchanged.
    /// </summary>
    NothingDetected = 3,
    /// <summary>
    /// Only detections below the threshold were found; the string is published unchanged.
    /// </summary>
    BelowThreshold = 4
}

/// <summary>
/// Why a decoded string is listed for review in the detection log (WR-018); a string may have several reasons.
/// </summary>
[Flags]
public enum ReviewReason
{
    /// <summary>
    /// Not for review.
    /// </summary>
    None = 0,
    /// <summary>
    /// Published unchanged because nothing was detected.
    /// </summary>
    NothingDetected = 1,
    /// <summary>
    /// Published unchanged because every detection scored below the threshold.
    /// </summary>
    BelowThreshold = 2,
    /// <summary>
    /// Changed by an entity with no anonymizer of its own, so <c>DEFAULT</c> or Presidio's <c>replace</c> applied.
    /// </summary>
    NoAction = 4,
    /// <summary>
    /// Changed by a guess of the spaCy named-entity recognizer, which runs only with <c>Presidio:DefaultDetection</c> on.
    /// </summary>
    SpacyGuess = 8,
    /// <summary>
    /// Detections of different entities overlapped; the higher score won the shared characters.
    /// </summary>
    Overlap = 16,
    /// <summary>
    /// An action's result was longer than the text it replaced and was cut to fit (WR-015).
    /// </summary>
    Cut = 32
}

#endregion

#region Models

/// <summary>
/// One value-free detection log entry (WR-018): never the value, only its location, length, shape and decision.
/// </summary>
/// <param name="Slot">The candidate's order among the message's decoded strings.</param>
/// <param name="Length">The value length.</param>
/// <param name="Shape">The character shape: A for a letter, 9 for a digit, other characters unchanged.</param>
/// <param name="Recognizer">The recognizer names joined with <c>+</c>, <c>allow_list</c>, or <c>none</c>.</param>
/// <param name="Entity">The detected entity types, joined with <c>+</c>, or empty.</param>
/// <param name="Score">The highest detection score, or zero.</param>
/// <param name="Decision">What happened to the string.</param>
/// <param name="Operator">The operators applied, joined with <c>+</c>, or empty.</param>
/// <param name="Reasons">Why the string is listed for review, or <see cref="ReviewReason.None"/>.</param>
/// <param name="Threshold">The feed's score threshold, so a review line can compare a weak score with it.</param>
/// <param name="NoActionEntities">The detected entities without an anonymizer of their own, joined with <c>+</c>, or empty.</param>
/// <param name="Pointer">The string's JSON Pointer with every property name, which may itself be a value in dynamic-key JSON;
/// written only with <c>Presidio:DetectionLogValues</c> on (WR-020).</param>
/// <param name="SafePointer">The JSON Pointer with every property name the feed's recognizer file does not declare replaced by
/// <c>?</c>; array indexes stay (WR-018).</param>
/// <param name="Pattern">The string's key path with array indexes as <c>*</c>, used to group strings across messages in the
/// local harness summary (WR-021).</param>
public sealed record DetectionEvent(int Slot, int Length, string Shape, string Recognizer, string Entity, double Score, DetectionDecision Decision,
    string Operator = "", ReviewReason Reasons = ReviewReason.None, double Threshold = 0, string NoActionEntities = "",
    string Pointer = "", string SafePointer = "", string Pattern = "");

/// <summary>
/// The protected values for every parsed field, and one detection event per decoded string.
/// </summary>
/// <param name="Values">The protected value of every field, by identifier.</param>
/// <param name="Events">The value-free detection events, in candidate order.</param>
/// <param name="CandidateValues">The decoded value of each candidate, by slot, kept beside the events so the events stay
/// value-free; written to the detection log only with <c>Presidio:DetectionLogValues</c> on (WR-020).</param>
/// <param name="ProtectedCandidateValues">The protected value of each candidate, by slot, for the after values of the
/// detection log under WR-020.</param>
public sealed record DetectionOutcome(IReadOnlyDictionary<string, string> Values, IReadOnlyList<DetectionEvent> Events,
    IReadOnlyList<string>? CandidateValues = null, IReadOnlyList<string>? ProtectedCandidateValues = null);

#endregion

/// <summary>
/// Protects one parsed JSON message with its feed's recognizer file. Presidio only returns spans; every decision is made
/// here. Every JSON string is a candidate and is decided on its own (WR-014), in this order: a string under a mapped key is
/// that entity across its whole width; a string under a skipped key, or that matches an allow-list item end to end, is
/// published unchanged; otherwise Presidio analyzes it with the property names of its key path as context, each span at or
/// above the threshold gets its entity's action (its own, else <c>DEFAULT</c>, else <c>replace</c>), and a string in which
/// nothing is detected is published unchanged. Numbers, booleans and nulls never change, and a number or boolean under a
/// mapped key dead-letters the message (WR-016). Every result keeps the string's width (WR-015).
/// </summary>
/// <remarks>
/// <c>Presidio:DefaultDetection</c> decides which recognizers Presidio runs. Off (the shipped default), only the file's
/// custom recognizers run, so only what the file defines acts: its mapped keys and custom recognizers; a file that defines
/// no custom recognizer is not sent to Presidio at all, and with no mapped key either the message is published as
/// received. On, Presidio's predefined recognizers (spaCy NER included) run beside the custom ones, less any the file
/// switches off.
/// </remarks>
/// <param name="files">The validated recognizer files by feed.</param>
/// <param name="defaultDetection">The <c>Presidio:DefaultDetection</c> setting; false by default.</param>
public sealed class DetectingFeedProtector(IReadOnlyDictionary<FeedType, RecognizerFile> files, bool defaultDetection = false) : IFeedProtector
{

    #region Fields and Constants

    /// <summary>
    /// The name Presidio reports for its spaCy named-entity recognizer.
    /// </summary>
    public const string SpacyRecognizer = "SpacyRecognizer";

    /// <summary>
    /// The recognizer name logged for a string decided by its key in <c>&lt;entity_mapping&gt;</c>.
    /// </summary>
    public const string EntityMappingRecognizer = "entity_mapping";

    /// <summary>
    /// The recognizer name logged for a string published unchanged by <c>&lt;keys_to_skip&gt;</c>.
    /// </summary>
    public const string KeysToSkipRecognizer = "keys_to_skip";

    /// <summary>
    /// Serializes calls into the shared embedded Python engines.
    /// </summary>
    private static readonly SemaphoreSlim EngineGate = new(1, 1);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets whether Presidio's predefined recognizers run beside the recognizer files' custom recognizers.
    /// </summary>
    /// <value>The <c>Presidio:DefaultDetection</c> setting.</value>
    public bool DefaultDetection => defaultDetection;

    #endregion

    #region Methods

    /// <summary>
    /// Builds every feed's Presidio registry, with or without Presidio's predefined recognizers as
    /// <see cref="DefaultDetection"/> says. Call once after <see cref="PythonHost.Start"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The bridge rejected a configuration, such as an unknown predefined recognizer.</exception>
    public void Configure()
    {
        foreach (var file in files.Values)
        {
            try
            {
                PythonHost.Call("configure_feed", file.ToBridgeJson(defaultDetection));
            }
            catch (Exception ex)
            {
                // The reason comes from the configuration only (a recognizer name or a pattern Presidio rejects), never from
                // message data, so it is safe to show.
                throw new InvalidDataException($"Local Presidio could not load the {FeedCatalog.Get(file.Feed).FolderName} recognizers: {Reason(ex)}");
            }
        }
    }

    /// <summary>
    /// Protects every decoded string of one parsed message.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <param name="parsed">The parse from the JSON feed parser.</param>
    /// <param name="cancellationToken">The token that interrupts processing.</param>
    /// <returns>The protected value of every field and one event per decoded string.</returns>
    /// <exception cref="InvalidDataException">A mapped key holds a number or boolean, detection failed, a pattern timed out,
    /// or an action failed.</exception>
    public async ValueTask<DetectionOutcome> ProtectAsync(FeedType feed, ParsedFeed parsed, CancellationToken cancellationToken)
    {
        var file = files[feed];
        var candidates = JsonFeedParser.StringFields(parsed);
        RejectMappedNonStrings(file, parsed);
        var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value, StringComparer.Ordinal);
        var events = new DetectionEvent[candidates.Count];
        var names = candidates.Select(field => JsonFeedParser.PropertyNames(field.Name)).ToArray();
        var pending = new List<int>();
        try
        {
            for (int slot = 0; slot < candidates.Count; slot++)
            {
                var field = candidates[slot];
                if (field.Value.Length == 0)
                {
                    events[slot] = Event(file, slot, field, "none", "", 0, DetectionDecision.NothingDetected);
                }
                else if (file.MappedEntity(names[slot]) is { } entity)
                {
                    // The key says what the value is, so the whole string is the entity and Presidio is not asked (WR-014).
                    var (value, applied, reasons) = Apply(file, field.Value, [new Span(0, field.Value.Length, entity, 1, EntityMappingRecognizer)]);
                    values[field.Id] = value;
                    events[slot] = Applied(file, slot, field, value, applied, reasons);
                }
                else if (file.Skips(names[slot]))
                {
                    events[slot] = Event(file, slot, field, KeysToSkipRecognizer, "", 0, DetectionDecision.Allowed);
                }
                else if (file.AllowList.MatchesWhole(field.Value))
                {
                    events[slot] = Event(file, slot, field, "allow_list", "", 0, DetectionDecision.Allowed);
                }
                else
                {
                    pending.Add(slot);
                }
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new InvalidDataException("An allow-list pattern timed out; input retained and no output was published.");
        }

        // Without default detection Presidio runs the file's custom recognizers only; a file without any is not sent at all.
        var spans = defaultDetection || file.Recognizers.Count > 0
            ? await AnalyzeAsync(feed, pending.Select(slot => candidates[slot].Value).ToArray(), pending.Select(slot => names[slot]).ToArray(), cancellationToken)
            : pending.Select(_ => Array.Empty<Span>()).ToArray();
        for (int i = 0; i < pending.Count; i++)
        {
            int slot = pending[i];
            var field = candidates[slot];
            var strong = spans[i].Where(span => span.Score >= file.Threshold).ToArray();
            if (strong.Length == 0)
            {
                var weak = spans[i];
                events[slot] = weak.Length == 0
                    ? Event(file, slot, field, "none", "", 0, DetectionDecision.NothingDetected, reasons: ReviewReason.NothingDetected)
                    : Event(file, slot, field, Names(weak.Select(s => s.Recognizer)), Names(weak.Select(s => s.Entity)), weak.Max(s => s.Score),
                        DetectionDecision.BelowThreshold, reasons: ReviewReason.BelowThreshold);
                continue;
            }
            var (value, applied, reasons) = Apply(file, field.Value, strong);
            values[field.Id] = value;
            events[slot] = Applied(file, slot, field, value, applied, reasons);
        }

        var detected = events.ToArray();
        return new DetectionOutcome(values, detected, candidates.Select(c => c.Value).ToArray(), candidates.Select(c => values[c.Id]).ToArray());
    }

    /// <summary>
    /// Returns the character shape of a value: A for a letter, 9 for a digit, other characters unchanged.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The shape, at most 64 characters.</returns>
    public static string Shape(string value)
    {
        var shape = new StringBuilder(Math.Min(value.Length, 64));
        foreach (char c in value.Take(64))
        {
            shape.Append(char.IsAsciiLetter(c) ? 'A' : char.IsAsciiDigit(c) ? '9' : char.IsAscii(c) && !char.IsControl(c) ? c : '?');
        }
        return shape.ToString();
    }

    /// <summary>
    /// Returns an operator's Presidio name.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <returns>Such as <c>mask</c>.</returns>
    public static string Operator(AnonymizerOperator op) => FeedAnonymizer.Operators.Single(pair => pair.Value == op).Key;

    /// <summary>
    /// Applies the configured actions to the detected spans of one string. Overlaps are resolved by score, then length,
    /// then position: each character belongs to the first span that claims it. Each span's action runs on the span's
    /// whole text, fitted to its width, and the span writes only the characters no higher-ranked span took, so options such
    /// as <c>chars_to_mask</c> and <c>from_end</c> count from the span's own edges.
    /// </summary>
    private static (string Value, IReadOnlyList<(Span Span, AnonymizerAction Action, bool Own)> Applied, ReviewReason Reasons) Apply(RecognizerFile file,
        string value, IReadOnlyList<Span> spans)
    {
        var ranked = spans.OrderByDescending(s => s.Score).ThenByDescending(s => s.End - s.Start).ThenBy(s => s.Start)
            .ThenBy(s => s.Entity, StringComparer.Ordinal).ToArray();
        var owner = Enumerable.Repeat(-1, value.Length).ToArray();
        var reasons = ReviewReason.None;
        for (int rank = 0; rank < ranked.Length; rank++)
        {
            var span = ranked[rank];
            for (int c = span.Start; c < span.End; c++)
            {
                if (owner[c] < 0)
                {
                    owner[c] = rank;
                }
                else if (ranked[owner[c]].Entity != span.Entity)
                {
                    reasons |= ReviewReason.Overlap;
                }
            }
        }

        var result = value.ToCharArray();
        var applied = new List<(Span Span, AnonymizerAction Action, bool Own)>();
        for (int rank = 0; rank < ranked.Length; rank++)
        {
            var span = ranked[rank];
            if (!Enumerable.Range(span.Start, span.End - span.Start).Any(c => owner[c] == rank))
            {
                continue;
            }
            var (action, own) = file.ActionFor(span.Entity);
            var fitted = FeedAnonymizer.Apply(action, span.Entity, value[span.Start..span.End]);
            for (int c = span.Start; c < span.End; c++)
            {
                if (owner[c] == rank)
                {
                    result[c] = fitted.Text[c - span.Start];
                }
            }
            // A hash is longer than almost any value by design, so its truncation to width is not a review reason (WR-015).
            bool cut = fitted.Cut && action.Operator != AnonymizerOperator.Hash;
            reasons |= (own ? ReviewReason.None : ReviewReason.NoAction) | (cut ? ReviewReason.Cut : ReviewReason.None)
                | (span.Recognizer == SpacyRecognizer ? ReviewReason.SpacyGuess : ReviewReason.None);
            applied.Add((span, action, own));
        }
        return (new string(result), applied, reasons);
    }

    /// <summary>
    /// Rejects a number or boolean anywhere under a mapped key: it cannot be protected without changing its JSON type, so
    /// the message fails closed instead of publishing it (WR-016). Nulls carry no value.
    /// </summary>
    /// <param name="file">The recognizer file.</param>
    /// <param name="parsed">The JSON parse.</param>
    /// <exception cref="InvalidDataException">A mapped key holds a number or boolean.</exception>
    private static void RejectMappedNonStrings(RecognizerFile file, ParsedFeed parsed)
    {
        if (JsonFeedParser.NumberOrBooleanFields(parsed).Any(field => file.MappedEntity(JsonFeedParser.PropertyNames(field.Name)) is not null))
        {
            throw new InvalidDataException("A personal-data property holds a number or boolean that cannot be protected in its JSON type; input retained and no output was published.");
        }
    }

    /// <summary>
    /// Sends one message's decoded strings to the embedded Presidio engine in one batch, each with the property names of its
    /// key path as Presidio context words, as Presidio's own <c>analyze_dict</c> passes a key.
    /// </summary>
    private static async ValueTask<IReadOnlyList<Span[]>> AnalyzeAsync(FeedType feed, IReadOnlyList<string> texts, IReadOnlyList<IReadOnlyList<string>> contexts,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }
        Span[][] result;
        await EngineGate.WaitAsync(cancellationToken);
        try
        {
            string json = PythonHost.Call("analyze_strings", FeedCatalog.Get(feed).FolderName, JsonSerializer.Serialize(texts), JsonSerializer.Serialize(contexts));
            using var document = JsonDocument.Parse(json);
            result = document.RootElement.EnumerateArray().Select(found => found.EnumerateArray().Select(span => new Span(
                span[0].GetInt32(), span[1].GetInt32(), span[2].GetString() ?? "", span[3].GetDouble(), span[4].GetString() ?? "")).ToArray()).ToArray();
        }
        catch (Exception)
        {
            // A bridge failure, a timeout or malformed bridge output: no output is published.
            throw new InvalidDataException("Local Presidio detection failed; input retained and no output was published.");
        }
        finally
        {
            EngineGate.Release();
        }
        if (result.Length != texts.Count || result.Zip(texts).Any(pair => pair.First.Any(s => s.Start < 0 || s.End > pair.Second.Length || s.Start >= s.End)))
        {
            throw new InvalidDataException("Local Presidio returned spans that do not fit the message; no output was published.");
        }
        return result;
    }

    /// <summary>
    /// Creates the event of a string whose spans got their actions.
    /// </summary>
    private static DetectionEvent Applied(RecognizerFile file, int slot, FeedField field, string value,
        IReadOnlyList<(Span Span, AnonymizerAction Action, bool Own)> applied, ReviewReason reasons) =>
        Event(file, slot, field, Names(applied.Select(a => a.Span.Recognizer)), Names(applied.Select(a => a.Span.Entity)),
            applied.Max(a => a.Span.Score), value == field.Value ? DetectionDecision.Kept : DetectionDecision.Changed,
            Names(applied.Select(a => Operator(a.Action.Operator))), reasons, Names(applied.Where(a => !a.Own).Select(a => a.Span.Entity)));

    /// <summary>
    /// Creates a value-free event, located by the string's JSON Pointer in the forms the detection log uses (WR-018).
    /// </summary>
    private static DetectionEvent Event(RecognizerFile file, int slot, FeedField field, string recognizer, string entity, double score, DetectionDecision decision,
        string op = "", ReviewReason reasons = ReviewReason.None, string noActionEntities = "") =>
        new(slot, field.Value.Length, Shape(field.Value), recognizer, entity, Math.Round(score, 2, MidpointRounding.AwayFromZero), decision, op, reasons,
            file.Threshold, noActionEntities, field.Id, SafePointer(file, field), field.Name);

    /// <summary>
    /// Returns a string's JSON Pointer with each property name the recognizer file does not declare replaced by <c>?</c>.
    /// In dynamic-key JSON a property name can itself be a value, so only declared names and array indexes are written in
    /// the value-free detection log (WR-018); the full pointer is written only under WR-020.
    /// </summary>
    private static string SafePointer(RecognizerFile file, FeedField field)
    {
        // The instance pointer and the wildcard path have the same segments; a "*" in the path marks an array index.
        string[] instance = field.Id.Split('/');
        string[] path = field.Name.Split('/');
        return string.Join('/', instance.Select((segment, i) => segment.Length == 0 || (i < path.Length && path[i] == "*") ||
            file.Declares(JsonFeedParser.Unescape(segment)) ? segment : "?"));
    }

    /// <summary>
    /// Returns the last line of a bridge error (the Python exception's own message), at most 200 characters, ending with a
    /// full stop.
    /// </summary>
    private static string Reason(Exception ex)
    {
        string line = ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        line = line.Length > 200 ? line[..200] : line;
        return line.Length == 0 ? "unknown error." : line.TrimEnd('.') + ".";
    }

    /// <summary>
    /// Joins distinct names in ordinal order with <c>+</c>.
    /// </summary>
    private static string Names(IEnumerable<string> names) => string.Join('+', names.Where(n => n.Length > 0).Distinct().Order(StringComparer.Ordinal));

    #endregion

    #region Nested Types

    /// <summary>
    /// One span returned by Presidio.
    /// </summary>
    /// <param name="Start">The first character.</param>
    /// <param name="End">The character after the last.</param>
    /// <param name="Entity">The entity type.</param>
    /// <param name="Score">The detection score.</param>
    /// <param name="Recognizer">The recognizer name: a custom recognizer's name or a Presidio predefined recognizer.</param>
    private sealed record Span(int Start, int End, string Entity, double Score, string Recognizer);

    #endregion
}
