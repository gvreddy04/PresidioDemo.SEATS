using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

/// <summary>
/// Checks the shipped recognizer file (WR-014): it loads, declares the Seats feed's passenger-name recognizer and keys
/// only, and every in-scope (PERSON) value of the frozen baseline is covered by a key mapping or a custom recognizer, or by
/// a Presidio predefined entity when the shipped <c>Presidio:DefaultDetection</c> is on. Other baseline values are out of
/// the names-only scope.
/// </summary>
public static class ConfigurationChecks
{

    #region Fields and Constants

    /// <summary>
    /// The entity types Presidio's predefined recognizers detect in English (Presidio 2.2.364), which run only with
    /// <c>Presidio:DefaultDetection</c> on, unless switched off.
    /// </summary>
    public static readonly IReadOnlySet<string> PredefinedEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        "CREDIT_CARD", "CRYPTO", "DATE_TIME", "EMAIL_ADDRESS", "IBAN_CODE", "IP_ADDRESS", "LOCATION", "MAC_ADDRESS", "MEDICAL_LICENSE",
        "NRP", "PERSON", "PHONE_NUMBER", "UK_NHS", "URL", "US_BANK_NUMBER", "US_DRIVER_LICENSE", "US_ITIN", "US_PASSPORT", "US_SSN"
    };

    #endregion

    #region Methods

    /// <summary>
    /// Runs the checks.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The number of checks that passed.</returns>
    public static int Run(string root)
    {
        string directory = Path.Combine(root, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        var files = RecognizerFileReader.ReadAll(directory);
        bool defaultDetection = DetectionRun.ShippedDefaultDetection(root);
        int passed = 0;
        Check(files.Count == FeedCatalog.All.Count && files.ContainsKey(FeedType.Seats), "the Seats recognizer file loads", ref passed);
        Check(!defaultDetection, "the shipped Presidio:DefaultDetection is off, so these checks run as a deployment without an override does", ref passed);
        var seats = files[FeedType.Seats];
        Check(seats.MappedEntity(["Customers", "FirstName"]) == "PERSON" && seats.MappedEntity(["Customers", "BookedAsName"]) == "PERSON"
            && seats.Recognizers.Any(r => r.Entity == "PERSON"), "passenger-name keys are mapped and a passenger-name recognizer is declared", ref passed);
        Check(seats.EntityMapping!.Values.All(entity => entity == ParityBaseline.ScopeEntity) && seats.Recognizers.All(r => r.Entity == ParityBaseline.ScopeEntity)
            && seats.MappedEntity(["Customers", "PaxId"]) is null, "the shipped file defines passenger names only", ref passed);
        Check(seats.Anonymizers.ContainsKey(FeedAnonymizer.DefaultEntity), "the shipped file sets a DEFAULT anonymizer", ref passed);
        var baseline = ParityBaseline.Load(root);
        int outOfScope = baseline.Protected.Count(e => !ParityBaseline.InScope(e));
        var uncovered = UncoveredEntities(baseline, files, defaultDetection);
        Check(uncovered.Count == 0, "every in-scope baseline entity has a key mapping or a custom recognizer"
            + (uncovered.Count == 0 ? "" : ": missing " + string.Join(", ", uncovered)), ref passed);
        var wrongKey = baseline.Protected.Where(ParityBaseline.InScope).Where(e => seats.MappedEntity(Names(e.Location)) != e.Entity).Select(e => e.Location).ToArray();
        Check(wrongKey.Length == 0, $"every PERSON baseline value sits under a key mapped to PERSON ({outOfScope} other baseline values are out of scope: names-only)"
            + (wrongKey.Length == 0 ? "" : ": not mapped " + string.Join(", ", wrongKey)), ref passed);
        Check(baseline.Pnrs.All(e => seats.Skips(Names(e.Location))), "every baseline record locator sits under a skipped key, so it stays unchanged", ref passed);
        var withoutNames = new Dictionary<FeedType, RecognizerFile>(files)
        {
            [FeedType.Seats] = seats with
            {
                EntityMapping = seats.EntityMapping!.Where(pair => pair.Value != "PERSON").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                Recognizers = seats.Recognizers.Where(r => r.Entity != "PERSON").ToArray()
            },
        };
        Check(UncoveredEntities(baseline, withoutNames, defaultDetection).Contains("Seats PERSON"),
            "the coverage check reports the passenger-name entity removed from its feed's file", ref passed);
        Check(UncoveredEntities(baseline, withoutNames, defaultDetection: true).Count == 0,
            "with Presidio:DefaultDetection on, Presidio's predefined PERSON covers the names instead", ref passed);
        return passed;
    }

    /// <summary>
    /// Returns the in-scope baseline entity types, per feed, that no key mapping or custom recognizer covers, nor a Presidio
    /// predefined entity when <paramref name="defaultDetection"/> is on.
    /// </summary>
    /// <param name="baseline">The frozen baseline.</param>
    /// <param name="files">The recognizer files by feed.</param>
    /// <param name="defaultDetection">The <c>Presidio:DefaultDetection</c> setting.</param>
    /// <returns>"FEED ENTITY" entries in ordinal order.</returns>
    public static IReadOnlyList<string> UncoveredEntities(ParityBaselineDocument baseline, IReadOnlyDictionary<FeedType, RecognizerFile> files, bool defaultDetection) =>
        baseline.Protected
            .Where(ParityBaseline.InScope)
            .Select(e => (Type: FeedCatalog.All.Single(f => f.FolderName == e.Feed).Type, Name: e.Feed, e.Entity))
            .Distinct()
            .Where(e => !(files[e.Type].EntityMapping?.Values.Contains(e.Entity) ?? false) && !files[e.Type].Recognizers.Any(r => r.Entity == e.Entity)
                && !(defaultDetection && PredefinedEntities.Contains(e.Entity)))
            .Select(e => e.Name + " " + e.Entity)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Returns the property names of a JSON Pointer such as <c>/OrderChangeNotif/Old/Customers/0/PaxId</c>: its segments without array indexes.
    /// </summary>
    private static string[] Names(string pointer) =>
        pointer.Split('/').Skip(1).Where(segment => !segment.All(char.IsAsciiDigit)).Select(PresidioDemo.Core.Decoder.JsonFeedParser.Unescape).ToArray();

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
