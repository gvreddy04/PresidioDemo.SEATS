using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PresidioDemo.Core.Logging;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;
using PresidioDemo.Messaging.Models;
using PresidioDemo.Messaging.Testing;
using PresidioDemo.Worker.Pipeline;

#region Execution

// Test-only batch runner. Sends every .txt (IBM MQ export) and .bin (raw payload) file under --input through the
// real FeedWorkers and embedded Presidio over the in-memory broker, then writes:
//   <output>/published/<feed>/<file>          validated protected output
//   <output>/dead-letter/<feed>/<file>        rejected original input, with <file>.reason.txt
//   <output>/logs/<feed>/<file>.log.txt       full before/after transaction log (contains original data; tests only)
//   <output>/report.json                      one item per input file
// Exit codes: 0 every file published, 2 at least one rejected, 1 configuration or usage error.
const string usage = "Usage: FeedPipelineHarness --input <folder> --output <folder> [--recognizer-directory <folder>] [--detection-log on|off|values] [--fail-publishes <n>]";
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
var settings = new Dictionary<string, string>
{
    ["--recognizer-directory"] = Path.Combine(project, "src", "PresidioDemo.Core", "Configuration", "recognizers"),
    ["--detection-log"] = "off",
    ["--fail-publishes"] = "0"
};
var supplied = new HashSet<string>();
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--input" or "--output" or "--recognizer-directory" or "--detection-log" or "--fail-publishes") || !supplied.Add(args[i]))
    {
        Console.Error.WriteLine(usage);
        return 1;
    }
    settings[args[i]] = args[i] is "--fail-publishes" or "--detection-log" ? args[i + 1] : Path.GetFullPath(args[i + 1]);
}
if (!settings.ContainsKey("--input") || !settings.ContainsKey("--output") || !int.TryParse(settings["--fail-publishes"], out int failPublishes) ||
    settings["--detection-log"] is not ("on" or "off" or "values"))
{
    Console.Error.WriteLine(usage);
    return 1;
}

