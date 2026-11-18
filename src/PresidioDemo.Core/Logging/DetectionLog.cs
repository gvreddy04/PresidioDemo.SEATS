using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.Core.Logging;

#region Enumerations

/// <summary>
/// The detection log group a decoded string is listed in (WR-018).
/// </summary>
public enum DetectionGroup
{
    /// <summary>
    /// Personal data: a configured action changed the string.
    /// </summary>
    Pii,
    /// <summary>
    /// Unchanged on purpose: an allow-list item or the <c>keep</c> operator left the string as it was.
    /// </summary>
    NonPii,
    /// <summary>
    /// Worth checking: left unprotected (nothing detected, or below the threshold), changed without an action of its own,
    /// changed by a spaCy guess, or affected by an overlap or a cut result; each line carries a fix.
    /// </summary>
    Review
}

#endregion

/// <summary>
/// Formats the detection log (WR-018): one block per message, with a header line and the decoded strings listed in the
/// PII, non-PII and review groups, each exactly once. A JSON string is located by its JSON Pointer, with every property
/// name the recognizer file does not declare written as <c>?</c>. Value-free unless decoded values are passed, which
/// happens only with
/// <c>Presidio:DetectionLogValues</c> on in a non-production environment (WR-020). Written only while
/// <c>Presidio:DetectionLog</c> is on.
/// </summary>
/// <remarks>
/// The header starts in the first column and every other line is indented, so a log collector can treat each block as one
/// entry (a multiline start pattern of <c>^\S</c>). Values, pointers and reasons are JSON-encoded, so none of them can
/// break a line or start a forged entry.
/// </remarks>
public static class DetectionLog
{

    #region Fields and Constants

    /// <summary>
    /// The indent of an entry line.
    /// </summary>
    private const string EntryIndent = "    ";

    /// <summary>
    /// The indent of a group heading.
    /// </summary>
    private const string GroupIndent = "  ";

    /// <summary>
    /// The widest location the entries are aligned to; a longer location (such as a long encoded pointer under WR-020) is
    /// written unpadded, so one long location cannot widen every line of the block.
    /// </summary>
    private const int MaximumPadding = 48;

    /// <summary>
    /// Encodes values readably (an apostrophe stays an apostrophe) while still escaping quotes, backslashes and control
    /// characters, so an encoded value always stays on one line.
    /// </summary>
    private static readonly JsonSerializerOptions ValueEncoding = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    #endregion

    #region Methods

