using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;

#region Execution

// Generic JSON codec checks (V-03): the shared System.Text.Json codec against an independent JSON reader, the frozen
// baseline locations, exact round trips and fail-closed validation. Output is value-free: names, counts and locations only.
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
int checks = 0;
// Scalar and string counts of the supplied samples and the two-passenger fixture, as the independent reader below finds them.
var expectedScalars = new Dictionary<string, (int Scalars, int Strings)>
{
    ["SEATS.txt"] = (59, 58), ["SEATS_03.txt"] = (51, 50), ["seats-mq-export.txt"] = (106, 105)
};
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "tests", "fixtures", "parity-baseline.json")));
var locations = baseline.RootElement.GetProperty("protected").EnumerateArray().Concat(baseline.RootElement.GetProperty("pnrs").EnumerateArray())
    .Where(e => e.GetProperty("feed").GetString() == "Seats")
    .Select(e => (Sample: e.GetProperty("sample").GetString()!, Pointer: e.GetProperty("location").GetString()!, Length: e.GetProperty("length").GetInt32()))
    .ToArray();
var recognizers = RecognizerFileReader.ReadAll(Path.Combine(project, "src", "PresidioDemo.Core", "Configuration", "recognizers"))[FeedType.Seats];
var codec = FeedCodecRegistry.Create(FeedType.Seats);
Check(codec is SeatsParser and JsonFeedParser, "the registry selects the Seats parser over the shared System.Text.Json codec");

