using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Protection;

#region Models

/// <summary>
/// One pattern of a custom recognizer, as Presidio's <c>Pattern</c>.
/// </summary>
/// <param name="Name">The pattern name.</param>
/// <param name="Score">The score Presidio assigns to a match.</param>
/// <param name="Regex">The regular expression, in the syntax shared by .NET and the Python <c>regex</c> module.</param>
public sealed record CustomPattern(string Name, double Score, string Regex);

/// <summary>
/// A custom recognizer, as a Presidio <c>PatternRecognizer</c> of <c>type: custom</c>.
/// </summary>
/// <param name="Name">The unique recognizer name, shown in logs.</param>
/// <param name="Entity">The supported entity, such as <c>PERSON</c>.</param>
/// <param name="Patterns">The patterns; may be empty when a deny list or a key is given.</param>
/// <param name="Context">Context words that raise a match's score when they appear near it.</param>
/// <param name="DenyList">Words always detected as the entity.</param>
/// <param name="DenyListScore">The score of a deny-list match.</param>
/// <param name="Keys">JSON property names whose whole string value is this entity, at any depth below the property: an
/// extension of Presidio's format for JSON feeds, matched in .NET ignoring case, with score 1.</param>
public sealed record CustomRecognizer(string Name, string Entity, IReadOnlyList<CustomPattern> Patterns, IReadOnlyList<string> Context,
    IReadOnlyList<string> DenyList, double DenyListScore, IReadOnlyList<string> Keys)
{

    #region Methods

    /// <summary>
    /// Returns whether this recognizer runs in Presidio: it has a pattern or a deny list, not only keys.
    /// </summary>
    /// <returns>True when the bridge registers it.</returns>
    public bool RunsInPresidio() => Patterns.Count > 0 || DenyList.Count > 0;

    #endregion
}

/// <summary>
/// The values published unchanged: a decoded string that matches an item end to end, or whose own JSON property is an
/// allowed key, is not analyzed (WR-014).
/// </summary>
/// <param name="Regex">Whether items are regular expressions (<c>match="regex"</c>) rather than exact text.</param>
/// <param name="Items">The items as written.</param>
/// <param name="Compiled">The whole-string patterns for regex items; empty for exact items.</param>
/// <param name="Keys">JSON property names whose string values are published unchanged: an extension of Presidio's format for
/// JSON feeds, matched ignoring case against the value's own property name.</param>
public sealed record AllowList(bool Regex, IReadOnlyList<string> Items, IReadOnlyList<Regex> Compiled, IReadOnlyList<string> Keys)
{

    #region Fields and Constants

    /// <summary>
    /// An allow list with no items.
    /// </summary>
    public static readonly AllowList Empty = new(false, [], [], []);

    #endregion

    #region Methods

    /// <summary>
    /// Returns whether a JSON property name is an allowed key.
    /// </summary>
    /// <param name="key">The value's own unescaped property name.</param>
    /// <returns>True when its string values are published unchanged without analysis.</returns>
    public bool AllowsKey(string key) => key.Length > 0 && Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether the whole string matches an item.
    /// </summary>
    /// <param name="value">The decoded string.</param>
    /// <returns>True when the string is published unchanged without analysis.</returns>
    public bool MatchesWhole(string value) => Regex ? Compiled.Any(item => item.IsMatch(value)) : Items.Contains(value, StringComparer.Ordinal);

    #endregion
}