FeedPipelineConfiguration pipeline;
List<(string Path, FeedType Feed, byte[] Content)> inputs = [];
try
{
    pipeline = FeedPipelineConfiguration.Load(settings["--recognizer-directory"]);
    foreach (string file in Directory.EnumerateFiles(settings["--input"], "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        if (FeedFileRouting.IsFeedFile(file))
        {
            inputs.Add((file, FeedFileRouting.FromPath(file), File.ReadAllBytes(file)));
        }
    }
    if (inputs.Count == 0)
    {
        throw new InvalidDataException("No .txt or .bin input files were found.");
    }
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
{
    Console.Error.WriteLine("Harness could not start: " + ex.Message);
    return 1;
}

var broker = new InMemoryMessageBroker { FailNextPublishes = failPublishes };
var messaging = new MessagingOptions
{
    Transport = "InMemory", MaxDeliveries = 5, PublishAckTimeoutMs = 1000, PublishRetryDelayMs = 50,
    Routes = FeedCatalog.All.Select(f => new MessageRoute { Feed = f.FolderName, Input = "in." + f.FolderName, Output = "out/" + f.FolderName, DeadLetter = "dlq." + f.FolderName }).ToList()
};
var routes = FeedWorkerService.Routes(messaging);
var outcomes = new ConcurrentDictionary<string, FeedWorkerOutcome>();
Console.WriteLine("Initializing embedded local Presidio and the bundled NLP model...");
PythonHost.Start();
try
{
    var protector = pipeline.CreateProtector();
    protector.Configure();
    var processor = new FeedMessageProcessor(pipeline.CreateRouter(protector));
    using var stop = new CancellationTokenSource();
    bool detectionLog = settings["--detection-log"] is "on" or "values";
    bool detectionLogValues = settings["--detection-log"] == "values";
    // The workers write the detection blocks themselves, exactly as in a deployment: value-free with Presidio:DetectionLog
    // on (WR-018), with values in "values" mode as with Presidio:DetectionLogValues on (WR-020).
    var workers = routes.Select(pair => new FeedWorker(pair.Key, pair.Value, messaging, broker, broker, processor, NullLogger.Instance, outcome =>
    {
        outcomes[outcome.Input.CorrelationId!] = outcome;
        return ValueTask.CompletedTask;
    }, detectionLog, detectionLogValues).RunAsync(stop.Token)).ToArray();
    for (int i = 0; i < inputs.Count; i++)
    {
        var (path, feed, content) = inputs[i];
        string format = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? FeedWorker.MqExportFormat : FeedWorker.RawFormat;
        // The correlation ID is the file name, as the sample sender sets it, so detection blocks show file=<name>.
        broker.Send(routes[feed].Input, content, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = format }, Correlation(i));
    }
    var deadline = DateTime.UtcNow.AddMinutes(10);
    while (outcomes.Count < inputs.Count && DateTime.UtcNow < deadline)
    {
        await Task.Delay(50);
    }
    stop.Cancel();
    await Task.WhenAll(workers);
}
finally
{
    PythonHost.Stop();
}

string output = settings["--output"];
var items = new List<object>();
var summary = new List<(FeedType Feed, string File, IReadOnlyList<DetectionEvent> Events)>();
for (int i = 0; i < inputs.Count; i++)
{
    var (path, feed, content) = inputs[i];
    string folder = FeedCatalog.Get(feed).FolderName;
    string name = Path.GetFileName(path);
    string sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    if (!outcomes.TryGetValue(Correlation(i), out var outcome))
    {
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Pending" });
        Console.WriteLine($"HARNESS {folder,-12} Pending   {name}");
        continue;
    }
    // The before/after file is local and git-ignored, so its block carries values (WR-009).
    string? detection = outcome.Result.DetectionEvents is { } events
        ? DetectionLog.FormatBlock(feed, outcome.Result.Message.Delivery.MessageId, events, outcome.Result.Published ? null : outcome.Result.Reason,
            outcome.Result.DetectionValues, outcome.Result.DetectionProtectedValues, outcome.Input.CorrelationId)
        : null;
    if (outcome.Result.DetectionEvents is { } reviewed)
    {
        summary.Add((feed, name, reviewed));
    }
    string log = Write(Path.Combine(output, "logs", folder, name + ".log.txt"), System.Text.Encoding.UTF8.GetBytes(FeedLoggers.Format(
        outcome.Result.Message with { Delivery = outcome.Result.Message.Delivery with { OriginalName = name } }, outcome.Result.Workflow, outcome.Result.Reason, detection)));
    if (outcome.Result.Published)
    {
        string published = Write(Path.Combine(output, "published", folder, name), outcome.Published.Payload.ToArray());
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Published", Output = published, Log = log, MessageId = outcome.Published.MessageId });
        Console.WriteLine($"HARNESS {folder,-12} Published {name}");
    }
    else
    {
        string dead = Write(Path.Combine(output, "dead-letter", folder, name), outcome.Published.Payload.ToArray());
        Write(dead + ".reason.txt", System.Text.Encoding.UTF8.GetBytes(outcome.Result.Reason!));
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Rejected", DeadLetter = dead, Reason = outcome.Result.Reason, Log = log, MessageId = outcome.Published.MessageId });
        Console.WriteLine($"HARNESS {folder,-12} Rejected  {name} | {outcome.Result.Reason}");
    }
}
string report = Write(Path.Combine(output, "report.json"), JsonSerializer.SerializeToUtf8Bytes(new { PublishAttempts = broker.PublishAttempts, Items = items }, new JsonSerializerOptions { WriteIndented = true }));
Write(Path.Combine(output, "detection-summary.txt"), System.Text.Encoding.UTF8.GetBytes(Summary(inputs.Select(input => input.Feed).ToArray(), summary)));
Console.WriteLine("Harness report: " + report);
return items.All(item => item.GetType().GetProperty("Status")!.GetValue(item) as string == "Published") ? 0 : 2;

#endregion

#region Methods

// The correlation ID of input i: its file name, prefixed with its index only when another input has the same name, so
// each outcome stays keyed uniquely.
string Correlation(int i)
{
    string name = Path.GetFileName(inputs[i].Path);
    return inputs.Count(input => Path.GetFileName(input.Path).Equals(name, StringComparison.OrdinalIgnoreCase)) > 1 ? $"{i}-{name}" : name;
}

// Lists, per feed, every review-group field across the run (WR-021), by its key path with array indexes as *, which
// keeps real property names because this file stays local (in dynamic-key JSON a property name can itself be a value). A
// path holding anything outside printable ASCII is JSON-encoded, so no name can break or forge a line. No string value is
// written: only the review reasons, character shapes, a count and the file names.
static string Summary(IReadOnlyList<FeedType> inputFeeds, IReadOnlyList<(FeedType Feed, string File, IReadOnlyList<DetectionEvent> Events)> detected)
{
    var text = new System.Text.StringBuilder();
    text.AppendLine("Detection summary: review-group fields per feed (value-free; the detection log gives a fix for each)");
    foreach (var feed in FeedCatalog.All.Select(f => f.Type).Where(inputFeeds.Contains))
    {
        text.AppendLine();
        text.AppendLine($"{FeedCatalog.Get(feed).FolderName}  review fields ({inputFeeds.Count(f => f == feed)} files)");
        var rows = detected.Where(d => d.Feed == feed)
            .SelectMany(d => d.Events.Where(e => DetectionLog.Group(e) == DetectionGroup.Review).Select(e => (d.File, Event: e)))
            .GroupBy(r => (Field: Printable(r.Event.Pattern), Reason: DetectionLog.Reason(r.Event.Reasons)))
            .OrderBy(g => g.Key.Field, StringComparer.Ordinal).ThenBy(g => g.Key.Reason, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0)
        {
            text.AppendLine("  (none)");
            continue;
        }
        int width = Math.Min(rows.Max(g => g.Key.Field.Length), 48);
        foreach (var row in rows)
        {
            string shapes = string.Join(", ", row.Select(r => r.Event.Shape).Distinct(StringComparer.Ordinal).Take(3));
            string files = string.Join(", ", row.Select(r => r.File).Distinct(StringComparer.Ordinal));
            text.AppendLine($"  {row.Key.Field.PadRight(width)}  {row.Key.Reason,-22}  seen={row.Count()}  shapes={shapes}  files={files}");
        }
    }
    return text.ToString();
}

// Returns a path as it is when it is printable ASCII and does not start with a quote; otherwise JSON-encoded (WR-021).
static string Printable(string path) =>
    path.Length > 0 && path[0] != '"' && path.All(c => c is >= ' ' and <= '~') ? path : DetectionLog.Encode(path);

static string Write(string path, byte[] content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content);
    return path;
}

#endregion