var samples = Directory.EnumerateFiles(Path.Combine(project, "docs", "sample-docs"), "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
    .Append(Path.Combine(project, "tests", "unit-tests", "fixtures", "seats-mq-export.txt")).ToArray();
Check(samples.Length == 4 && samples.Take(3).All(path => FeedFileRouting.FromPath(path) == FeedType.Seats),
    "3 Seats samples and the two-passenger fixture are checked, found " + samples.Length);
int compared = 0;
foreach (string path in samples)
{
    string name = Path.GetFileName(path);
    byte[] original = File.ReadAllBytes(path);
    var message = Message(original, name);
    if (name.Contains("Negative_Testing", StringComparison.Ordinal))
    {
        // SEATS_02 is the SEATS export with CCSID 437: JSON needs UTF-8, and no code-page conversion is guessed.
        Reject(() => codec.Parse(message), name + ": an MQ export without CCSID 1208");
        Console.WriteLine($"PASS {name,-30} rejected before detection: its MQ export is not CCSID 1208");
        continue;
    }
    var envelope = FeedPayloadReader.Read(message);
    var parsed = codec.Parse(message);
    var independent = Independent(envelope.Payload);
    var strings = JsonFeedParser.StringFields(parsed);
    Check(parsed.Fields.SequenceEqual(independent), name + ": the independent JSON reader agrees on every scalar, path and value");
    Check(parsed.Fields.Count == expectedScalars[name].Scalars && strings.Count == expectedScalars[name].Strings,
        $"{name}: {parsed.Fields.Count} scalars and {strings.Count} string candidates, expected {expectedScalars[name]}");
    using (var document = JsonDocument.Parse(envelope.Payload))
    {
        Check(strings.Count == CountStrings(document.RootElement), name + ": every JSON string scalar is a detection candidate");
    }

    // A-002: every frozen baseline location (protected value or PNR) is one string candidate of the baseline length.
    foreach (var location in locations.Where(l => l.Sample == name))
    {
        var candidate = strings.SingleOrDefault(field => field.Id == location.Pointer);
        Check(candidate is not null && candidate.Value.Length == location.Length, $"{name}: baseline location {location.Pointer} is a string candidate of its length");
        compared++;
    }

    // Unchanged values rebuild the original bytes exactly, as an MQ export and as a raw payload.
    var unchanged = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
    byte[] same = codec.Serialize(parsed, unchanged);
    codec.Validate(parsed, same);
    Check(same.AsSpan().SequenceEqual(original), name + ": exact MQ export round trip");
    var raw = codec.Parse(Message(envelope.Payload, "raw.bin"));
    Check(codec.Serialize(raw, raw.Fields.ToDictionary(f => f.Id, f => f.Value)).AsSpan().SequenceEqual(envelope.Payload), name + ": exact raw JSON round trip");

    // Same-width masks of every string keep the length, the MQ metadata and hex rows, and every other value.
    var stringIds = strings.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
    var masked = parsed.Fields.ToDictionary(f => f.Id, f => stringIds.Contains(f.Id) ? new string('*', f.Value.Length) : f.Value);
    byte[] protectedContent = codec.Serialize(parsed, masked);
    codec.Validate(parsed, protectedContent);
    var afterMask = FeedPayloadReader.Read(Message(protectedContent, name));
    Check(protectedContent.Length == original.Length && afterMask.Header == envelope.Header && afterMask.HexRowBytes == envelope.HexRowBytes
        && Independent(afterMask.Payload).All(field => field.Value == masked[field.Id]), name + ": same-width masks keep the length, metadata and every other value");

    // The mapped keys protect their values with their anonymizers; skipped keys and every other value stay unchanged.
    var protectedByKey = parsed.Fields.ToDictionary(f => f.Id, f => ByKey(recognizers, f, stringIds));
    byte[] keyed = codec.Serialize(parsed, protectedByKey);
    codec.Validate(parsed, keyed);
    var independentAfter = Independent(FeedPayloadReader.Read(Message(keyed, name)).Payload);
    Check(independentAfter.Count == independent.Count && independentAfter.All(field => field.Value == protectedByKey[field.Id])
        && protectedByKey.Count(pair => pair.Value != unchanged[pair.Key]) > 0, name + ": the independent reader confirms every key protection and kept value");
    Check(independent.Where(f => recognizers.Skips(JsonFeedParser.PropertyNames(f.Name))).All(f => protectedByKey[f.Id] == f.Value),
        name + ": skipped keys, including the PNR, stay unchanged");
    var reparsed = codec.Parse(Message(keyed, name));
    Check(codec.Serialize(reparsed, reparsed.Fields.ToDictionary(f => f.Id, f => f.Value)).AsSpan().SequenceEqual(keyed), name + ": the protected export parses and re-encodes exactly");

    // A changed number and a changed byte outside the strings are refused.
    var number = parsed.Fields.First(f => !stringIds.Contains(f.Id));
    Reject(() => codec.Serialize(parsed, unchanged.ToDictionary(p => p.Key, p => p.Key == number.Id ? p.Value + "0" : p.Value)), name + ": a number change");
    byte[] tamperedPayload = envelope.Payload.ToArray();
    int colon = Array.IndexOf(tamperedPayload, (byte)':');
    tamperedPayload[colon - 2] ^= 0x01;
    Reject(() => codec.Validate(parsed, envelope.Serialize(tamperedPayload)), name + ": a changed property-name byte");
    Console.WriteLine($"PASS {name,-30} {parsed.Fields.Count,3} scalars, {strings.Count,3} string candidates | baseline locations are string candidates");
}

// Lexical forms, escapes and malformed JSON, independent of the samples. Values are made up.
ParsedFeed Parse(string json) => codec.Parse(Message(Encoding.UTF8.GetBytes(json)));
var lexical = Parse(" {\"UARecloc\":\"K7QX2M\", \"Customers\":[{\"PaxId\":\"PAX-1001\",\"FirstName\":\"Em\\u0069ly\"},{\"PaxId\":\"PAX-1002\",\"FirstName\":\"Michael\"}],\"x/y~z\":\"Carter\",\"Count\":1e+02,\"Active\":true,\"Optional\":null,\"Empty\":[],\"Object\":{}} \n");
var values = lexical.Fields.ToDictionary(field => field.Id, field => field.Value);
Check(values["/x~1y~0z"] == "Carter" && lexical.Fields.Single(f => f.Id == "/Customers/1/FirstName").Name == "/Customers/*/FirstName",
    "JSON Pointer escapes keep exact instance and wildcard paths");
Check(values["/Customers/0/FirstName"] == "Emily", "an escaped string is decoded");
values["/Customers/0/FirstName"] = "[PASSENGER_NAME_WITH_LONGER_TEXT]";
byte[] changed = codec.Serialize(lexical, values);
codec.Validate(lexical, changed);
Check(Encoding.UTF8.GetString(changed).Contains("1e+02", StringComparison.Ordinal) && Encoding.UTF8.GetString(changed).Contains("\"Optional\":null", StringComparison.Ordinal),
    "a longer string keeps the numeric spelling and the null");
Reject(() => codec.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("1e+02", "100", StringComparison.Ordinal))), "an outside numeric spelling change");
Reject(() => codec.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("\"Empty\":[]", "\"Empty\":[{}]", StringComparison.Ordinal))), "an outside empty-container change");
values["/Count"] = "100";
Reject(() => codec.Serialize(lexical, values), "a non-string replacement");
values["/Count"] = "1e+02";
values.Remove("/Customers/0/FirstName");
Reject(() => codec.Serialize(lexical, values), "a missing protection field");
values["unknown"] = "extra";
Reject(() => codec.Serialize(lexical, values), "a substituted field ID with a matching count");
values.Remove("unknown");
values["/Customers/0/FirstName"] = null!;
Reject(() => codec.Serialize(lexical, values), "a null replacement, which is not a JSON null");
Reject(() => Parse("{\"x\":\"a\",\"x\":\"b\"}"), "a duplicate property");
Reject(() => Parse("{\"x\":\"a\",\"\\u0078\":\"b\"}"), "an escaped duplicate property");
Reject(() => Parse("{\"*\":\"ambiguous\"}"), "a wildcard property name");
Reject(() => Parse("{\"FirstName\":\"unfinished"), "truncated JSON");
Reject(() => Parse("[{\"x\":\"y\"}]"), "an array root, which is not the Seats object root");
Reject(() => Parse("{\"x\":\"y\"} {}"), "trailing JSON");
Reject(() => Parse("{}"), "JSON without scalars");
// 1 is the former PNR route value: a delivery for another route is refused by the Seats codec.
Reject(() => codec.Parse(new FeedMessage(new FeedDelivery((FeedType)1, Guid.Empty, "fixture.bin", "fixture"), Encoding.UTF8.GetBytes("{\"x\":\"y\"}"))), "a delivery for another route");
Reject(() => codec.Serialize(new ParsedFeed(lexical.Fields, new object()), lexical.Fields.ToDictionary(f => f.Id, f => f.Value)), "a layout from another codec");
string export = "A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX " + Convert.ToHexString(Encoding.UTF8.GetBytes("{\"FirstName\":\"Emily\"}"));
var exported = codec.Parse(Message(Encoding.UTF8.GetBytes(export), "fixture.txt"));
byte[] rebuilt = codec.Serialize(exported, exported.Fields.ToDictionary(field => field.Id, field => "*****"));
codec.Validate(exported, rebuilt);
Check(Encoding.UTF8.GetString(rebuilt).StartsWith("A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX ", StringComparison.Ordinal) && rebuilt[^1] != '\n',
    "MQ CRLF and an absent final newline are kept during protection");
