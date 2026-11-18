using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;

/// <summary>
/// Checks the decoded-text protector (WR-014, WR-015) on the JSON feed: every JSON string is a candidate located by its key
/// path, mapped and skipped keys decide by property name, detected spans get their entity's action fitted to
/// width, a string in which nothing is detected is published unchanged and listed for review, numbers, booleans and nulls
/// never change, detection failures dead-letter, and every production sample rebuilds and validates. Runs with the shipped
/// <c>Presidio:DefaultDetection</c> (off); the checks that need Presidio's predefined recognizers turn it on.
/// </summary>
internal static class DetectingProtectorChecks
{

    #region Methods

    /// <summary>
    /// Runs the checks. Python must be started; the protector is configured here, after the failure-injection check.
    /// </summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <returns>A task that completes when every check passed.</returns>
    public static async Task RunAsync(string projectRoot)
    {
        string recognizers = Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        var files = RecognizerFileReader.ReadAll(recognizers);
        string samples = Path.Combine(projectRoot, "docs", "sample-docs");
        string seatsSample = Path.Combine(samples, "Seats", "SEATS.txt");
        // The shipped Presidio:DefaultDetection: only the file's custom recognizers and mapped keys act.
        var protector = new DetectingFeedProtector(files, defaultDetection: false);

        // Detection failure: the bridge has no registry for an unconfigured feed name, so the call fails and dead-letters.
        // Every string of the samples is decided by a key, so a made-up string under an undeclared key reaches Presidio.
        var unconfigured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.Seats] = files[FeedType.Seats] });
        await RejectAsync(() => unconfigured.ProtectAsync(FeedType.Seats, ParseJson("{\"Remarks\":\"CALL MILLER/JORDAN\"}"), CancellationToken.None),
            "Local Presidio detection failed", "a Presidio failure before configuration dead-letters");
        protector.Configure();

        int published = 0;
        foreach (string path in Directory.EnumerateFiles(samples, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Contains("Negative_Testing", StringComparison.Ordinal))
            {
                continue;
            }
            var feed = FeedFileRouting.FromPath(path);
            var codec = Codec(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] output = codec.Serialize(parsed, outcome.Values);
            codec.Validate(parsed, output);
            var strings = JsonFeedParser.StringFields(parsed);
            Check(parsed.Fields.All(f => outcome.Values[f.Id].Length == f.Value.Length), Path.GetFileName(path) + ": every protected string keeps its width");
            Check(parsed.Fields.Except(strings).All(f => outcome.Values[f.Id] == f.Value), Path.GetFileName(path) + ": numbers, booleans and nulls never change");
            var decoded = strings.Select(f => f.Value).Where(v => v.Any(char.IsAsciiLetterOrDigit) && DetectingFeedProtector.Shape(v) != v).ToHashSet(StringComparer.Ordinal);
            Check(outcome.Events.All(e => !decoded.Contains(e.Shape) && !decoded.Contains(e.Recognizer) && !decoded.Contains(e.Entity)),
                Path.GetFileName(path) + ": no detection event field carries a decoded value");
            // WR-018: a JSON string is located by its pointer; the value-free form keeps array indexes and declared names only.
            Check(outcome.Events.Count == strings.Count && outcome.Events.Zip(strings).All(pair => pair.First.Pointer == pair.Second.Id
                && pair.First.Pattern == pair.Second.Name && pair.First.SafePointer.Split('/').Length == pair.Second.Id.Split('/').Length
                && pair.First.SafePointer.Split('/').All(segment => segment.All(char.IsAsciiDigit) || !decoded.Contains(segment))),
                Path.GetFileName(path) + ": each event is located by its string's JSON Pointer, and the value-free pointer carries no value");
            // WR-020: the decoded values travel beside the events, one per slot, for the values flag only.
            Check(outcome.CandidateValues is { } carried && carried.Count == outcome.Events.Count && carried.SequenceEqual(strings.Select(f => f.Value)),
                Path.GetFileName(path) + ": the candidate values match the decoded strings by slot");
            published++;
        }
        Console.WriteLine($"PASS {published} production samples protected, rebuilt and validated with the recognizer files");

        // WR-018, WR-020: one block per message; every string in exactly one group; review lines carry a reason and a fix.
        // Made-up JSON events: undeclared property names are ? in the value-free form and full, encoded, with values.
        DetectionEvent[] events =
        [
            Json(new(0, 13, "AAAAAA/AAAAAA", "passengerName", "PERSON", 0.9, DetectionDecision.Changed, "mask"), "/Customers/0/BookedAsName", "/Customers/0/BookedAsName"),
            Json(new(1, 6, "AAA999", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5), "/Customers/0/Remark", "/Customers/0/?"),
            Json(new(2, 22, "AAAA AAA", DetectingFeedProtector.SpacyRecognizer, "LOCATION", 0.85, DetectionDecision.Changed, "mask", ReviewReason.SpacyGuess, 0.5), "/Notes/2", "/?/2"),
            Json(new(3, 6, "AA9999", DetectingFeedProtector.KeysToSkipRecognizer, "", 0, DetectionDecision.Allowed), "/Customers/0/UARecloc", "/Customers/0/UARecloc"),
            Json(new(4, 10, "AA99999999", "UsLicenseRecognizer", "US_DRIVER_LICENSE", 0.3, DetectionDecision.BelowThreshold, Reasons: ReviewReason.BelowThreshold, Threshold: 0.5), "/Customers/1/Licence", "/Customers/1/?"),
            Json(new(5, 7, "99AAA99", DetectingFeedProtector.EntityMappingRecognizer, "DATE_OF_BIRTH", 1, DetectionDecision.Changed, "mask"), "/Customers/1/DOB", "/Customers/1/DOB"),
            Json(new(6, 12, "AAAA AAAAAAA", "EmailRecognizer", "EMAIL_ADDRESS", 1, DetectionDecision.Changed, "replace", ReviewReason.NoAction | ReviewReason.Cut, 0.5), "/EventDetail/Contact", "/EventDetail/?"),
        ];
        string[] fictionalBefore = ["MILLER/JORDAN", "ABC123", "LINE ONE\nSAY \"HI\" \\ X", "XY1234", "XY99887766", "14MAR85", "CALL O'NEIL"];
        string[] fictionalAfter = ["######/######", "ABC123", "######################", "XY1234", "XY99887766", "*******", "CALL <EMAIL"];
        string[] Lines(string block) => block.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string Section(string block, string heading)
        {
            var lines = Lines(block);
            int start = Array.FindIndex(lines, line => line == "  " + heading);
            int end = Array.FindIndex(lines, start + 1, line => !line.StartsWith("    ", StringComparison.Ordinal));
            return string.Join("\n", lines[(start + 1)..(end < 0 ? lines.Length : end)]);
        }
        const string ReviewHeading = "Review (check these; each line has a fix)";
        var log = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, events);
        var valued = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, events, before: fictionalBefore, after: fictionalAfter);
        Check(Lines(log)[0] == "Seats " + Guid.Empty.ToString("N") + " DETECTION PUBLISHED file=- fields=7 pii=2 non-pii=1 review=4",
            "the header names the feed, transaction, outcome, file (- when none) and field counts, and no offset base for JSON");
        Check(Lines(log).Skip(1).All(line => line.StartsWith(' ')) && Lines(valued).Skip(1).All(line => line.StartsWith(' ')),
            "every line after the header is indented, so a collector keeps the block as one entry");
        string pii = Section(log, "PII fields (changed)"), nonPii = Section(log, "Non-PII fields (unchanged)"), review = Section(log, ReviewHeading);
        Check(new[] { "    /Customers/0/BookedAsName ", "    /Customers/1/DOB " }.All(pii.Contains) && nonPii.Contains("    /Customers/0/UARecloc ", StringComparison.Ordinal)
            && new[] { "    /Customers/0/? ", "    /?/2 ", "    /Customers/1/? ", "    /EventDetail/? " }.All(review.Contains), "each string is listed in its group, located by its value-free pointer");
        Check(Lines(log).Count(line => line.StartsWith("    /", StringComparison.Ordinal)) == events.Length, "each string appears exactly once");
        string ReviewLine(string at) => review.Split('\n').Single(line => line.StartsWith("    " + at + " ", StringComparison.Ordinal));
        Check(ReviewLine("/Customers/0/?").Contains("  unchanged  ", StringComparison.Ordinal)
            && ReviewLine("/Customers/0/?").Contains("reason=nothing-detected fix=\"add a recognizer if this is personal data, or an allow-list item if it is not\"", StringComparison.Ordinal),
            "a nothing-detected string is listed unchanged with its fix");
        Check(ReviewLine("/Customers/1/?").Contains("score=0.30<0.50", StringComparison.Ordinal) && ReviewLine("/Customers/1/?").Contains("reason=below-threshold", StringComparison.Ordinal),
            "a below-threshold string compares its score with the threshold");
        Check(ReviewLine("/?/2").Contains("  changed  ", StringComparison.Ordinal) && ReviewLine("/?/2").Contains("operator=mask decision=changed reason=spacy-guess", StringComparison.Ordinal),
            "a spaCy guess is listed for review although it changed the string");
        Check(ReviewLine("/EventDetail/?").Contains("reason=no-action+cut fix=\"add an <anonymizer> for EMAIL_ADDRESS; use mask for this entity, or a shorter new_value\"", StringComparison.Ordinal),
            "several reasons are listed with one fix each");
        Check(fictionalBefore.All(value => !log.Contains(value, StringComparison.Ordinal)) && fictionalAfter.Where(value => !value.All(c => c == '#')).All(value => !log.Contains(value, StringComparison.Ordinal))
            && !log.Contains("->", StringComparison.Ordinal) && log.Contains("len=13 shape=AAAAAA/AAAAAA", StringComparison.Ordinal),
            "a value-free block carries lengths and shapes, never a value");
        Check(new[] { "Remark", "Notes", "Licence", "Contact" }.All(name => !log.Contains(name, StringComparison.Ordinal))
            && new[] { "\"/Customers/0/Remark\"", "\"/Notes/2\"", "\"/Customers/1/Licence\"", "\"/EventDetail/Contact\"" }.All(valued.Contains),
            "an undeclared property name is ? without values and part of the full, encoded pointer with values");
        Check(Lines(valued).Length == Lines(log).Length, "an encoded value never breaks a line");
        Check(valued.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && valued.Contains("\"CALL O'NEIL\" -> \"CALL <EMAIL\"", StringComparison.Ordinal)
            && Section(valued, "Non-PII fields (unchanged)").Contains("\"XY1234\"  recognizer=keys_to_skip", StringComparison.Ordinal),
            "a changed string shows before -> after; an unchanged string shows its value once");
        string encoded = Lines(valued).Single(line => line.Contains("\"/Notes/2\"", StringComparison.Ordinal));
        string quoted = encoded[(encoded.IndexOf("\"LINE", StringComparison.Ordinal))..encoded.IndexOf(" -> ", StringComparison.Ordinal)];
        Check(JsonSerializer.Deserialize<string>(quoted) == fictionalBefore[2], "an encoded value reads back exactly");
        var rejected = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, events, "Fixture rejection.", fictionalBefore, null);
        Check(Lines(rejected)[0].EndsWith(" DETECTION DEAD-LETTERED file=- fields=7 pii=2 non-pii=1 review=4 reason=\"Fixture rejection.\"", StringComparison.Ordinal)
            && rejected.Contains("\"MILLER/JORDAN\" -> not published", StringComparison.Ordinal), "a dead-lettered block gives the reason and no after value");
        var allowedOnly = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, [events[3]]);
        Check(Section(allowedOnly, "PII fields (changed)") == "    (none)" && Section(allowedOnly, ReviewHeading) == "    (none)",
            "an empty group is printed as (none)");
        // Review R-003: fewer values than events must not fail; a slot without a value keeps the value-free form.
        var shortValues = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, events, before: ["MILLER/JORDAN"], after: fictionalAfter);
        Check(shortValues.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && shortValues.Contains("len=6 shape=AAA999", StringComparison.Ordinal),
            "a slot without a value keeps the value-free form");
        // A property name can hold a value or a newline: the value-free form hides it, the values form encodes it.
        DetectionEvent[] jsonEvents = [Json(new(0, 4, "AAAA", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5),
            "/0/con\ntacts/j.miller@example.com", "/0/?/?")];
        var jsonFree = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, jsonEvents);
        var jsonValued = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, jsonEvents, before: ["HOME"], after: ["HOME"]);
        Check(jsonFree.Contains("    /0/?/?  len=4 shape=AAAA  unchanged", StringComparison.Ordinal) && !jsonFree.Contains("miller", StringComparison.Ordinal),
            "a value-free JSON entry shows undeclared names as ?");
        Check(jsonValued.Contains("    \"/0/con\\ntacts/j.miller@example.com\"  \"HOME\"  unchanged", StringComparison.Ordinal) && Lines(jsonValued).Length == Lines(jsonFree).Length,
            "a values JSON entry shows the full pointer, encoded so a key cannot break the line");
        // Review R-009: one very long location must not pad every other line of the block to its width.
        DetectionEvent[] wide =
        [
            Json(new(0, 4, "AAAA", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5), "/0/" + new string('k', 5000), "/0/?"),
            Json(new(1, 6, "AAA999", "allow_list", "", 0, DetectionDecision.Allowed), "/0/UARecloc", "/0/UARecloc"),
        ];
        var wideBlock = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, Guid.Empty, wide, before: ["HOME", "K7QX2M"], after: ["HOME", "K7QX2M"]);
        Check(Lines(wideBlock).Single(line => line.Contains("UARecloc", StringComparison.Ordinal)).Length < 200,
            "a long location is written unpadded instead of widening every line");
        // Review R-010: zero-width and bidirectional formatting characters are escaped, and the value still reads back.
        string disguised = "CALL " + (char)0x202E + "MILLER" + (char)0x200B + " HOME";
        string encodedDisguise = PresidioDemo.Core.Logging.DetectionLog.Encode(disguised);
        Check(!encodedDisguise.Contains((char)0x202E) && !encodedDisguise.Contains((char)0x200B) && encodedDisguise.Contains("\\u202E", StringComparison.Ordinal)
            && JsonSerializer.Deserialize<string>(encodedDisguise) == disguised, "formatting characters are escaped and the value reads back");
        Check(PresidioDemo.Core.Logging.DetectionLog.Encode("O'NEIL" + (char)0x2028 + "X") == "\"O'NEIL\\u2028X\"", "an apostrophe stays readable and a line separator is escaped");
        // Review R-011: the worker writes each block with one Console.Out.Write. Console.Out is a synchronized writer, so
        // blocks written from many threads at once (as by several consumers per task) must come out whole.
        Check(Console.Out.GetType().Name == "SyncTextWriter", "the console writer the worker uses is synchronized");
        var captured = new StringWriter();
        var console = Console.Out;
        Console.SetOut(TextWriter.Synchronized(captured));
        try
        {
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                Console.Out.Write(PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.Seats, new Guid(i, 0, 0, new byte[8]), events, before: fictionalBefore, after: fictionalAfter)));
        }
        finally
        {
            Console.SetOut(console);
        }
        var written = Lines(captured.ToString());
        int perBlock = Lines(valued).Length;
        // "Seats " plus the 32-digit transaction and a space: the rest of each header, and every body line, must match.
        const int afterTransaction = 39;
        Check(written.Length == 400 * perBlock && Enumerable.Range(0, 400).All(b =>
            written[b * perBlock][afterTransaction..] == Lines(valued)[0][afterTransaction..]
            && written.Skip(b * perBlock + 1).Take(perBlock - 1).SequenceEqual(Lines(valued).Skip(1))),
            "400 blocks written from 16 threads at once each come out whole");
        Console.WriteLine("PASS the detection log writes one grouped block per message, with a fix per review line and values only when passed (WR-018, WR-020)");

        // WR-014 on the shipped file and a sample: mapped keys protect, skipped keys stay, and nothing is analyzed twice.
        var seatsOutcome = await protector.ProtectAsync(FeedType.Seats, Parse(seatsSample), CancellationToken.None);
        var seatsStrings = JsonFeedParser.StringFields(Parse(seatsSample));
        Check(seatsOutcome.Events.Any(e => e.Decision == DetectionDecision.Changed) && seatsOutcome.Events.Any(e => e.Decision == DetectionDecision.Allowed),
            "SEATS.txt has changed and allowed strings");
        Check(seatsOutcome.Events.Where(e => e.Decision is DetectionDecision.Allowed or DetectionDecision.NothingDetected or DetectionDecision.BelowThreshold)
            .All(e => seatsOutcome.Values[seatsStrings[e.Slot].Id] == seatsStrings[e.Slot].Value), "allowed and undetected strings are published unchanged");
        Check(seatsStrings.Where(f => f.Name.EndsWith("/UARecloc", StringComparison.Ordinal)).All(f => seatsOutcome.Values[f.Id] == f.Value)
            && seatsOutcome.Events.Where(e => e.Pattern.EndsWith("/UARecloc", StringComparison.Ordinal)).All(e => e.Decision == DetectionDecision.Allowed && e.Recognizer == DetectingFeedProtector.KeysToSkipRecognizer && e.SafePointer.EndsWith("/UARecloc", StringComparison.Ordinal)),
            "the PNR is published unchanged under its skipped key");

        // Made-up JSON through the shipped file (AC-004, AC-005, AC-013).
        var made = ParseJson("{\"Customers\":[{\"PaxId\":\"PAX-1001\",\"FirstName\":\"Jordan\",\"lastname\":\"Miller\",\"BookedAsName\":\"MILLER/JORDAN\","
            + "\"MiddleName\":null,\"UARecloc\":\"K7QX2M\",\"SeatNumber\":\"23C\",\"Attributes\":[\"WINDOW\",\"PETINCABIN\"],\"OrderVersion\":4,\"Infant\":false}],"
            + "\"Remarks\":[\"Please book a window seat\",\"REF 1234 MILLER/JORDAN\",\"Contact Alex Sample\",\"2026-11-09\"],"
            + "\"Nested\":{\"FirstName\":{\"Given\":\"Jordan\"},\"MiddleName\":[\"ANN\",\"LEE\"],\"LastName\":\"2026-11-09\"},\"contacts\":{\"jordan.miller@example.com\":\"HOME\"}}");
        var madeOutcome = await protector.ProtectAsync(FeedType.Seats, made, CancellationToken.None);
        string Made(string pointer) => madeOutcome.Values[pointer];
        DetectionEvent MadeEvent(string pointer) => madeOutcome.Events.Single(e => e.Pointer == pointer);
        Check(Made("/Customers/0/FirstName") == "******" && Made("/Customers/0/lastname") == "******" && Made("/Customers/0/BookedAsName") == "*************",
            "mapped keys mask names at their width, ignoring the key's case");
        Check(Made("/Customers/0/PaxId") == "PAX-1001" && Made("/Customers/0/Attributes/0") == "WINDOW" && Made("/Customers/0/Attributes/1") == "PETINCABIN",
            "a passenger ID and seat attributes, outside the names-only file, are published unchanged");
        Check(MadeEvent("/Customers/0/FirstName") is { Recognizer: DetectingFeedProtector.EntityMappingRecognizer, Entity: "PERSON", Score: 1, Decision: DetectionDecision.Changed, Operator: "mask" },
            "a mapped key decision names entity_mapping, the entity and the operator, with score 1");
        Check(Made("/Nested/MiddleName/0") == "***" && Made("/Nested/MiddleName/1") == "***", "array values under a key are that key's entity");
        Check(Made("/Nested/FirstName/Given") == "******", "a string nested under a personal-data key is protected by that key (R-012)");
        Check(Made("/Nested/LastName") == "**********", "a mapped key wins over an allow-list item, so a value it declares is never released (R-004)");
        Check(Made("/Customers/0/UARecloc") == "K7QX2M" && Made("/Customers/0/SeatNumber") == "23C" && MadeEvent("/Customers/0/SeatNumber") is { Decision: DetectionDecision.Allowed, Recognizer: DetectingFeedProtector.KeysToSkipRecognizer },
            "skipped keys publish their values unchanged without analysis");
        Check(Made("/Customers/0/OrderVersion") == "4" && Made("/Customers/0/Infant") == "false" && Made("/Customers/0/MiddleName") == ""
            && madeOutcome.Events.Count == JsonFeedParser.StringFields(made).Count, "numbers, booleans and nulls are not candidates and never change");
        Check(Made("/Remarks/0") == "Please book a window seat", "a string in which nothing is detected is published unchanged");
        Check(Made("/Remarks/1") == "REF 1234 *************", "the SURNAME/GIVEN pattern masks a name inside free text under any key");
        Check(Made("/Remarks/2") == "Contact Alex Sample" && MadeEvent("/Remarks/2") is { Decision: DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected },
            "with default detection off, a name no definition covers is published unchanged and listed as nothing-detected");
        Check(madeOutcome.Events.All(e => e.Recognizer is "passengerName" or "none" or "allow_list" or DetectingFeedProtector.EntityMappingRecognizer
            or DetectingFeedProtector.KeysToSkipRecognizer && !e.Reasons.HasFlag(ReviewReason.SpacyGuess)),
            "with default detection off, only the file's own definitions decide: no predefined or spaCy decision and no spacy-guess");
        Check(Made("/Remarks/3") == "2026-11-09", "a whole-string allow-list item keeps a timestamp");
        var dynamicKey = MadeEvent("/contacts/jordan.miller@example.com");
        Check(dynamicKey.SafePointer == "/?/?" && dynamicKey.Pattern == "/contacts/jordan.miller@example.com",
            "an undeclared key is logged as ?, so a value used as a property name stays out of the value-free log");
        Check(MadeEvent("/Customers/0/FirstName").SafePointer == "/?/0/FirstName" && MadeEvent("/Remarks/0").SafePointer == "/?/0",
            "a declared key keeps its name in the value-free pointer, and array indexes stay");
        // WR-016: a number or boolean under a personal-data key cannot be protected in its JSON type, so it dead-letters.
        await RejectAsync(() => protector.ProtectAsync(FeedType.Seats, ParseJson("{\"Customers\":[{\"FirstName\":1001}]}"), CancellationToken.None),
            "number or boolean", "a number under a personal-data key dead-letters");
        await RejectAsync(() => protector.ProtectAsync(FeedType.Seats, ParseJson("{\"LastName\":[\"MILLER\",true]}"), CancellationToken.None),
            "number or boolean", "a boolean nested under a personal-data key dead-letters");
        var unmappedNumber = await protector.ProtectAsync(FeedType.Seats, ParseJson("{\"Customers\":[{\"PaxId\":1001,\"FirstName\":\"Jordan\"}]}"), CancellationToken.None);
        Check(unmappedNumber.Values["/Customers/0/PaxId"] == "1001" && unmappedNumber.Values["/Customers/0/FirstName"] == "******",
            "a number under a key the names-only file does not map is published unchanged");
        // Bytes outside a changed string stay exactly as received: numeric spelling, escapes, whitespace and nulls.
        string spelled = "{ \"OrderVersion\":4.0e0, \"Infant\":false,\"Cabin\":\"\\u0059\",\"FirstName\":\"Em\\u0069ly\",\"MiddleName\":null }";
        var spelledParsed = ParseJson(spelled);
        var spelledOutcome = await protector.ProtectAsync(FeedType.Seats, spelledParsed, CancellationToken.None);
        byte[] spelledOutput = Codec(FeedType.Seats).Serialize(spelledParsed, spelledOutcome.Values);
        Codec(FeedType.Seats).Validate(spelledParsed, spelledOutput);
        Check(Encoding.UTF8.GetString(spelledOutput) == spelled.Replace("\"Em\\u0069ly\"", "\"*****\"", StringComparison.Ordinal),
            "only the mapped string changes; every other byte, escapes and numeric spelling included, is published as received");
        // AC-011: there is no PNR anchor; a message whose PNR is missing or not PNR-shaped still publishes.
        var noPnr = await protector.ProtectAsync(FeedType.Seats, ParseJson("{\"Customers\":[{\"FirstName\":\"Jordan\",\"UARecloc\":\"AB\"}]}"), CancellationToken.None);
        Check(noPnr.Values["/Customers/0/UARecloc"] == "AB" && noPnr.Values["/Customers/0/FirstName"] == "******", "a malformed PNR is published as received, not dead-lettered");
        Console.WriteLine("PASS the shipped file decides per JSON string: mapped keys, skipped keys, allow list, Presidio, unchanged when nothing is detected");

        // AC-003, AC-006, AC-008: test-only files reconfigure the Seats registry, so the shipped one is restored afterwards.
        string Temporary(string content)
        {
            string file = Path.Combine(Path.GetTempPath(), "seats-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, content);
            return file;
        }
        DetectingFeedProtector Configured(string file, bool defaultDetection = false)
        {
            var configured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.Seats] = RecognizerFileReader.Read(file, FeedType.Seats) },
                defaultDetection);
            configured.Configure();
            return configured;
        }
        string empty = Temporary("<recognizers feed=\"Seats\" />");
        string noCards = Temporary("<recognizers feed=\"Seats\"><recognizer name=\"CreditCardRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<anonymizer entity=\"PERSON\" type=\"mask\" /></recognizers>");
        string withDefault = Temporary("<recognizers feed=\"Seats\"><anonymizer entity=\"PERSON\" type=\"mask\" /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        // These files rely on Presidio's predefined recognizers, so they run with Presidio:DefaultDetection on.
        try
        {
            var bare = await ProtectStringsAsync(Configured(empty, defaultDetection: true), "Please book a window seat", "Contact Alex Sample", "alex@example.com",
                "Card 4111 1111 1111 1111");
            Check(bare[0] == "Please book a window seat", "an emptied file starts and publishes an undetected string unchanged");
            Check(bare[1] == "Contact <PERSON>   " && bare[2] == "<EMAIL_ADDRESS> " && bare[3] == "Card " + "<CREDIT_CARD>".PadRight(19),
                "with default detection on and nothing configured, Presidio's predefined recognizers detect, and replace is fitted to width");
            var emptied = await Configured(empty, defaultDetection: true).ProtectAsync(FeedType.Seats, ParseJson("{\"FirstName\":\"Jordan\",\"SeatNumber\":\"23C\"}"), CancellationToken.None);
            Check(emptied.Events.All(e => e.SafePointer.EndsWith("/?", StringComparison.Ordinal) && e.Recognizer is not (DetectingFeedProtector.EntityMappingRecognizer or DetectingFeedProtector.KeysToSkipRecognizer)),
                "without key lists, no property name is declared, mapped or skipped");
            var switchedOff = await ProtectStringsAsync(Configured(noCards, defaultDetection: true), "Card 4111 1111 1111 1111", "Contact Alex Sample", "alex@example.com");
            Check(switchedOff[0] == "Card 4111 1111 1111 1111", "a predefined recognizer switched off detects nothing");
            Check(switchedOff[1] == "Contact ***********" && switchedOff[2] == "<EMAIL_ADDRESS> ", "an entity uses its own anonymizer, else replace");
            var fallback = await ProtectStringsAsync(Configured(withDefault, defaultDetection: true), "alex@example.com");
            Check(fallback[0] == "****************", "an entity without its own anonymizer uses DEFAULT");
            Console.WriteLine("PASS with default detection on, an emptied file runs Presidio's defaults; switches and anonymizers change the output with no code change");

            // Review reasons as the protector produces them (made-up strings).
            var guess = await ProtectOutcomeAsync(Configured(withDefault, defaultDetection: true), "Contact Alex Sample", "alex@example.com", "LI WU");
            Check(guess.Events[0].Reasons.HasFlag(ReviewReason.SpacyGuess) && !guess.Events[0].Reasons.HasFlag(ReviewReason.NoAction),
                "a spaCy PERSON with its own anonymizer is a spacy-guess, not a no-action");
            Check(guess.Events[1].Reasons.HasFlag(ReviewReason.NoAction) && guess.Events[1].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal),
                "an entity that used DEFAULT is a no-action naming that entity");
            var mixed = await ProtectOutcomeAsync(Configured(noCards, defaultDetection: true), "Contact Alex Sample at alex@example.com");
            Check(mixed.Events[0].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal) && !mixed.Events[0].NoActionEntities.Contains("PERSON", StringComparison.Ordinal),
                "the no-action fix names only the entities without their own anonymizer");
            var cut = await ProtectOutcomeAsync(Configured(empty, defaultDetection: true), "LI WU");
            Check(Value(cut, 0) == "<PERS" && cut.Events[0].Reasons.HasFlag(ReviewReason.Cut), "a replace result longer than the value is cut and listed as cut");
        }
        finally
        {
            File.Delete(empty);
            File.Delete(noCards);
            File.Delete(withDefault);
            protector.Configure();
        }

        // Presidio:DefaultDetection, the four cases on one made-up message: a mapped name and an e-mail address in a note
        // that no definition covers. Each case reconfigures the Seats registry, so the shipped one is restored afterwards.
        const string Flagged = "{\"FirstName\":\"JORDAN\",\"Note\":\"mail jordan.miller@example.com\"}";
        string nothingDefined = Temporary("<recognizers feed=\"Seats\" />");
        try
        {
            Check(await ProtectJsonAsync(protector, Flagged) == "{\"FirstName\":\"******\",\"Note\":\"mail jordan.miller@example.com\"}",
                "default detection off with definitions: only the mapped key and the custom recognizers act; the e-mail address is published");
            Check(await ProtectJsonAsync(Configured(Path.Combine(recognizers, "seats.xml"), defaultDetection: true), Flagged) == "{\"FirstName\":\"******\",\"Note\":\"mail *************************\"}",
                "default detection on with definitions: the mapped key and Presidio's predefined recognizers act, so the e-mail address is masked too");
            var presidioOnly = Configured(nothingDefined, defaultDetection: true);
            var presidioParsed = ParseJson(Flagged);
            var presidioOutcome = await presidioOnly.ProtectAsync(FeedType.Seats, presidioParsed, CancellationToken.None);
            var note = JsonFeedParser.StringFields(presidioParsed).ToList().FindIndex(f => f.Id.EndsWith("/Note", StringComparison.Ordinal));
            Check(presidioOutcome.ProtectedCandidateValues![note] == "mail " + "<EMAIL_ADDRESS>".PadRight(25)
                && presidioOutcome.Events.All(e => e.Recognizer is not (DetectingFeedProtector.EntityMappingRecognizer or "passengerName")),
                "default detection on without definitions: only Presidio's predefined recognizers act, with Presidio's replace fitted to width");
            var bareMessage = ParseJson(Flagged);
            var bareOutcome = await Configured(nothingDefined).ProtectAsync(FeedType.Seats, bareMessage, CancellationToken.None);
            Check(Codec(FeedType.Seats).Serialize(bareMessage, bareOutcome.Values).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Flagged))
                && bareOutcome.Events.All(e => e.Recognizer == "none" && e.Decision == DetectionDecision.NothingDetected),
                "default detection off without definitions: the output equals the input byte for byte and nothing is detected");
            Console.WriteLine("PASS Presidio:DefaultDetection: off acts on the file's definitions only, on adds Presidio's predefined recognizers");
        }
        finally
        {
            File.Delete(nothingDefined);
            protector.Configure();
        }

        // Overlaps, keep and hash with custom recognizers (made-up codes): CODE_A covers AB1234 at 0.9 and CODE_B covers
        // 1234CD at 0.7, so CODE_A wins the shared 1234 and CODE_B keeps CD. chars_to_mask counts from CODE_B's own start,
        // so masking its first two characters changes only what CODE_A already took. These run with the shipped default
        // detection off, so only the custom recognizers act; spaCy is also switched off by name, because with default
        // detection on it reads a made-up code such as AB1234CD as a PERSON and would win the whole string. The key
        // recognizer shows a key's entity takes its own anonymizer too.
        string Codes(string actionA, string actionB) => Temporary("<recognizers feed=\"Seats\">"
            + "<recognizer name=\"CryptoRecognizer\" type=\"predefined\" enabled=\"true\" />"
            + "<recognizer name=\"SpacyRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<recognizer name=\"codeA\" type=\"custom\" supported_entity=\"CODE_A\"><pattern name=\"a\" score=\"0.9\">AB\\d{4}</pattern></recognizer>"
            + "<entity_mapping><key name=\"CodeA\" entity=\"CODE_A\" /></entity_mapping>"
            + "<recognizer name=\"codeB\" type=\"custom\" supported_entity=\"CODE_B\"><pattern name=\"b\" score=\"0.7\">\\d{4}CD</pattern></recognizer>"
            + "<anonymizer entity=\"CODE_A\" " + actionA + " /><anonymizer entity=\"CODE_B\" " + actionB + " /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        string overlap = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\"");
        string partial = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\" chars_to_mask=\"2\"");
        string keepFile = Codes("type=\"keep\"", "type=\"mask\"");
        string hashed = Codes("type=\"hash\" salt=\"demo\"", "type=\"mask\"");
        string typo = Temporary("<recognizers feed=\"Seats\"><recognizer name=\"CreditCardRecogniser\" type=\"predefined\" /></recognizers>");
        // Key-path context: a weak pattern (0.3) is raised over the threshold by Presidio's context enhancer only when a
        // property name on the string's key path holds its context word, as Presidio's analyze_dict passes a key.
        string context = Temporary("<recognizers feed=\"Seats\"><recognizer name=\"SpacyRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<recognizer name=\"memberCode\" type=\"custom\" supported_entity=\"MEMBER_CODE\"><pattern name=\"code\" score=\"0.3\">ZX\\d{4}</pattern>"
            + "<context>membership</context></recognizer><anonymizer entity=\"MEMBER_CODE\" type=\"mask\" /></recognizers>");
        try
        {
            var both = await ProtectOutcomeAsync(Configured(overlap), "AB1234CD");
            Check(Value(both, 0) == "******##" && both.Events[0].Reasons.HasFlag(ReviewReason.Overlap), $"the higher score wins an overlap; the loser masks its own characters (got {Value(both, 0)}, {both.Events[0].Recognizer}, {both.Events[0].Entity}, {both.Events[0].Reasons})");
            var counted = await ProtectOutcomeAsync(Configured(partial), "AB1234CD");
            Check(Value(counted, 0) == "******CD", "chars_to_mask counts from the losing span's own start");
            var keep = await ProtectOutcomeAsync(Configured(keepFile), "AB1234");
            Check(Value(keep, 0) == "AB1234" && keep.Events[0].Decision == DetectionDecision.Kept && keep.Events[0].Reasons == ReviewReason.None,
                "the keep operator publishes a detected value unchanged as kept, not for review");
            var keptByKey = await Configured(keepFile).ProtectAsync(FeedType.Seats, ParseJson("{\"CodeA\":\"anything\"}"), CancellationToken.None);
            Check(keptByKey.Values["/CodeA"] == "anything" && keptByKey.Events[0] is { Decision: DetectionDecision.Kept, Recognizer: DetectingFeedProtector.EntityMappingRecognizer, Reasons: ReviewReason.None },
                "a mapped key's entity uses its own anonymizer: keep publishes it unchanged as kept");
            var hash = await ProtectOutcomeAsync(Configured(hashed), "AB1234");
            Check(Value(hash, 0).Length == 6 && Value(hash, 0) != "AB1234" && !hash.Events[0].Reasons.HasFlag(ReviewReason.Cut),
                "a hash cut to width is not listed as cut");
            try
            {
                Configured(typo);
                throw new Exception("an unknown predefined recognizer name must stop startup");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("CreditCardRecogniser", StringComparison.Ordinal))
            {
                // Names are checked with default detection off too, although no predefined recognizer runs.
            }
            var keyContext = ParseJson("{\"MembershipCode\":\"ZX1234\",\"Remarks\":\"ZX1234\"}");
            var keyOutcome = await Configured(context).ProtectAsync(FeedType.Seats, keyContext, CancellationToken.None);
            Check(keyOutcome.Values["/MembershipCode"] == "******" && keyOutcome.Events[0].Score >= 0.5,
                "a property name holding a recognizer's context word raises a weak match over the threshold");
            Check(keyOutcome.Values["/Remarks"] == "ZX1234" && keyOutcome.Events[1].Decision == DetectionDecision.BelowThreshold,
                "the same value under a key without the context word stays below the threshold and is listed for review");
            Console.WriteLine("PASS with default detection off, overlaps, chars_to_mask, keep, hash and key-path context behave per span and per key; an unknown predefined name stops startup and is named");
        }
        finally
        {
            foreach (string file in new[] { overlap, partial, keepFile, hashed, typo, context })
            {
                File.Delete(file);
            }
            protector.Configure();
        }

        // WR-020: a rejection after detection keeps its events and values, with the reason unchanged. A codec whose
        // rebuild check fails stands in for a rebuild that differs outside the protected strings (WR-016).
        var failingRebuild = FeedWorkflowRouter.Create(protector, _ => new RebuildRejectingCodec(new SeatsParser()));
        var afterDetection = await new FeedMessageProcessor(failingRebuild)
            .ProcessAsync(FeedType.Seats, Guid.NewGuid(), "SEATS.txt", File.ReadAllBytes(seatsSample), CancellationToken.None);
        Check(!afterDetection.Published && afterDetection.Reason == RebuildRejectingCodec.Reason
            && afterDetection.DetectionEvents is { Count: > 0 } kept && afterDetection.DetectionValues?.Count == kept.Count,
            "a message dead-lettered after detection keeps its events and values, with the same reason");
        Console.WriteLine("PASS a message dead-lettered after detection keeps its events and values for the detection log (WR-020)");

        // WR-020: a message rejected before detection has no events to log.
        var reviewed = new FeedMessageProcessor(FeedPipelineConfiguration.Load(recognizers).CreateRouter(protector));
        foreach (var (name, input) in new[]
        {
            ("SEATS_02_Negative_Testing.txt", File.ReadAllBytes(Path.Combine(samples, "Seats", "SEATS_02_Negative_Testing.txt"))),
            ("truncated.bin", new byte[] { 0x7B, 0x22, 0x78 }),
        })
        {
            var before = await reviewed.ProcessAsync(FeedType.Seats, Guid.NewGuid(), name, input, CancellationToken.None);
            Check(!before.Published && before.DetectionEvents is null && before.DetectionValues is null, name + ": rejected before detection, so nothing to log");
        }
        Console.WriteLine("PASS messages rejected before detection carry no events or values");

        // R-003: a recognizer pattern that times out dead-letters instead of publishing unchecked text. This reconfigures
        // the Seats registry, so it restores the shipped configuration afterwards.
        string slow = Temporary(File.ReadAllText(Path.Combine(recognizers, "seats.xml")).Replace("</recognizers>",
            "<recognizer name=\"slowCheck\" type=\"custom\" supported_entity=\"DEMO_CODE\"><pattern name=\"slow\" score=\"0.85\">(A|AA)+$</pattern></recognizer></recognizers>",
            StringComparison.Ordinal));
        try
        {
            var slowProtector = Configured(slow);
            await RejectAsync(() => slowProtector.ProtectAsync(FeedType.Seats, ParseJson("{\"UARecloc\":\"K7QX2M\",\"Note\":\"" + new string('A', 40) + "!\"}"), CancellationToken.None),
                "Local Presidio detection failed", "a pattern timeout dead-letters the message");
        }
        finally
        {
            File.Delete(slow);
            protector.Configure();
        }

        // V-12: numbers are never candidates or changed, so a message whose number differs is published with that number
        // exactly as received.
        var seatsCodec = new SeatsParser();
        var seatsMessage = Message(seatsSample, FeedType.Seats);
        var seatsEnvelope = FeedPayloadReader.Read(seatsMessage);
        string text = Encoding.UTF8.GetString(seatsEnvelope.Payload);
        Check(text.Split("\"OrderVersion\":4,").Length == 2, "SEATS.txt holds its order version once");
        byte[] mismatched = Encoding.UTF8.GetBytes(text.Replace("\"OrderVersion\":4,", "\"OrderVersion\":7,", StringComparison.Ordinal));
        var mismatchMessage = new FeedMessage(seatsMessage.Delivery, seatsEnvelope.Serialize(mismatched));
        var mismatchParsed = seatsCodec.Parse(mismatchMessage);
        var mismatchOutcome = await protector.ProtectAsync(FeedType.Seats, mismatchParsed, CancellationToken.None);
        byte[] mismatchOutput = seatsCodec.Serialize(mismatchParsed, mismatchOutcome.Values);
        seatsCodec.Validate(mismatchParsed, mismatchOutput);
        Check(Encoding.UTF8.GetString(FeedPayloadReader.Read(new FeedMessage(seatsMessage.Delivery, mismatchOutput)).Payload).Contains("\"OrderVersion\":7,", StringComparison.Ordinal),
            "Seats number published unchanged");
        Console.WriteLine("PASS a Seats message with a changed number is published with the number unchanged");

        // WR-019: narrowing a recognizer breaks names-only parity with the frozen baseline before merge. This reconfigures
        // the Seats registry, so it runs last and restores the shipped configuration afterwards.
        string narrowed = Temporary(File.ReadAllText(Path.Combine(recognizers, "seats.xml"))
            .Replace("<key name=\"FirstName\" entity=\"PERSON\" />", "", StringComparison.Ordinal).Replace("<key name=\"LastName\" entity=\"PERSON\" />", "", StringComparison.Ordinal));
        try
        {
            int reviewedMisses = await ParityMissesAsync(protector, projectRoot, FeedType.Seats);
            Check(reviewedMisses == 0, "the shipped recognizers must have no unapproved Seats names-only parity miss");
            int narrowedMisses = await ParityMissesAsync(Configured(narrowed), projectRoot, FeedType.Seats);
            Check(narrowedMisses > 0, "a narrowed name recognizer must fail names-only parity");
            Console.WriteLine($"PASS a narrowed Seats name recognizer fails names-only parity with the frozen baseline ({narrowedMisses} unapproved misses)");
        }
        finally
        {
            File.Delete(narrowed);
            protector.Configure();
        }
    }

    /// <summary>
    /// Adds a made-up JSON location to a fictional event for the detection log checks.
    /// </summary>
    private static DetectionEvent Json(DetectionEvent e, string pointer, string safePointer) =>
        e with { Pointer = pointer, SafePointer = safePointer };

    /// <summary>
    /// Protects a made-up JSON message and returns the rebuilt, validated output as text.
    /// </summary>
    private static async Task<string> ProtectJsonAsync(DetectingFeedProtector protector, string json)
    {
        var parsed = ParseJson(json);
        var outcome = await protector.ProtectAsync(FeedType.Seats, parsed, CancellationToken.None);
        var codec = Codec(FeedType.Seats);
        byte[] output = codec.Serialize(parsed, outcome.Values);
        codec.Validate(parsed, output);
        return Encoding.UTF8.GetString(output);
    }

    /// <summary>
    /// Protects made-up strings as one parsed JSON message and returns the published values in order.
    /// </summary>
    private static async Task<string[]> ProtectStringsAsync(DetectingFeedProtector protector, params string[] values)
    {
        var outcome = await ProtectOutcomeAsync(protector, values);
        return Enumerable.Range(0, values.Length).Select(i => Value(outcome, i)).ToArray();
    }

    /// <summary>
    /// Protects made-up strings as one parsed JSON message: an array under the undeclared property <c>Remarks</c>, so each
    /// string's slot is its array index.
    /// </summary>
    private static ValueTask<DetectionOutcome> ProtectOutcomeAsync(DetectingFeedProtector protector, params string[] values) =>
        protector.ProtectAsync(FeedType.Seats, ParseJson(JsonSerializer.Serialize(new { Remarks = values })), CancellationToken.None);

    /// <summary>
    /// Returns the published value of a made-up string from <see cref="ProtectOutcomeAsync"/>.
    /// </summary>
    private static string Value(DetectionOutcome outcome, int slot) => outcome.Values["/Remarks/" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>
    /// Counts in-scope (PERSON) baseline values of one feed that the protector publishes unchanged and that are not approved
    /// exceptions (WR-019); other baseline values are out of the names-only scope. Value-free: compares the strings at the
    /// frozen JSON Pointers only.
    /// </summary>
    /// <param name="protector">The protector to measure.</param>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="feed">The feed.</param>
    /// <returns>The number of unapproved misses.</returns>
    private static async Task<int> ParityMissesAsync(DetectingFeedProtector protector, string projectRoot, FeedType feed)
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-baseline.json")));
        using var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-exceptions.json")));
        var exceptions = approved.RootElement.EnumerateArray().Select(e => (e.GetProperty("sample").GetString(), e.GetProperty("location").GetString())).ToHashSet();
        string folder = FeedCatalog.Get(feed).FolderName;
        int misses = 0;
        foreach (var group in baseline.RootElement.GetProperty("protected").EnumerateArray()
            .Where(e => e.GetProperty("feed").GetString() == folder && e.GetProperty("entity").GetString() == "PERSON")
            .GroupBy(e => e.GetProperty("sample").GetString()!))
        {
            string path = Path.Combine(projectRoot, "docs", "sample-docs", folder, group.Key);
            var codec = FeedCodecRegistry.Create(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            var before = parsed.Fields.ToDictionary(f => f.Id, f => f.Value, StringComparer.Ordinal);
            var after = codec.Parse(new FeedMessage(message.Delivery, codec.Serialize(parsed, outcome.Values))).Fields.ToDictionary(f => f.Id, f => f.Value, StringComparer.Ordinal);
            foreach (var entry in group)
            {
                string location = entry.GetProperty("location").GetString()!;
                // Only characters that were not already * need to change; a value that is all * was masked before it arrived.
                bool caught = before[location].Zip(after[location]).All(pair => pair.First == '*' || pair.First != pair.Second);
                if (!caught && !exceptions.Contains((group.Key, location)))
                {
                    misses++;
                }
            }
        }
        return misses;
    }

    /// <summary>
    /// Returns the codec the decoded-text path uses for a feed.
    /// </summary>
    private static IFeedCodec Codec(FeedType feed) => FeedCodecRegistry.Create(feed);

    /// <summary>
    /// Builds a message from a sample file.
    /// </summary>
    private static FeedMessage Message(string path, FeedType feed) => new(new FeedDelivery(feed, Guid.NewGuid(), Path.GetFileName(path), path), File.ReadAllBytes(path));

    /// <summary>
    /// Parses a sample with the decoded-text codec of its feed.
    /// </summary>
    private static ParsedFeed Parse(string path)
    {
        var feed = FeedFileRouting.FromPath(path);
        return Codec(feed).Parse(Message(path, feed));
    }

    /// <summary>
    /// Parses made-up JSON as a raw Seats payload.
    /// </summary>
    private static ParsedFeed ParseJson(string json) =>
        Codec(FeedType.Seats).Parse(new FeedMessage(new FeedDelivery(FeedType.Seats, Guid.NewGuid(), "fixture.bin", ""), Encoding.UTF8.GetBytes(json)));

    /// <summary>
    /// Requires an InvalidDataException whose message contains the expected text.
    /// </summary>
    private static async Task RejectAsync(Func<ValueTask<DetectionOutcome>> action, string expected, string label)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new Exception("Expected rejection: " + label);
    }

    /// <summary>
    /// Requires a condition.
    /// </summary>
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
    }

    #endregion
}

/// <summary>
/// Wraps a real codec but fails its rebuild check, so a message is rejected after detection ran.
/// </summary>
/// <param name="inner">The codec that parses and rebuilds the message.</param>
internal sealed class RebuildRejectingCodec(IFeedCodec inner) : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The value-free reason the rebuild check gives.
    /// </summary>
    public const string Reason = "Fixture rebuild check failed.";

    #endregion

    #region Methods

    /// <summary>
    /// Parses the message with the wrapped codec.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The wrapped codec's parse.</returns>
    public ParsedFeed Parse(FeedMessage message) => inner.Parse(message);

    /// <summary>
    /// Rebuilds the message with the wrapped codec.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="anonymizedFields">The protected values by field identifier.</param>
    /// <returns>The rebuilt message.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => inner.Serialize(original, anonymizedFields);

    /// <summary>
    /// Always rejects the rebuilt message.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="serialized">The rebuilt message.</param>
    /// <exception cref="InvalidDataException">Always.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => throw new InvalidDataException(Reason);

    #endregion
}
