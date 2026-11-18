using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

/// <summary>
/// Checks the Presidio-style recognizer file reader (WR-014): a valid file loads with its switches, custom recognizers,
/// JSON key mappings and skipped keys, allow list and anonymizers, and every invalid or retired form stops startup with a
/// value-free message naming it.
/// All values are made up.
/// </summary>
public static class RecognizerFileChecks
{

    #region Fields and Constants

    /// <summary>
    /// A small valid file that uses every element.
    /// </summary>
    private const string Valid = """
        <?xml version="1.0" encoding="utf-8"?>
        <recognizers feed="Seats" language="en" default_score_threshold="0.6">
          <recognizer name="CryptoRecognizer" type="predefined" enabled="false" />
          <recognizer name="passengerName" type="custom" supported_entity="PERSON">
            <pattern name="surname/given" score="0.9">(?&lt;![A-Z])[A-Z]{2,}/[A-Z]+</pattern>
            <context>DOCS NAME</context>
          </recognizer>
          <recognizer name="titles" type="custom" supported_entity="TITLE" deny_list_score="0.8">
            <deny_list>MR MRS DR</deny_list>
          </recognizer>
          <recognizer name="unused" type="custom" supported_entity="ORDER_ID" enabled="false">
            <pattern name="order" score="0.85">ORD\d+</pattern>
          </recognizer>
          <entity_mapping>
            <key name="FirstName" entity="PERSON" />
            <key name="DOB" entity="DATE_OF_BIRTH" />
            <key name="Attributes" entity="SENSITIVE_ATTRIBUTE" />
          </entity_mapping>
          <keys_to_skip>
            <key name="UARecloc" />
            <key name="FlightLegRefId" />
          </keys_to_skip>
          <allow_list match="regex">
            <item>\d{4}</item>
            <item>FQT[RS]</item>
          </allow_list>
          <anonymizer entity="PERSON" type="mask" masking_char="*" />
          <anonymizer entity="TITLE" type="replace" new_value="[T]" />
          <anonymizer entity="DEFAULT" type="mask" masking_char="#" />
        </recognizers>
        """;

    #endregion

    #region Methods

