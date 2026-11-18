using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PresidioDemo.Core.Logging;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Messaging.Abstractions;
using PresidioDemo.Messaging.Models;
using PresidioDemo.Worker.Hosting;
using PresidioDemo.Worker.Pipeline;

#region Execution

// Subscribes to the protected output topic of every route in the environment selected in the worker's appsettings.json
// (DOTNET_ENVIRONMENT, else CurrentEnvironment), like any other subscriber: nothing is removed from a queue, and only messages
// published while it runs are received. Each message is decoded with its feed's codec and written to a text file with every
// decoded value, its JSON Pointer and published value (QF-26111602). The file is local diagnostics under the
// git-ignored .test-runs folder; it holds the published (protected) values, never the worker's input.
const string usage = "Usage: dotnet run --project tools/FeedOutputReader -- [--topic <topic>] [--output <file>] [--count <n>] [--settings <appsettings.json>]";
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string settingsFile = Path.Combine(project, "src", "PresidioDemo.Worker", "appsettings.json");
string output = Path.Combine(project, ".test-runs", "output-reader", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
string? topicOverride = null;
int limit = 0;
for (int i = 0; i < args.Length; i += 2)
{
    switch (i + 1 < args.Length ? args[i] : "")
    {
        case "--settings": settingsFile = Path.GetFullPath(args[i + 1]); break;
        case "--output": output = Path.GetFullPath(args[i + 1]); break;
        case "--topic": topicOverride = args[i + 1]; break;
        case "--count" when int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0: limit = n; break;
        default: Console.Error.WriteLine(usage); return 1;
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
try
{
    var (settings, environment) = EnvironmentConfiguration.Build(settingsFile);
    // The broker allows one connection per client name, so the reader must not reuse the running worker's name.
    IConfiguration configuration = settings["Solace:ClientName"] is { Length: > 0 } clientName
        ? new ConfigurationBuilder().AddConfiguration(settings)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Solace:ClientName"] = clientName + "-reader" }).Build()
        : settings;
    var messaging = MessagingOptions.Load(configuration);
    var routes = FeedWorkerService.Routes(messaging);
    // Each subscription decodes with its route's feed; --topic keeps the feed of the single route it replaces.
    var subscriptions = topicOverride is null ? routes.Select(r => (Feed: r.Key, Topic: r.Value.Output)).ToArray()
        : routes.Count == 1 ? [(routes.Keys.Single(), topicOverride)]
        : throw new InvalidOperationException("--topic needs exactly one route in the selected environment.");

    var services = new ServiceCollection().AddLogging(log => log.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
    services.AddConfiguredMessaging(configuration, messaging, Path.GetDirectoryName(settingsFile)!);
    await using var provider = services.BuildServiceProvider();
    var connection = provider.GetRequiredService<IMessagingConnection>();
    var subscriber = provider.GetService<IMessageSubscriber>()
        ?? throw new InvalidOperationException($"The {messaging.Transport} transport has no topic subscriber.");
    await connection.ConnectAsync(stop.Token);

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await using var writer = new StreamWriter(output, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    var gate = new Lock();
    int received = 0;
    writer.WriteLine($"FEED OUTPUT READER | environment {environment} | started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    foreach (var (feed, topic) in subscriptions)
    {
        writer.WriteLine($"subscribed  {FeedCatalog.Get(feed).FolderName,-6} topic:{topic}");
    }
    writer.WriteLine();

    var readers = subscriptions.Select(subscription => Task.Run(async () =>
    {
        await foreach (var message in subscriber.SubscribeAsync(subscription.Topic, stop.Token))
        {
            lock (gate)
            {
                if (limit > 0 && received >= limit)
                {
                    break;
                }
                received++;
                var (block, summary) = Describe(received, subscription.Feed, message);
                writer.Write(block);
                Console.WriteLine(summary);
                if (limit > 0 && received >= limit)
                {
                    stop.Cancel();
                }
            }
        }
    })).ToArray();
    Console.WriteLine($"Subscribed to {string.Join(", ", subscriptions.Select(s => "topic:" + s.Topic))} ({environment}). " +
        (limit > 0 ? $"Stops after {limit} messages or Ctrl+C." : "Ctrl+C stops."));
    Console.WriteLine($"Log: {Path.GetRelativePath(project, output)}");
    try { await Task.WhenAll(readers); }
    catch (OperationCanceledException) { /* Ctrl+C or --count reached. */ }
    writer.WriteLine($"stopped {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {received} messages");
    Console.WriteLine($"Stopped after {received} messages.");
    await connection.DisposeAsync();
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Reader failed: " + ex.Message);
    return 1;
}

#endregion

#region Methods

// Returns the log block for one message (a header, then one line per decoded string) and its one-line console summary.
static (string Block, string Summary) Describe(int number, FeedType feed, InboundMessage message)
{
    string file = message.CorrelationId is { Length: > 0 } correlation ? correlation : "-";
    string folder = FeedCatalog.Get(feed).FolderName;
    var block = new StringBuilder();
    block.AppendLine(CultureInfo.InvariantCulture, $"=== MESSAGE {number} | {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} | topic:{message.Source}");
    block.AppendLine(CultureInfo.InvariantCulture, $"  message ID: {message.StableId}");
    block.AppendLine(CultureInfo.InvariantCulture, $"  file:       {DetectionLog.Encode(file)}");
    block.AppendLine(CultureInfo.InvariantCulture, $"  headers:    {(message.Headers.Count == 0 ? "-" : string.Join(", ", message.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key + "=" + DetectionLog.Encode(h.Value))))}");
    string format = IsMqExport(message.Payload.Span) ? FeedWorker.MqExportFormat : FeedWorker.RawFormat;
    try
    {
        // The worker publishes the input's transport format unchanged (WR-004) without a format header, so it is read from the bytes.
        var delivery = new FeedDelivery(feed, Guid.Empty, "output" + (format == FeedWorker.MqExportFormat ? ".txt" : ".bin"), "");
        var fields = FeedCodecRegistry.Create(feed).Parse(new FeedMessage(delivery, message.Payload)).Fields;
        string[] locations = fields.Select(Location).ToArray();
        int width = locations.Select(l => l.Length).DefaultIfEmpty(0).Max();
        string offsets = fields.Any(f => f.Offset is not null) ? " | offsets=payload" : "";
        block.AppendLine(CultureInfo.InvariantCulture, $"  feed {folder} | format {format} | {message.Payload.Length} bytes | fields={fields.Count}{offsets}");
        block.AppendLine("  Decoded fields (published values)");
        if (fields.Count == 0)
        {
            block.AppendLine("    (none)");
        }
        for (int i = 0; i < fields.Count; i++)
        {
            block.Append("    ").Append(locations[i].PadRight(width)).Append("  ").AppendLine(DetectionLog.Encode(fields[i].Value));
        }
        block.AppendLine();
        return (block.ToString(), $"RECV {folder,-6} {file,-24} {message.Payload.Length,7} bytes  fields={fields.Count}");
    }
    catch (InvalidDataException ex)
    {
        block.AppendLine(CultureInfo.InvariantCulture, $"  feed {folder} | format {format} | {message.Payload.Length} bytes | not decoded: {DetectionLog.Encode(ex.Message)}");
        block.AppendLine();
        return (block.ToString(), $"RECV {folder,-6} {file,-24} {message.Payload.Length,7} bytes  NOT DECODED: {ex.Message}");
    }
}

// Returns where a decoded value sits, as the detection log writes it with values: its JSON Pointer, JSON-encoded when it is
// not printable ASCII or starts with a quote, so a property name cannot break the line; or slot 12 @0x01A4 for a codec
// that reports payload offsets.
static string Location(FeedField field)
{
    if (field.Offset is int offset)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{field.Id} @0x{offset:X4}");
    }
    return field.Id.Length > 0 && field.Id[0] != '"' && field.Id.All(c => c is >= ' ' and <= '~') ? field.Id : DetectionLog.Encode(field.Id);
}

// Returns whether a payload is an IBM MQ export or diagnostic dump (UTF-8 text the MQ reader accepts) rather than raw bytes.
static bool IsMqExport(ReadOnlySpan<byte> payload)
{
    try
    {
        string text = new UTF8Encoding(false, true).GetString(payload);
        return text.StartsWith("A VER ", StringComparison.Ordinal) || text.Contains("AMQSBCG0", StringComparison.Ordinal);
    }
    catch (DecoderFallbackException)
    {
        return false;
    }
}

#endregion
