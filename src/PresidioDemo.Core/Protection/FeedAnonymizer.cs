using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PresidioDemo.Core.Protection;

#region Enumerations

/// <summary>
/// The Presidio operators a recognizer file may choose for an entity (WR-014). <c>encrypt</c>, <c>custom</c> and
/// <c>surrogate_ahds</c> are rejected because they need a key, code or a remote service.
/// </summary>
public enum AnonymizerOperator
{
    /// <summary>Overwrites characters with a masking character.</summary>
    Mask = 0,

    /// <summary>Writes a replacement text, by default the entity type in angle brackets.</summary>
    Replace = 1,

    /// <summary>Removes the text; the span becomes spaces, because every result keeps its width.</summary>
    Redact = 2,

    /// <summary>Writes the lowercase hexadecimal hash of the text.</summary>
    Hash = 3,

    /// <summary>Leaves the text unchanged.</summary>
    Keep = 4
}

#endregion

#region Models

/// <summary>
/// One configured action: the operator and its Presidio parameters for one entity, or for <c>DEFAULT</c>.
/// </summary>
/// <param name="Entity">The entity type, such as <c>PERSON</c>, or <c>DEFAULT</c>.</param>
/// <param name="Operator">The operator.</param>
/// <param name="MaskingChar">The <c>mask</c> character.</param>
/// <param name="CharsToMask">How many characters <c>mask</c> changes, or null for all of them.</param>
/// <param name="FromEnd">Whether <c>mask</c> starts from the end of the text.</param>
/// <param name="NewValue">The <c>replace</c> text, or null for <c>&lt;ENTITY&gt;</c>.</param>
/// <param name="HashType">The <c>hash</c> algorithm: <c>sha256</c> or <c>sha512</c>.</param>
/// <param name="Salt">The <c>hash</c> salt, or null for a new random salt per value.</param>
public sealed record AnonymizerAction(string Entity, AnonymizerOperator Operator, char MaskingChar = '*', int? CharsToMask = null,
    bool FromEnd = false, string? NewValue = null, string HashType = "sha256", string? Salt = null);

/// <summary>
/// The result of one action on one detected span, already fitted to the span's width (WR-015).
/// </summary>
/// <param name="Text">The text written in place of the span; exactly as long as the span.</param>
/// <param name="Cut">Whether the operator's result was longer than the span and was cut.</param>
public sealed record AnonymizedSpan(string Text, bool Cut);

#endregion

/// <summary>
/// Applies the configured Presidio operators in .NET and fits every result to the width of the text it replaces, so a
/// protected JSON string keeps its character width and the bytes around it stay in place (WR-015). Parameter names follow
/// Presidio's anonymizer operators.
/// </summary>
public static class FeedAnonymizer
{

    #region Fields and Constants

    /// <summary>
    /// The entity name of the action that applies to every entity without one of its own.
    /// </summary>
    public const string DefaultEntity = "DEFAULT";

    /// <summary>
    /// Presidio operators that are recognized but not allowed (they need a key, code or a remote service).
    /// </summary>
    public static readonly IReadOnlySet<string> RejectedOperators = new HashSet<string>(StringComparer.Ordinal) { "encrypt", "custom", "surrogate_ahds", "decrypt" };

    /// <summary>
    /// The allowed operators by their Presidio names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AnonymizerOperator> Operators = new Dictionary<string, AnonymizerOperator>(StringComparer.Ordinal)
    {
        ["mask"] = AnonymizerOperator.Mask,
        ["replace"] = AnonymizerOperator.Replace,
        ["redact"] = AnonymizerOperator.Redact,
        ["hash"] = AnonymizerOperator.Hash,
        ["keep"] = AnonymizerOperator.Keep
    };

    /// <summary>
    /// The parameters each operator accepts, by their Presidio names.
    /// </summary>
    public static readonly IReadOnlyDictionary<AnonymizerOperator, IReadOnlySet<string>> Parameters = new Dictionary<AnonymizerOperator, IReadOnlySet<string>>
    {
        [AnonymizerOperator.Mask] = new HashSet<string>(StringComparer.Ordinal) { "masking_char", "chars_to_mask", "from_end" },
        [AnonymizerOperator.Replace] = new HashSet<string>(StringComparer.Ordinal) { "new_value" },
        [AnonymizerOperator.Redact] = new HashSet<string>(StringComparer.Ordinal),
        [AnonymizerOperator.Hash] = new HashSet<string>(StringComparer.Ordinal) { "hash_type", "salt" },
        [AnonymizerOperator.Keep] = new HashSet<string>(StringComparer.Ordinal)
    };

    #endregion

    #region Methods