    /// <summary>
    /// Runs the checks.
    /// </summary>
    /// <returns>The number of checks that passed.</returns>
    public static int Run()
    {
        int passed = 0;
        string scratch = Path.Combine(Path.GetTempPath(), "recognizer-file-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var file = Load(scratch, Valid);
            Check(file.Language == "en" && file.Threshold == 0.6, "root attributes are read", ref passed);
            Check(file.Disabled.SequenceEqual(["CryptoRecognizer"]), "a predefined recognizer with enabled=\"false\" is switched off", ref passed);
            Check(file.Recognizers.Select(r => r.Name).SequenceEqual(["passengerName", "titles"]), "custom recognizers load; a disabled one is left out", ref passed);
            var name = file.Recognizers[0];
            Check(name.Entity == "PERSON" && name.Patterns.Single() is { Name: "surname/given", Score: 0.9 } && name.Context.SequenceEqual(["DOCS", "NAME"]),
                "a pattern keeps its name and score, context words are split on spaces", ref passed);
            Check(file.Recognizers[1].DenyList.SequenceEqual(["MR", "MRS", "DR"]) && file.Recognizers[1].DenyListScore == 0.8, "a deny list and its score load", ref passed);
            Check(file.AllowList.MatchesWhole("1405") && file.AllowList.MatchesWhole("fqtr") && !file.AllowList.MatchesWhole("XXXX1111")
                && !file.AllowList.MatchesWhole("APPR 1234 OK"), "allow-list items match whole strings only", ref passed);
            Check(file.ActionFor("PERSON") is ({ Operator: AnonymizerOperator.Mask, MaskingChar: '*' }, true)
                && file.ActionFor("CREDIT_CARD") is ({ Operator: AnonymizerOperator.Mask, MaskingChar: '#' }, false),
                "an entity uses its own anonymizer, else DEFAULT", ref passed);
            var bare = Load(scratch, "<recognizers feed=\"Seats\" />");
            Check(bare.Disabled.Count == 0 && bare.Recognizers.Count == 0 && bare.Threshold == 0.5 && !bare.AllowList.MatchesWhole("1405")
                && bare.MappedEntity(["FirstName"]) is null && !bare.Skips(["UARecloc"])
                && bare.ActionFor("PERSON") is ({ Operator: AnonymizerOperator.Replace }, false), "an empty file loads with Presidio's defaults and replace", ref passed);
            Check(Load(scratch, "<recognizers feed=\"Seats\"><allow_list><item>FQTR</item></allow_list></recognizers>").AllowList is { Regex: false } exact
                && exact.MatchesWhole("FQTR") && !exact.MatchesWhole("fqtr"), "an exact allow list compares the whole text exactly", ref passed);
            Check(file.ToBridgeJson(false).Contains("\"disabled\":[\"CryptoRecognizer\"]", StringComparison.Ordinal)
                && file.ToBridgeJson(false).Contains("\"denyList\":[\"MR\",\"MRS\",\"DR\"]", StringComparison.Ordinal), "the bridge JSON carries switches and deny lists", ref passed);
            Check(file.ToBridgeJson(false).Contains("\"defaultDetection\":false", StringComparison.Ordinal)
                && file.ToBridgeJson(true).Contains("\"defaultDetection\":true", StringComparison.Ordinal),
                "the bridge JSON carries Presidio:DefaultDetection, so the registry is built with or without the predefined recognizers", ref passed);
            // JSON keys: a mapped key makes its whole value the entity, the innermost mapped name on the path wins, and names
            // compare ignoring case; a skipped key anywhere on the path publishes the value without analysis.
            Check(file.MappedEntity(["Customers", "firstname"]) == "PERSON" && file.MappedEntity(["Customers", "LastName"]) is null
                && file.MappedEntity(["Attributes", "FirstName"]) == "PERSON" && file.MappedEntity(["Attributes", "Code"]) == "SENSITIVE_ATTRIBUTE",
                "entity_mapping maps a key path to the entity of its innermost mapped name, ignoring case", ref passed);
            Check(file.Skips(["uarecloc"]) && file.Skips(["FlightLegRefId", "Code"]) && !file.Skips(["Customers", "PaxStatus"]),
                "keys_to_skip skips a value under a listed name anywhere on its key path, ignoring case", ref passed);
            Check(file.Declares("dob") && file.Declares("UARecloc") && !file.Declares("Customers"), "mapped and skipped names are declared; others are not", ref passed);
            Check(!file.ToBridgeJson(true).Contains("FirstName", StringComparison.Ordinal) && !file.ToBridgeJson(true).Contains("UARecloc", StringComparison.Ordinal),
                "key mappings and skipped keys stay in .NET; the bridge JSON does not carry them", ref passed);

            Reject(scratch, "<recognizers feed=\"Seats\"", "not well-formed XML", ref passed);
            Reject(scratch, "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x \"y\">]><recognizers feed=\"Seats\" />", "a DTD", ref passed);
            Reject(scratch, Valid.Replace("feed=\"Seats\"", "feed=\"OTHER\""), "the wrong feed attribute", ref passed);
            Reject(scratch, Valid.Replace("language=\"en\"", "language=\"de\""), "a language other than en", ref passed);
            Reject(scratch, Valid.Replace("0.6", "1.5"), "a threshold above 1", ref passed);
            Reject(scratch, Valid.Replace("ORD\\d+", "(unclosed"), "a pattern that does not compile", ref passed);
            Reject(scratch, Valid.Replace("\\d{4}", "(unclosed"), "an allow-list item that does not compile", ref passed);
            // An unbalanced ")" is invalid alone but valid inside the whole-string wrapper, where it would match every string.
            Reject(scratch, Valid.Replace("<item>FQT[RS]</item>", "<item>FQTR)|(?:.*</item>"), "an allow-list item that escapes its anchor", ref passed, "does not compile");
            Reject(scratch, Valid.Replace("ORD\\d+", "ORD)|(?:.*"), "a pattern that escapes its anchor", ref passed, "does not compile");
            Reject(scratch, Valid.Replace("name=\"titles\"", "name=\"passengerName\""), "a duplicate recognizer name", ref passed);
            Reject(scratch, Valid.Replace("type=\"predefined\"", "type=\"builtin\""), "an unknown recognizer type", ref passed);
            Reject(scratch, Valid.Replace("enabled=\"false\" />", "enabled=\"no\" />"), "an enabled value other than true or false", ref passed);
            Reject(scratch, Valid.Replace("supported_entity=\"PERSON\"", "supported_entity=\"person\""), "a lower-case entity", ref passed);
            Reject(scratch, Valid.Replace("<deny_list>MR MRS DR</deny_list>", ""), "a custom recognizer without a pattern or deny list", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"encrypt\" key=\"k\""), "the encrypt operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"custom\""), "the custom operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"surrogate_ahds\""), "the surrogate_ahds operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"scramble\""), "an unknown operator", ref passed);
            Reject(scratch, Valid.Replace("entity=\"TITLE\"", "entity=\"PERSON\""), "two anonymizers for one entity", ref passed);
            Reject(scratch, Valid.Replace("<allow_list match=\"regex\">", "<allow_list match=\"glob\">"), "an unknown allow-list match", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<allow_list><item>X</item></allow_list></recognizers>"), "a second allow list", ref passed);
            Reject(scratch, Valid.Replace("<context>DOCS NAME</context>", "<context>DOCS</context><rule />"), "an unknown child element", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<rule /></recognizers>"), "an unknown element", ref passed);
            Reject(scratch, Valid.Replace("type=\"predefined\"", "type=\"predefined\" colour=\"red\""), "an unknown attribute", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<entity_mapping><key name=\"LastName\" entity=\"PERSON\" /></entity_mapping></recognizers>"), "a second entity_mapping", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<keys_to_skip><key name=\"LinkType\" /></keys_to_skip></recognizers>"), "a second keys_to_skip", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"DOB\" entity=\"DATE_OF_BIRTH\" />", "<key name=\"DOB\" entity=\"DATE_OF_BIRTH\" /><key name=\"dob\" entity=\"PERSON\" />"),
                "a key mapped twice, ignoring case", ref passed, "twice");
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<key name=\"firstname\" />"), "a key both mapped and skipped", ref passed, "both mapped");
            Reject(scratch, Valid.Replace("entity=\"DATE_OF_BIRTH\" />", "entity=\"date_of_birth\" />"), "a lower-case mapped entity", ref passed);
            Reject(scratch, Valid.Replace("entity=\"DATE_OF_BIRTH\" />", "entity=\"DEFAULT\" />"), "DEFAULT as a mapped entity", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<key name=\"*\" />"), "the array wildcard as a key name", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<key />"), "a key without a name", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<key name=\"FlightLegRefId\" entity=\"PERSON\" />"), "an entity on a skipped key", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<key name=\"FlightLegRefId\">x</key>"), "a key with content", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"FlightLegRefId\" />", "<item>FlightLegRefId</item>"), "an unknown child of keys_to_skip", ref passed);
            Reject(scratch, Valid.Replace("<entity_mapping>", "<entity_mapping match=\"path\">"), "an attribute on entity_mapping", ref passed);
            Reject(scratch, Valid.Replace("<key name=\"UARecloc\" />", "").Replace("<key name=\"FlightLegRefId\" />", ""), "an empty keys_to_skip", ref passed, "at least one");
            // Elements and attributes of the earlier format fail loudly instead of being ignored.
            Reject(scratch, Valid.Replace("</recognizers>", "<builtIn entity=\"PERSON\" /></recognizers>"), "a retired <builtIn>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<keep name=\"time\"><pattern>\\d{4}</pattern></keep></recognizers>"), "a retired <keep>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<format name=\"x\" entity=\"X\"><pattern>X</pattern></format></recognizers>"), "a retired <format>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<pnrAnchor after=\"passengerName\" /></recognizers>"), "a retired <pnrAnchor>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("language=\"en\"", "language=\"en\" defaultMaskCharacter=\"#\""), "a retired defaultMaskCharacter", ref passed, "no longer supported");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
        return passed;
    }

    /// <summary>
    /// Writes the content as <c>seats.xml</c> and reads it.
    /// </summary>
    private static RecognizerFile Load(string scratch, string content)
    {
        string path = Path.Combine(scratch, "seats.xml");
        File.WriteAllText(path, content);
        return RecognizerFileReader.Read(path, FeedType.Seats);
    }

    /// <summary>
    /// Requires the reader to reject the content with a message naming the file (and, when given, containing a phrase).
    /// </summary>
    private static void Reject(string scratch, string content, string label, ref int passed, string? phrase = null)
    {
        try
        {
            Load(scratch, content);
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("Recognizer file seats.xml", StringComparison.Ordinal)
            && (phrase is null || ex.Message.Contains(phrase, StringComparison.Ordinal)))
        {
            Console.WriteLine("PASS rejected " + label + " (" + ex.Message + ")");
            passed++;
            return;
        }
        throw new Exception("Expected the reader to reject " + label);
    }

    /// <summary>
    /// Throws when a condition fails.
    /// </summary>
    private static void Check(bool condition, string label, ref int passed)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
        Console.WriteLine("PASS " + label);
        passed++;
    }

    #endregion
}
