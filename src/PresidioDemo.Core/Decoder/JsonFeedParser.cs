using System.Globalization;
using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Decoder;

/// <summary>
/// Shared System.Text.Json processing for the JSON feeds, with exact unselected bytes: every scalar becomes a field, and
/// a protected string is written back character by character, so every byte outside a changed character stays as it was.
/// </summary>
/// The Seats feed has one sealed child, <see cref="SeatsParser"/>.
/// <param name="feed">The JSON feed route this parser serves.</param>
/// <param name="rootKind">The required JSON array or object root for this feed.</param>
public abstract class JsonFeedParser(FeedType feed, JsonTokenType rootKind) : IFeedCodec
{

    #region Methods

    /// <summary>
    /// Returns every JSON string scalar of a parse, in payload order, as a text candidate for detection. The field name is
    /// the wildcard key path (for example <c>/OrderChangeNotif/Old/Customers/*/FirstName</c>), whose property names are the detection
    /// context. Numbers, booleans and nulls are never candidates and must stay unchanged.
    /// </summary>
    /// <param name="parsed">A parse result from a JSON feed codec.</param>
    /// <returns>The string fields.</returns>
    /// <exception cref="InvalidDataException">The parse did not come from a JSON feed codec.</exception>
    public static IReadOnlyList<FeedField> StringFields(ParsedFeed parsed)
    {
        if (parsed.Layout is not JsonLayout layout)
        {
            throw new InvalidDataException("The parse did not come from a JSON feed codec.");
        }
        return layout.Scalars.Where(scalar => scalar.Kind == JsonTokenType.String).Select(scalar => scalar.Field).ToArray();
    }