    /// <summary>
    /// Applies an action to one detected span and fits the result to the span's width.
    /// </summary>
    /// <param name="action">The configured action.</param>
    /// <param name="entity">The detected entity type, used by the default <c>replace</c> text.</param>
    /// <param name="text">The detected text.</param>
    /// <returns>The fitted text and whether it was cut.</returns>
    public static AnonymizedSpan Apply(AnonymizerAction action, string entity, string text) => action.Operator switch
    {
        AnonymizerOperator.Mask => new AnonymizedSpan(Mask(text, action.MaskingChar, action.CharsToMask, action.FromEnd), false),
        AnonymizerOperator.Replace => Fit(action.NewValue ?? "<" + entity + ">", text.Length),
        AnonymizerOperator.Redact => Fit("", text.Length),
        AnonymizerOperator.Hash => Fit(Hash(text, action.HashType, action.Salt), text.Length),
        AnonymizerOperator.Keep => new AnonymizedSpan(text, false),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    /// <summary>
    /// Creates an action from its Presidio operator name and parameters, rejecting what the worker does not allow.
    /// </summary>
    /// <param name="entity">The entity type or <c>DEFAULT</c>.</param>
    /// <param name="type">The Presidio operator name.</param>
    /// <param name="parameters">The operator parameters by Presidio name.</param>
    /// <returns>The validated action.</returns>
    /// <exception cref="FormatException">The operator or a parameter is not allowed; the message names it but no value.</exception>
    public static AnonymizerAction Create(string entity, string type, IReadOnlyDictionary<string, string> parameters)
    {
        if (RejectedOperators.Contains(type))
        {
            throw new FormatException($"operator '{type}' is not allowed (it needs a key, code or a remote service)");
        }
        if (!Operators.TryGetValue(type, out var op))
        {
            throw new FormatException($"operator '{type}' is not supported");
        }
        foreach (string name in parameters.Keys.Where(name => !Parameters[op].Contains(name)))
        {
            throw new FormatException($"operator '{type}' has no parameter '{name}'");
        }
        string? Get(string name) => parameters.TryGetValue(name, out string? value) ? value : null;
        return op switch
        {
            AnonymizerOperator.Mask => new AnonymizerAction(entity, op,
                MaskingChar: Get("masking_char") is { } c ? Printable(c, 1, 1, "masking_char")[0] : '*',
                CharsToMask: Get("chars_to_mask") is { } n ? Count(n) : null,
                FromEnd: Get("from_end") is { } f ? Flag(f) : false),
            AnonymizerOperator.Replace => new AnonymizerAction(entity, op, NewValue: Get("new_value") is { } v ? Printable(v, 0, 256, "new_value") : null),
            AnonymizerOperator.Hash => new AnonymizerAction(entity, op,
                HashType: Get("hash_type") is { } h ? (h is "sha256" or "sha512" ? h : throw new FormatException("hash_type must be sha256 or sha512")) : "sha256",
                Salt: Get("salt") is { } s ? Printable(s, 1, 256, "salt") : null),
            _ => new AnonymizerAction(entity, op)
        };
    }

    /// <summary>
    /// Fits a result to a width: a shorter result is padded with spaces, a longer one is cut.
    /// </summary>
    /// <param name="result">The operator's result.</param>
    /// <param name="width">The width of the replaced text.</param>
    /// <returns>The fitted text and whether it was cut.</returns>
    public static AnonymizedSpan Fit(string result, int width) =>
        result.Length > width ? new AnonymizedSpan(result[..width], true) : new AnonymizedSpan(result.PadRight(width, ' '), false);

    /// <summary>
    /// Reads a positive whole number.
    /// </summary>
    private static int Count(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0 ? count : throw new FormatException("chars_to_mask must be a positive whole number");

    /// <summary>
    /// Reads <c>true</c> or <c>false</c>.
    /// </summary>
    private static bool Flag(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException("from_end must be true or false")
    };

    /// <summary>
    /// Returns the lowercase hexadecimal SHA-256 or SHA-512 of the salt followed by the text; a missing salt is a new
    /// random salt for this value, as Presidio does, so equal values do not hash alike across messages.
    /// </summary>
    private static string Hash(string text, string hashType, string? salt)
    {
        byte[] saltBytes = salt is null ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(salt);
        byte[] input = [.. saltBytes, .. Encoding.UTF8.GetBytes(text)];
        return Convert.ToHexStringLower(hashType == "sha512" ? SHA512.HashData(input) : SHA256.HashData(input));
    }

    /// <summary>
    /// Masks <paramref name="count"/> characters (all when null) from the start or the end with the masking character.
    /// </summary>
    private static string Mask(string text, char mask, int? count, bool fromEnd)
    {
        int masked = Math.Min(count ?? text.Length, text.Length);
        return fromEnd ? text[..(text.Length - masked)] + new string(mask, masked) : new string(mask, masked) + text[masked..];
    }

    /// <summary>
    /// Requires printable ASCII of a length within bounds, so a configured text stays plain ASCII in every output (WR-016).
    /// </summary>
    private static string Printable(string value, int min, int max, string name) =>
        value.Length >= min && value.Length <= max && value.All(c => c is >= ' ' and <= '~') ? value
            : throw new FormatException($"{name} must be {min} to {max} printable ASCII characters");

    #endregion
}