Reject(() => codec.Validate(exported, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(rebuilt).Replace("A VER 2", "A VER 3", StringComparison.Ordinal))), "an MQ header change");
Reject(() => codec.Parse(Message(Encoding.UTF8.GetBytes(export.Replace("1208", "437", StringComparison.Ordinal)), "fixture.txt")), "an unsupported JSON CCSID");

Console.WriteLine($"PASS Generic JSON codec checks complete | {samples.Length} files | {compared} baseline locations | {checks} checks");

// Returns the protection a mapped key gives a string, as the protector applies it; every other value is unchanged.
static string ByKey(RecognizerFile file, FeedField field, IReadOnlySet<string> strings)
{
    if (!strings.Contains(field.Id) || field.Value.Length == 0)
    {
        return field.Value;
    }
    string? entity = file.MappedEntity(JsonFeedParser.PropertyNames(field.Name));
    return entity is null ? field.Value : FeedAnonymizer.Apply(file.ActionFor(entity).Action, entity, field.Value).Text;
}

static int CountStrings(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.Object => element.EnumerateObject().Sum(property => CountStrings(property.Value)),
    JsonValueKind.Array => element.EnumerateArray().Sum(CountStrings),
    JsonValueKind.String => 1,
    _ => 0
};

static FeedMessage Message(byte[] bytes, string name = "fixture.bin") => new(new(FeedType.Seats, Guid.Empty, name, "fixture"), bytes);

// This reader uses the independent DOM API and has no dependency on the production JSON span code.
static IReadOnlyList<FeedField> Independent(byte[] payload)
{
    using (var document = JsonDocument.Parse(payload))
    {
        var fields = new List<FeedField>();
        Walk(document.RootElement, "", "");
        return fields;

        void Walk(JsonElement element, string id, string policy)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    string name = property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    Walk(property.Value, id + "/" + name, policy + "/" + name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, id + "/" + index++, policy + "/*");
                }
            }
            else
            {
                string value = element.ValueKind == JsonValueKind.String ? element.GetString()! :
                    element.ValueKind == JsonValueKind.Null ? "" : element.GetRawText();
                fields.Add(new(id, policy, value));
            }
        }
    }
}

void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
    checks++;
}

void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        checks++;
        return;
    }
    throw new Exception("Expected rejection: " + label);
}

#endregion