    /// <summary>
    /// Returns the log word for a decision.
    /// </summary>
    /// <param name="decision">The decision.</param>
    /// <returns>A lower-case, hyphenated word.</returns>
    public static string Decision(DetectionDecision decision) => decision switch
    {
        DetectionDecision.Changed => "changed",
        DetectionDecision.Kept => "kept",
        DetectionDecision.Allowed => "allowed",
        DetectionDecision.NothingDetected => "nothing-detected",
        DetectionDecision.BelowThreshold => "below-threshold",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    /// <summary>
    /// Returns the value-free fix a developer can apply for each review reason, joined with <c>; </c>.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <returns>The fix text, or empty when the string is not for review.</returns>
    public static string Fix(DetectionEvent e) => string.Join("; ", Reasons(e.Reasons).Select(reason => reason switch
    {
        ReviewReason.NothingDetected => "add a recognizer if this is personal data, or an allow-list item if it is not",
        ReviewReason.BelowThreshold => "add context words or raise the pattern score if this is personal data",
        ReviewReason.NoAction => "add an <anonymizer> for " + (e.NoActionEntities.Length > 0 ? e.NoActionEntities : e.Entity),
        ReviewReason.SpacyGuess => "add an allow-list item if this is not personal data",
        ReviewReason.Overlap => "check which recognizer should win, and adjust a pattern score",
        ReviewReason.Cut => "use mask for this entity, or a shorter new_value",
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    }));

    /// <summary>
    /// Returns the log words for the review reasons, such as <c>nothing-detected</c>, joined with <c>+</c>.
    /// </summary>
    /// <param name="reasons">The reasons.</param>
    /// <returns>The words, or empty for none.</returns>
    public static string Reason(ReviewReason reasons) => string.Join('+', Reasons(reasons).Select(reason => reason switch
    {
        ReviewReason.NothingDetected => "nothing-detected",
        ReviewReason.BelowThreshold => "below-threshold",
        ReviewReason.NoAction => "no-action",
        ReviewReason.SpacyGuess => "spacy-guess",
        ReviewReason.Overlap => "overlap",
        ReviewReason.Cut => "cut",
        _ => throw new ArgumentOutOfRangeException(nameof(reasons))
    }));

    /// <summary>
    /// Encodes a value, pointer, property name or reason as one JSON string. Beyond JSON's own escapes, the relaxed encoder
    /// already escapes line and paragraph separators; zero-width and bidirectional formatting characters are escaped too, so
    /// a value can neither break its line nor change how the rest of the line displays.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The quoted, escaped text, which decodes back to <paramref name="value"/>.</returns>
    public static string Encode(string value)
    {
        string encoded = JsonSerializer.Serialize(value, ValueEncoding);
        if (!encoded.Any(IsFormatting))
        {
            return encoded;
        }
        var escaped = new StringBuilder(encoded.Length + 16);
        foreach (char c in encoded)
        {
            escaped.Append(IsFormatting(c) ? string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}") : c.ToString());
        }
        return escaped.ToString();
    }

    /// <summary>
    /// Returns the detection log block for one message.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <param name="transaction">The transaction identifier.</param>
    /// <param name="events">The events from the protector, one per decoded string.</param>
    /// <param name="rejectReason">The value-free dead-letter reason, or null for a published message.</param>
    /// <param name="before">The decoded values by slot, or null for a value-free block (WR-020).</param>
    /// <param name="after">The published values by slot, or null; ignored for a value-free block.</param>
    /// <param name="source">The source file name, which senders put in the correlation ID, or null; written as
    /// <c>file=</c> (QF-26111601), <c>-</c> when absent, JSON-encoded when it has characters outside letters, digits,
    /// <c>.</c>, <c>_</c> and <c>-</c>.</param>
    /// <returns>The block, its lines separated by <see cref="Environment.NewLine"/> and ending with one.</returns>
    public static string FormatBlock(FeedType feed, Guid transaction, IReadOnlyList<DetectionEvent> events, string? rejectReason = null,
        IReadOnlyList<string>? before = null, IReadOnlyList<string>? after = null, string? source = null)
    {
        var groups = Enumerable.Range(0, events.Count).ToLookup(i => Group(events[i]));
        var locations = events.Select(e => Location(e, before is not null)).ToArray();
        int width = Math.Min(locations.Select(location => location.Length).DefaultIfEmpty(0).Max(), MaximumPadding);

        var block = new StringBuilder();
        // The header names the file, then the fields identified and how they split into PII, non-PII and review.
        block.Append(CultureInfo.InvariantCulture,
            $"{FeedCatalog.Get(feed).FolderName} {transaction:N} DETECTION {(rejectReason is null ? "PUBLISHED" : "DEAD-LETTERED")} file={File(source)} fields={events.Count} pii={groups[DetectionGroup.Pii].Count()} non-pii={groups[DetectionGroup.NonPii].Count()} review={groups[DetectionGroup.Review].Count()}");
        if (rejectReason is not null)
        {
            block.Append(" reason=").Append(Encode(rejectReason));
        }
        block.Append(Environment.NewLine);

        foreach (var (group, heading) in new[]
        {
            (DetectionGroup.Pii, "PII fields (changed)"),
            (DetectionGroup.NonPii, "Non-PII fields (unchanged)"),
            (DetectionGroup.Review, "Review (check these; each line has a fix)")
        })
        {
            block.Append(GroupIndent).Append(heading).Append(Environment.NewLine);
            if (!groups[group].Any())
            {
                // An empty group is printed, so a reader knows it was checked rather than left out.
                block.Append(EntryIndent).Append("(none)").Append(Environment.NewLine);
            }
            foreach (int index in groups[group])
            {
                var e = events[index];
                block.Append(EntryIndent).Append(locations[index].PadRight(width)).Append("  ")
                    .Append(Value(e, group, rejectReason is not null, before, after)).Append("  ")
                    .Append(CultureInfo.InvariantCulture, $"recognizer={e.Recognizer} entity={(e.Entity.Length == 0 ? "-" : e.Entity)} score={e.Score:0.00}");
                if (e.Reasons.HasFlag(ReviewReason.BelowThreshold))
                {
                    block.Append(CultureInfo.InvariantCulture, $"<{e.Threshold:0.00}");
                }
                if (e.Operator.Length > 0)
                {
                    block.Append(" operator=").Append(e.Operator);
                }
                block.Append(" decision=").Append(Decision(e.Decision));
                if (group == DetectionGroup.Review)
                {
                    block.Append(" reason=").Append(Reason(e.Reasons)).Append(" fix=").Append(Encode(Fix(e)));
                }
                block.Append(Environment.NewLine);
            }
        }
        return block.ToString();
    }

    /// <summary>
    /// Returns the group a decoded string is listed in (WR-018): review when it has any review reason, otherwise PII when
    /// an action changed it, otherwise non-PII.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <returns>The group.</returns>
    public static DetectionGroup Group(DetectionEvent e) =>
        e.Reasons != ReviewReason.None ? DetectionGroup.Review : e.Decision == DetectionDecision.Changed ? DetectionGroup.Pii : DetectionGroup.NonPii;

    /// <summary>
    /// Returns whether a decision changes the string.
    /// </summary>
    /// <param name="decision">The decision.</param>
    /// <returns>True when an action changed it.</returns>
    private static bool Changes(DetectionDecision decision) => decision == DetectionDecision.Changed;

    /// <summary>
    /// Returns the single reasons set in a combination, in their declared order.
    /// </summary>
    private static IEnumerable<ReviewReason> Reasons(ReviewReason reasons) =>
        Enum.GetValues<ReviewReason>().Where(reason => reason != ReviewReason.None && reasons.HasFlag(reason));

    /// <summary>
    /// Returns the <c>file=</c> value of a block header: the source name as it is, <c>-</c> when absent, or JSON-encoded when it
    /// has characters outside letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, so it can never break the header line.
    /// </summary>
    /// <param name="source">The source file name, or null.</param>
    /// <returns>The header value.</returns>
    private static string File(string? source) => string.IsNullOrEmpty(source) ? "-"
        : source.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? source : Encode(source);

    /// <summary>
    /// Returns whether a character is a zero-width or bidirectional formatting character (U+200B to U+200F, U+202A to
    /// U+202E, U+2066 to U+2069), which would display invisibly or reorder the text after it.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True for those characters.</returns>
    private static bool IsFormatting(char c) => c is >= (char)0x200B and <= (char)0x200F or >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069;

    /// <summary>
    /// Returns where a string sits: the value-free pointer, such as <c>/0/?/1/FirstName</c>, or the full JSON-encoded pointer
    /// under WR-020.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <param name="withValues">Whether the block carries values.</param>
    /// <returns>The location text.</returns>
    private static string Location(DetectionEvent e, bool withValues)
    {
        if (withValues && e.Pointer.Length > 0)
        {
            return Encode(e.Pointer);
        }
        return e.SafePointer.Length > 0 ? e.SafePointer : string.Create(CultureInfo.InvariantCulture, $"slot {e.Slot}");
    }

    /// <summary>
    /// Returns an entry's value part: <c>"before" -> "after"</c> or <c>"before"</c> under WR-020, otherwise
    /// <c>len=</c> and <c>shape=</c>. A review entry says whether the string changed.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <param name="group">Its group.</param>
    /// <param name="rejected">Whether the message was dead-lettered.</param>
    /// <param name="before">The decoded values by slot, or null.</param>
    /// <param name="after">The published values by slot, or null.</param>
    /// <returns>The value part.</returns>
    private static string Value(DetectionEvent e, DetectionGroup group, bool rejected, IReadOnlyList<string>? before, IReadOnlyList<string>? after)
    {
        bool changes = Changes(e.Decision);
        string text;
        // A slot without a value (a protector that supplied fewer values) keeps the value-free form instead of failing.
        if (before is not null && e.Slot >= 0 && e.Slot < before.Count)
        {
            text = Encode(before[e.Slot]);
            if (changes)
            {
                text += rejected || after is null || e.Slot >= after.Count ? " -> not published" : " -> " + Encode(after[e.Slot]);
            }
        }
        else
        {
            text = string.Create(CultureInfo.InvariantCulture, $"len={e.Length} shape={e.Shape}");
        }
        return group == DetectionGroup.Review ? text + (changes ? "  changed" : "  unchanged") : text;
    }

    #endregion
}
