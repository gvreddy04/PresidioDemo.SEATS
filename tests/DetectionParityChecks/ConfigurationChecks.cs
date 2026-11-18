using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

/// <summary>
/// Checks the shipped recognizer file (WR-014): it loads, declares the Seats feed's custom recognizers and JSON keys, every
/// entity type of the frozen baseline is covered by a custom recognizer or by a Presidio predefined entity, and every
/// baseline location sits under a recognizer key of its entity.
/// </summary>
public static class ConfigurationChecks
{

    #region Fields and Constants

    /// <summary>
    /// The entity types Presidio's predefined recognizers detect in English (Presidio 2.2.364), which run unless switched off.
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
        int passed = 0;
        Check(files.Count == FeedCatalog.All.Count && files.ContainsKey(FeedType.Seats), "the Seats recognizer file loads", ref passed);
        var seats = files[FeedType.Seats];
        Check(new[] { ("FirstName", "PERSON"), ("LastName", "PERSON"), ("BookedAsName", "PERSON"), ("PaxId", "PASSENGER_ID"), ("OrderId", "ORDER_ID"),
            ("EmpId", "EMPLOYEE_ID"), ("Attributes", "SENSITIVE_ATTRIBUTE") }.All(pair => seats.RecognizerForKey(pair.Item1)?.Entity == pair.Item2),
            "passenger-name, passenger-ID, order-ID, employee-ID and attribute keys are declared", ref passed);
        Check(seats.Recognizers.Any(r => r.Entity == "PERSON" && r.RunsInPresidio()), "the SURNAME/GIVEN name pattern runs in Presidio for every other string", ref passed);
        Check(seats.AllowList.AllowsKey("UARecloc") && seats.AllowList.AllowsKey("SeatNumber") && seats.AllowList.MatchesWhole("2026-11-09T14:05:33.120 PM"),
            "the PNR and the operational keys are allowed, and so are timestamps", ref passed);
        Check(seats.Anonymizers.ContainsKey(FeedAnonymizer.DefaultEntity), "the shipped file sets a DEFAULT anonymizer", ref passed);
        var baseline = ParityBaseline.Load(root);
        var uncovered = UncoveredEntities(baseline, files);
        Check(uncovered.Count == 0, "every baseline entity has a custom recognizer or a Presidio predefined entity"
            + (uncovered.Count == 0 ? "" : ": missing " + string.Join(", ", uncovered)), ref passed);
        var withoutAttributes = new Dictionary<FeedType, RecognizerFile>(files)
        {
            [FeedType.Seats] = seats with { Recognizers = seats.Recognizers.Where(r => r.Entity != "SENSITIVE_ATTRIBUTE").ToArray() },
        };
        Check(UncoveredEntities(baseline, withoutAttributes).Contains("Seats SENSITIVE_ATTRIBUTE"),
            "the coverage check reports a baseline entity removed from its feed's file", ref passed);
        var unkeyed = UnkeyedLocations(baseline, files);
        Check(unkeyed.Count == 0, "every baseline location sits under a recognizer key of its entity"
            + (unkeyed.Count == 0 ? "" : ": missing " + string.Join(", ", unkeyed)), ref passed);
        var withoutEmployeeKey = new Dictionary<FeedType, RecognizerFile>(files)
        {
            [FeedType.Seats] = seats with { Recognizers = seats.Recognizers.Select(r => r with { Keys = r.Keys.Where(k => k != "EmpId").ToArray() }).ToArray() },
        };
        Check(UnkeyedLocations(baseline, withoutEmployeeKey).Any(location => location.EndsWith("/EmpId", StringComparison.Ordinal)),
            "the key check reports a baseline location whose key was removed", ref passed);
        return passed;
    }

    /// <summary>
    /// Returns the baseline entity types, per feed, that neither a custom recognizer nor a Presidio predefined entity covers.
    /// </summary>
    /// <param name="baseline">The frozen baseline.</param>
    /// <param name="files">The recognizer files by feed.</param>
    /// <returns>"FEED ENTITY" entries in ordinal order.</returns>
    public static IReadOnlyList<string> UncoveredEntities(ParityBaselineDocument baseline, IReadOnlyDictionary<FeedType, RecognizerFile> files) =>
        baseline.Protected
            .Select(e => (Type: FeedCatalog.All.Single(f => f.FolderName == e.Feed).Type, Name: e.Feed, e.Entity))
            .Distinct()
            .Where(e => !files[e.Type].Recognizers.Any(r => r.Entity == e.Entity) && !PredefinedEntities.Contains(e.Entity))
            .Select(e => e.Name + " " + e.Entity)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Returns the baseline locations, as <c>sample pointer</c>, whose property path has no recognizer key of the baseline
    /// entity: the deepest key on the path decides, as in the protector.
    /// </summary>
    /// <param name="baseline">The frozen baseline.</param>
    /// <param name="files">The recognizer files by feed.</param>
    /// <returns>The unkeyed locations in baseline order.</returns>
    public static IReadOnlyList<string> UnkeyedLocations(ParityBaselineDocument baseline, IReadOnlyDictionary<FeedType, RecognizerFile> files) =>
        baseline.Protected
            .Where(e => files[FeedCatalog.All.Single(f => f.FolderName == e.Feed).Type] is var file
                && e.Location.Split('/').Where(s => s.Length > 0 && !s.All(char.IsAsciiDigit)).Reverse()
                    .Select(s => file.RecognizerForKey(s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
                    .FirstOrDefault(r => r is not null)?.Entity != e.Entity)
            .Select(e => e.Sample + " " + e.Location)
            .ToArray();

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