    /// <summary>
    /// Returns the fields of a parse that are numbers or booleans, which a protection can never change.
    /// </summary>
    /// <param name="parsed">A parse result from a JSON feed codec.</param>
    /// <returns>The number and boolean fields, in payload order.</returns>
    /// <exception cref="InvalidDataException">The parse did not come from a JSON feed codec.</exception>
    public static IReadOnlyList<FeedField> NumberOrBooleanFields(ParsedFeed parsed)
    {
        if (parsed.Layout is not JsonLayout layout)
        {
            throw new InvalidDataException("The parse did not come from a JSON feed codec.");
        }
        return layout.Scalars.Where(scalar => scalar.Kind is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
            .Select(scalar => scalar.Field).ToArray();
    }

    /// <summary>
    /// Returns the unescaped property names of a wildcard key path, outermost first, without array wildcards.
    /// </summary>
    /// <param name="path">The wildcard key path, such as <c>/OrderChangeNotif/Old/Customers/*/FirstName</c>.</param>
    /// <returns>The property names, such as <c>Customers</c> and <c>FirstName</c>.</returns>
    public static IReadOnlyList<string> PropertyNames(string path) =>
        path.Split('/').Where(segment => segment.Length > 0 && segment != "*").Select(Unescape).ToArray();

    /// <summary>
    /// Returns a JSON Pointer segment without its <c>~1</c> and <c>~0</c> escapes.
    /// </summary>
    /// <param name="segment">The escaped segment.</param>
    /// <returns>The property name or array index.</returns>
    public static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("JSON codec route does not match delivery.");
        }
        var envelope = FeedPayloadReader.Read(message);
        if (envelope.Ccsid is not null && envelope.Ccsid != "1208")
        {
            throw new InvalidDataException("JSON MQ exports require UTF-8 CCSID 1208; no implicit code-page conversion is performed.");
        }
        var scalars = new List<Scalar>();
        try
        {
            var reader = new Utf8JsonReader(envelope.Payload, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != rootKind)
            {
                throw new InvalidDataException("Unexpected JSON root for this feed.");
            }
            ReadValue(ref reader, "", "", scalars);
            if (reader.Read())
            {
                throw new InvalidDataException("JSON feed has trailing data.");
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Incomplete or malformed JSON payload at line {error.LineNumber}, byte {error.BytePositionInLine}; the complete message is retained without output.");
        }
        catch (InvalidOperationException)
        {
            // A string with an invalid escape or lone surrogate cannot be decoded, so it cannot be checked (WR-016).
            throw new InvalidDataException("A JSON string cannot be decoded; the complete message is retained without output.");
        }
        if (scalars.Count == 0)
        {
            throw new InvalidDataException("JSON feed contains no scalar fields.");
        }
        return new(Array.AsReadOnly(scalars.Select(scalar => scalar.Field).ToArray()),
            new JsonLayout(message, envelope, scalars.AsReadOnly()));
    }

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set. A changed string keeps
    /// the original bytes of every character that did not change, escapes included.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="anonymizedFields">The complete protected field values indexed by original instance identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = Layout(original);
        if (anonymizedFields.Count != layout.Scalars.Count)
        {
            throw new InvalidDataException("Protected field set does not match parsed fields.");
        }
        using (var output = new MemoryStream())
        {
            int cursor = 0;
            foreach (var scalar in layout.Scalars)
            {
                if (!anonymizedFields.TryGetValue(scalar.Field.Id, out string? value) || value is null)
                {
                    throw new InvalidDataException("Protected field is missing or null.");
                }
                output.Write(layout.Envelope.Payload.AsSpan(cursor, scalar.Offset - cursor));
                var token = layout.Envelope.Payload.AsSpan(scalar.Offset, scalar.Length);
                if (value == scalar.Field.Value)
                {
                    output.Write(token);
                }
                else
                {
                    if (scalar.Kind != JsonTokenType.String)
                    {
                        throw new InvalidDataException("Non-string JSON fields must remain unchanged.");
                    }
                    output.Write(Rewrite(token, scalar.Field.Value, value));
                }
                cursor = scalar.Offset + scalar.Length;
            }
            output.Write(layout.Envelope.Payload.AsSpan(cursor));
            return layout.Envelope.Serialize(output.ToArray());
        }
    }

    /// <summary>
    /// Verifies JSON paths, scalar types, MQ metadata and every unselected payload byte.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="serialized">The complete reconstructed output bytes being validated.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = Layout(original);
        var after = Layout(Parse(before.Message with { Content = serialized }));
        if (before.Envelope.Format != after.Envelope.Format || before.Envelope.Header != after.Envelope.Header ||
            before.Envelope.Newline != after.Envelope.Newline || before.Envelope.FinalNewline != after.Envelope.FinalNewline ||
            before.Envelope.HexRowBytes != after.Envelope.HexRowBytes || before.Scalars.Count != after.Scalars.Count)
        {
            throw new InvalidDataException("Serialized JSON structure or MQ metadata changed.");
        }
        int leftCursor = 0, rightCursor = 0;
        for (int index = 0; index < before.Scalars.Count; index++)
        {
            var left = before.Scalars[index];
            var right = after.Scalars[index];
            if (left.Field.Id != right.Field.Id || left.Field.Name != right.Field.Name || left.Kind != right.Kind)
            {
                throw new InvalidDataException("Serialized field locations or types failed validation.");
            }
            // Comparing the gaps also protects property names, separators and empty containers.
            if (!before.Envelope.Payload.AsSpan(leftCursor, left.Offset - leftCursor).SequenceEqual(
                after.Envelope.Payload.AsSpan(rightCursor, right.Offset - rightCursor)))
            {
                throw new InvalidDataException("Unselected JSON bytes or empty-container membership changed.");
            }
            bool mustPreserve = left.Kind != JsonTokenType.String || left.Field.Value == right.Field.Value;
            if (mustPreserve && !before.Envelope.Payload.AsSpan(left.Offset, left.Length).SequenceEqual(
                after.Envelope.Payload.AsSpan(right.Offset, right.Length)))
            {
                throw new InvalidDataException("Unselected JSON scalar bytes or non-string values changed.");
            }
            leftCursor = left.Offset + left.Length;
            rightCursor = right.Offset + right.Length;
        }
        if (!before.Envelope.Payload.AsSpan(leftCursor).SequenceEqual(after.Envelope.Payload.AsSpan(rightCursor)))
        {
            throw new InvalidDataException("Unselected JSON trailing bytes changed.");
        }
    }

    /// <summary>
    /// Obtains the retained parsed layout and verifies that it belongs to this feed route.
    /// </summary>
    /// <param name="parsed">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <returns>The retained parsed layout belonging to this JSON feed route.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private JsonLayout Layout(ParsedFeed parsed)
    {
        if (parsed.Layout is not JsonLayout layout || layout.Message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("JSON serializer requires a parsed layout from the same feed route.");
        }
        return layout;
    }

    /// <summary>
    /// Writes a changed string token. When the protected value is as long as the original, each run of original bytes
    /// that decodes to unchanged characters is copied as it was (so an escape such as <c>é</c> or <c>\/</c> keeps its
    /// spelling) and only the changed characters are encoded; otherwise the whole value is encoded.
    /// </summary>
    /// <param name="token">The original string token, quotes included.</param>
    /// <param name="before">The decoded original value.</param>
    /// <param name="after">The protected value.</param>
    /// <returns>The protected string token, quotes included.</returns>
    private static byte[] Rewrite(ReadOnlySpan<byte> token, string before, string after)
    {
        var output = new List<byte>(token.Length + 8) { (byte)'"' };
        var units = after.Length == before.Length ? Units(token) : null;
        if (units is null || units.Sum(unit => unit.Chars) != before.Length)
        {
            Encode(after, output);
        }
        else
        {
            int position = 0;
            foreach (var unit in units)
            {
                if (string.CompareOrdinal(before, position, after, position, unit.Chars) == 0)
                {
                    output.AddRange(token.Slice(unit.Start, unit.Length));
                }
                else
                {
                    Encode(after.Substring(position, unit.Chars), output);
                }
                position += unit.Chars;
            }
        }
        output.Add((byte)'"');
        return output.ToArray();
    }

    /// <summary>
    /// Splits a string token into the byte runs of its characters: one escape, or one UTF-8 sequence (two UTF-16
    /// characters for a character outside the Basic Multilingual Plane).
    /// </summary>
    /// <param name="token">The string token, quotes included.</param>
    /// <returns>Each run's start, byte length and UTF-16 character count.</returns>
    private static List<(int Start, int Length, int Chars)> Units(ReadOnlySpan<byte> token)
    {
        var units = new List<(int, int, int)>();
        int end = token.Length - 1;
        for (int at = 1; at < end;)
        {
            byte b = token[at];
            int length = b == (byte)'\\' ? (token[at + 1] == (byte)'u' ? 6 : 2)
                : b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : 2;
            int chars = b != (byte)'\\' && b >= 0xF0 ? 2 : 1;
            units.Add((at, length, chars));
            at += length;
        }
        return units;
    }

    /// <summary>
    /// Appends text as the inside of a JSON string: quotes, backslashes and control characters escaped, a lone surrogate
    /// as <c>\uXXXX</c>, every other character as UTF-8.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="output">The bytes being written.</param>
    private static void Encode(string text, List<byte> output)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\\')
            {
                output.Add((byte)'\\');
                output.Add((byte)c);
            }
            else if (c < ' ' || (char.IsSurrogate(c) && !(i + 1 < text.Length && char.IsSurrogatePair(c, text[i + 1]))))
            {
                output.AddRange(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}")));
            }
            else
            {
                int count = char.IsHighSurrogate(c) ? 2 : 1;
                output.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, count)));
                i += count - 1;
            }
        }
    }

    // JSON supplies the structure. Instance paths locate fields; wildcard paths carry the property names.
    /// <summary>
    /// Traverses JSON objects and arrays using System.Text.Json and records exact scalar spans.
    /// </summary>
    /// <param name="reader">The JSON token reader positioned at the value being traversed.</param>
    /// <param name="id">The instance path locating one value within this message.</param>
    /// <param name="path">The wildcard key path, with array indexes as <c>*</c>.</param>
    /// <param name="scalars">The ordered scalar locations collected during complete-message parsing.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static void ReadValue(ref Utf8JsonReader reader, string id, string path, List<Scalar> scalars)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidDataException("Invalid JSON object.");
                }
                string name = reader.GetString()!;
                if (!names.Add(name) || name == "*")
                {
                    throw new InvalidDataException("Duplicate or ambiguous JSON property.");
                }
                if (!reader.Read())
                {
                    throw new InvalidDataException("Incomplete JSON object.");
                }
                string escaped = name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                ReadValue(ref reader, id + "/" + escaped, path + "/" + escaped, scalars);
            }
            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new InvalidDataException("Incomplete JSON object.");
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            int index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                ReadValue(ref reader, id + "/" + index++.ToString(CultureInfo.InvariantCulture), path + "/*", scalars);
            }
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new InvalidDataException("Incomplete JSON array.");
            }
        }
        else if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or
            JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
        {
            string value = reader.TokenType == JsonTokenType.String ? reader.GetString()! :
                reader.TokenType == JsonTokenType.Null ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            scalars.Add(new(new(id, path, value), checked((int)reader.TokenStartIndex),
                checked((int)(reader.BytesConsumed - reader.TokenStartIndex)), reader.TokenType));
        }
        else
        {
            throw new InvalidDataException("Unsupported JSON token.");
        }
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// Retains JSON delivery metadata, transport framing and ordered scalar locations.
    /// </summary>
    /// <param name="Message">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="Envelope">The original payload and transport framing.</param>
    /// <param name="Scalars">The ordered decoded scalars and their original wire spans.</param>
    private sealed record JsonLayout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars);

    /// <summary>
    /// Retains one parsed scalar and the wire span needed to rebuild its value.
    /// </summary>
    /// <param name="Field">The parsed protection field associated with this binding.</param>
    /// <param name="Offset">The zero-based payload byte offset of the token.</param>
    /// <param name="Length">The token length in bytes, quotes included for a string.</param>
    /// <param name="Kind">The JSON token type.</param>
    private sealed record Scalar(FeedField Field, int Offset, int Length, JsonTokenType Kind);

    #endregion
}