/// <summary>
/// The validated Presidio-style recognizer file of one feed (WR-014).
/// </summary>
/// <param name="Feed">The feed route.</param>
/// <param name="Language">The analysis language; only <c>en</c>.</param>
/// <param name="Threshold">The minimum score for a detection to be acted on.</param>
/// <param name="Disabled">The names of Presidio predefined recognizers switched off.</param>
/// <param name="Recognizers">The custom recognizers.</param>
/// <param name="AllowList">The whole-string allow list.</param>
/// <param name="Anonymizers">The actions by entity, including <c>DEFAULT</c> when configured.</param>
/// <param name="Enabled">The names of Presidio predefined recognizers the file lists as enabled; they run anyway, but their names
/// are checked so a typo stops startup.</param>
public sealed record RecognizerFile(FeedType Feed, string Language, double Threshold, IReadOnlyList<string> Disabled,
    IReadOnlyList<CustomRecognizer> Recognizers, AllowList AllowList, IReadOnlyDictionary<string, AnonymizerAction> Anonymizers,
    IReadOnlyList<string>? Enabled = null)
{

    #region Methods

    /// <summary>
    /// Returns the custom recognizer whose keys hold a JSON property name, ignoring case.
    /// </summary>
    /// <param name="key">An unescaped JSON property name.</param>
    /// <returns>The recognizer, or null.</returns>
    public CustomRecognizer? RecognizerForKey(string key) =>
        key.Length == 0 ? null : Recognizers.FirstOrDefault(r => r.Keys.Contains(key, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Returns whether the file names a JSON property, as a recognizer key or an allow-list key. Value-free detection log
    /// locations show only these names (WR-018).
    /// </summary>
    /// <param name="key">An unescaped JSON property name.</param>
    /// <returns>True when the file declares it.</returns>
    public bool Declares(string key) => RecognizerForKey(key) is not null || AllowList.AllowsKey(key);

    /// <summary>
    /// Returns the action for a detected entity: its own, else <c>DEFAULT</c>, else Presidio's default <c>replace</c>.
    /// </summary>
    /// <param name="entity">The detected entity type.</param>
    /// <returns>The action and whether the entity has an action of its own.</returns>
    public (AnonymizerAction Action, bool Own) ActionFor(string entity) =>
        Anonymizers.TryGetValue(entity, out var own) ? (own, true)
        : Anonymizers.TryGetValue(FeedAnonymizer.DefaultEntity, out var fallback) ? (fallback, false)
        : (new AnonymizerAction(FeedAnonymizer.DefaultEntity, AnonymizerOperator.Replace), false);

    /// <summary>
    /// Returns the JSON document the Python bridge uses to build this feed's Presidio registry.
    /// </summary>
    /// <returns>Language, switched-off predefined recognizers and the custom recognizers with a pattern or deny list; .NET
    /// applies the threshold, the keys and the actions.</returns>
    public string ToBridgeJson() => JsonSerializer.Serialize(new
    {
        feed = FeedCatalog.Get(Feed).FolderName,
        language = Language,
        disabled = Disabled,
        enabled = Enabled ?? [],
        recognizers = Recognizers.Where(r => r.RunsInPresidio()).Select(r => new
        {
            name = r.Name,
            entity = r.Entity,
            patterns = r.Patterns.Select(p => new { name = p.Name, regex = p.Regex, score = p.Score }),
            context = r.Context,
            denyList = r.DenyList,
            denyListScore = r.DenyListScore
        })
    });

    #endregion
}

#endregion

/// <summary>
/// Reads and validates the per-feed Presidio-style recognizer XML (WR-014, PR-005). Any invalid file stops startup.
/// </summary>
public static class RecognizerFileReader
{

    #region Fields and Constants

    /// <summary>
    /// Elements and attributes of the earlier format, rejected with a clear message rather than ignored.
    /// </summary>
    private static readonly IReadOnlySet<string> RetiredNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "builtIn", "keep", "format", "pnrAnchor", "defaultMaskCharacter", "threshold"
    };

    /// <summary>
    /// The time limit for one .NET allow-list match.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    #endregion

    #region Methods

    /// <summary>
    /// Returns the file name of a feed's recognizer XML, such as <c>seats.xml</c>.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <returns>The file name.</returns>
    public static string FileName(FeedType feed) => FeedCatalog.Get(feed).FolderName.ToLowerInvariant() + ".xml";

    /// <summary>
    /// Reads one recognizer file per supported feed from a directory.
    /// </summary>
    /// <param name="directory">The recognizer directory.</param>
    /// <returns>The validated files by feed.</returns>
    /// <exception cref="InvalidDataException">A file is missing or invalid; the message names the file but no data value.</exception>
    public static IReadOnlyDictionary<FeedType, RecognizerFile> ReadAll(string directory)
    {
        var files = new Dictionary<FeedType, RecognizerFile>();
        foreach (var definition in FeedCatalog.All)
        {
            string path = Path.Combine(directory, FileName(definition.Type));
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Recognizer file {FileName(definition.Type)} is missing.");
            }
            files.Add(definition.Type, Read(path, definition.Type));
        }
        return files;
    }

    /// <summary>
    /// Reads and validates one recognizer file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="feed">The feed the file must declare.</param>
    /// <returns>The validated file.</returns>
    /// <exception cref="InvalidDataException">The file is invalid.</exception>
    public static RecognizerFile Read(string path, FeedType feed)
    {
        string file = Path.GetFileName(path);
        try
        {
            XElement root;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                root = XDocument.Load(reader).Root ?? throw Invalid(file, "has no root element");
            }
            return Parse(root, feed, file);
        }
        catch (XmlException)
        {
            throw Invalid(file, "is not well-formed XML or contains a DTD");
        }
    }

    /// <summary>
    /// Validates a parsed root element.
    /// </summary>
    private static RecognizerFile Parse(XElement root, FeedType feed, string file)
    {
        Require(root.Name.LocalName == "recognizers", file, "root must be <recognizers>");
        Attributes(root, file, "feed", "language", "default_score_threshold");
        Require(root.Attribute("feed")?.Value == FeedCatalog.Get(feed).FolderName, file, "feed attribute must be " + FeedCatalog.Get(feed).FolderName);
        string language = root.Attribute("language")?.Value ?? "en";
        Require(language == "en", file, "language must be en");
        double threshold = Score(root, "default_score_threshold", 0.5, file);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var disabled = new List<string>();
        var enabled = new List<string>();
        var recognizers = new List<CustomRecognizer>();
        var anonymizers = new Dictionary<string, AnonymizerAction>(StringComparer.Ordinal);
        AllowList? allowList = null;
        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "recognizer":
                    string name = Name(element, names, file);
                    switch (element.Attribute("type")?.Value ?? "custom")
                    {
                        case "predefined":
                            Attributes(element, file, "name", "type", "enabled");
                            Require(!element.HasElements, file, $"predefined recognizer '{name}' takes no child elements");
                            (Enabled(element, file, name) ? enabled : disabled).Add(name);
                            break;
                        case "custom":
                            var custom = Custom(element, name, file);
                            if (Enabled(element, file, name))
                            {
                                recognizers.Add(custom);
                            }
                            break;
                        default:
                            throw Invalid(file, $"recognizer '{name}' type must be predefined or custom");
                    }
                    break;
                case "allow_list":
                    Require(allowList is null, file, "declares <allow_list> twice");
                    allowList = Allow(element, file);
                    break;
                case "anonymizer":
                    var action = Anonymizer(element, file);
                    Require(anonymizers.TryAdd(action.Entity, action), file, $"entity '{action.Entity}' has two anonymizers");
                    break;
                case var retired when RetiredNames.Contains(retired):
                    throw Invalid(file, $"element <{retired}> is no longer supported (use the Presidio-style format)");
                default:
                    throw Invalid(file, $"has an unknown element <{element.Name.LocalName}>");
            }
        }
        // A key names one decision only, so a property cannot be both an entity and allowed, or two entities.
        string? twice = recognizers.SelectMany(r => r.Keys).Concat((allowList ?? AllowList.Empty).Keys)
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1)?.Key;
        Require(twice is null, file, $"key '{twice}' is declared twice");
        return new RecognizerFile(feed, language, threshold, disabled, recognizers, allowList ?? AllowList.Empty, anonymizers, enabled);
    }

    /// <summary>
    /// Reads one custom recognizer: patterns, context words, a deny list and JSON keys.
    /// </summary>
    private static CustomRecognizer Custom(XElement element, string name, string file)
    {
        Attributes(element, file, "name", "type", "enabled", "supported_entity", "deny_list_score");
        string entity = element.Attribute("supported_entity")?.Value ?? "";
        Require(Regex.IsMatch(entity, "\\A[A-Z][A-Z_]*\\z", RegexOptions.CultureInvariant), file, $"recognizer '{name}' needs an upper-case supported_entity");
        Children(element, file, "pattern", "context", "deny_list", "key");
        var patterns = new List<CustomPattern>();
        foreach (var pattern in element.Elements("pattern"))
        {
            Attributes(pattern, file, "name", "score");
            string patternName = pattern.Attribute("name")?.Value ?? "";
            Require(patternName.Length > 0, file, $"every pattern of '{name}' needs a name");
            // Patterns keep their exact text: a leading space can be part of a value.
            string regex = pattern.Value;
            Require(regex.Length > 0, file, $"'{name}' has an empty pattern");
            Compile(regex, file, name);
            patterns.Add(new CustomPattern(patternName, Score(pattern, "score", 0.85, file), regex));
        }
        var context = Words(element, "context");
        var denyList = Words(element, "deny_list");
        var keys = Keys(element, name, file);
        Require(patterns.Count > 0 || denyList.Count > 0 || keys.Count > 0, file, $"recognizer '{name}' needs a <pattern>, a <deny_list> or a <key>");
        Require(element.Attribute("deny_list_score") is null || denyList.Count > 0, file, $"recognizer '{name}' has deny_list_score but no <deny_list>");
        return new CustomRecognizer(name, entity, patterns, context, denyList, Score(element, "deny_list_score", 1.0, file), keys);
    }

    /// <summary>
    /// Reads the allow list: exact text by default, or whole-string regular expressions with <c>match="regex"</c>, and the
    /// JSON keys whose values are published unchanged.
    /// </summary>
    private static AllowList Allow(XElement element, string file)
    {
        Attributes(element, file, "match");
        Children(element, file, "item", "key");
        bool regex = (element.Attribute("match")?.Value ?? "exact") switch
        {
            "exact" => false,
            "regex" => true,
            _ => throw Invalid(file, "allow_list match must be exact or regex")
        };
        var items = element.Elements("item").Select(item => item.Value).ToArray();
        var keys = Keys(element, "allow_list", file);
        Require(items.Length + keys.Count > 0 && items.All(item => item.Length > 0), file, "allow_list needs non-empty <item> or <key> elements");
        return new AllowList(regex, items, regex ? items.Select(item => Compile(item, file, "allow_list")).ToArray() : [], keys);
    }

    /// <summary>
    /// Reads the <c>key</c> children: one JSON property name each, trimmed, without control characters, and not the
    /// <c>*</c> that stands for an array position in a field path.
    /// </summary>
    private static IReadOnlyList<string> Keys(XElement element, string name, string file)
    {
        var keys = new List<string>();
        foreach (var key in element.Elements("key"))
        {
            Require(!key.HasElements && !key.HasAttributes, file, $"every <key> of '{name}' holds a property name only");
            string text = key.Value.Trim();
            Require(text.Length > 0 && text != "*" && !text.Any(char.IsControl), file, $"'{name}' has an empty or invalid <key>");
            keys.Add(text);
        }
        return keys;
    }

    /// <summary>
    /// Reads one anonymizer: an entity (or <c>DEFAULT</c>), a Presidio operator type and its parameters.
    /// </summary>
    private static AnonymizerAction Anonymizer(XElement element, string file)
    {
        Require(!element.HasElements, file, "anonymizer takes no child elements");
        string entity = element.Attribute("entity")?.Value ?? "";
        Require(Regex.IsMatch(entity, "\\A[A-Z][A-Z_]*\\z", RegexOptions.CultureInvariant), file, "every anonymizer needs an upper-case entity or DEFAULT");
        string type = element.Attribute("type")?.Value ?? "";
        var parameters = element.Attributes().Where(a => a.Name.LocalName is not ("entity" or "type"))
            .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
        try
        {
            return FeedAnonymizer.Create(entity, type, parameters);
        }
        catch (FormatException ex)
        {
            throw Invalid(file, $"anonymizer for '{entity}': {ex.Message}");
        }
    }

    /// <summary>
    /// Compiles a pattern for .NET: the regex must compile on its own, and an allow-list item must match the whole string.
    /// </summary>
    private static Regex Compile(string pattern, string file, string name)
    {
        const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        try
        {
            // An unbalanced ")" such as "A)|(?:.*" is invalid alone but valid inside the wrapper, where it would escape the
            // whole-string anchor and match every string, so the bare pattern is compiled first.
            _ = new Regex(pattern, Options, MatchTimeout);
            return new Regex("\\A(?:" + pattern + ")\\z", Options, MatchTimeout);
        }
        catch (ArgumentException)
        {
            throw Invalid(file, $"'{name}' has a pattern that does not compile");
        }
    }

    /// <summary>
    /// Reads the optional <c>enabled</c> attribute.
    /// </summary>
    private static bool Enabled(XElement element, string file, string name) => element.Attribute("enabled")?.Value switch
    {
        null or "true" => true,
        "false" => false,
        _ => throw Invalid(file, $"recognizer '{name}' enabled must be true or false")
    };

    /// <summary>
    /// Reads a unique recognizer name.
    /// </summary>
    private static string Name(XElement element, HashSet<string> names, string file)
    {
        string name = element.Attribute("name")?.Value ?? "";
        Require(Regex.IsMatch(name, "\\A[A-Za-z][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant), file, "every recognizer needs a letter, digit and underscore name");
        Require(names.Add(name), file, $"recognizer '{name}' is declared twice");
        return name;
    }

    /// <summary>
    /// Reads an optional invariant score between 0 and 1.
    /// </summary>
    private static double Score(XElement element, string attribute, double fallback, string file)
    {
        string? text = element.Attribute(attribute)?.Value;
        if (text is null)
        {
            return fallback;
        }
        Require(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 1, file,
            $"attribute {attribute} must be a number from 0 to 1");
        return value;
    }

    /// <summary>
    /// Returns the space-separated words of every child element with this name.
    /// </summary>
    private static IReadOnlyList<string> Words(XElement element, string name) =>
        element.Elements(name).SelectMany(child => child.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();

    /// <summary>
    /// Rejects unknown attributes, naming a retired one.
    /// </summary>
    private static void Attributes(XElement element, string file, params string[] allowed)
    {
        foreach (var attribute in element.Attributes())
        {
            string name = attribute.Name.LocalName;
            Require(!RetiredNames.Contains(name), file, $"attribute {name} is no longer supported (use the Presidio-style format)");
            Require(allowed.Contains(name), file, $"<{element.Name.LocalName}> has an unknown attribute {name}");
        }
    }

    /// <summary>
    /// Rejects unknown child elements.
    /// </summary>
    private static void Children(XElement element, string file, params string[] allowed)
    {
        foreach (var child in element.Elements())
        {
            Require(allowed.Contains(child.Name.LocalName), file, $"<{element.Name.LocalName}> has an unknown child <{child.Name.LocalName}>");
        }
    }

    /// <summary>
    /// Throws when a condition fails.
    /// </summary>
    private static void Require(bool condition, string file, string problem)
    {
        if (!condition)
        {
            throw Invalid(file, problem);
        }
    }

    /// <summary>
    /// Creates a value-free configuration error.
    /// </summary>
    private static InvalidDataException Invalid(string file, string problem) => new($"Recognizer file {file}: {problem}.");

    #endregion
}
