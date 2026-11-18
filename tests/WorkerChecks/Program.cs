using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Messaging.Models;
using PresidioDemo.Messaging.Solace.Connection;
using PresidioDemo.Messaging.Testing;
using PresidioDemo.Worker.Hosting;
using PresidioDemo.Worker.Pipeline;

#region Execution

// Verifies environment selection, settings validation and FeedWorker delivery rules without Python or a broker.
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string runRoot = Path.Combine(project, ".test-runs", "worker-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
string settingsFile = Path.Combine(project, "src", "PresidioDemo.Worker", "appsettings.json");
Func<string, string?> noVariables = _ => null;
Func<string, string?> password = name => name == "SOLACE_PASSWORD" ? "fixture-password" : null;

// Environment selection
var (local, localName) = EnvironmentConfiguration.Build(settingsFile, noVariables);
Require(localName == "Local" && local["Solace:Host"] == "tcp://localhost:55555", "without DOTNET_ENVIRONMENT the file's CurrentEnvironment (Local) applies");
var (dev, devName) = EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? "Dev" : null);
Require(devName == "Dev" && dev["Solace:Host"]!.StartsWith("tcps://") && dev["Solace:Vpn"] == "dev-demoair-vpn", "DOTNET_ENVIRONMENT=Dev selects the Dev section");
Require(local["Messaging:Routes:0:Input"] == "q.seats.raw" && local["Messaging:Routes:1:Feed"] is null
    && dev["Messaging:Routes:0:Feed"] is null && dev["Messaging:Transport"] == "Solace",
    "Local lists its own Seats route, Dev lists none (no Dev Seats queues were supplied); shared Messaging settings still apply");
Reject(() => EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? "QA" : null),
    "an environment without a section stops startup");
RejectVariant(json => json["Solaceee"] = new JsonObject(), "an unknown top-level section is rejected");
RejectVariant(json => json["Environments"]!["Local"]!["Solcae"] = new JsonObject(), "an unknown setting inside an environment section is rejected");
RejectVariant(json => json.AsObject().Remove("CurrentEnvironment"), "a file without CurrentEnvironment needs DOTNET_ENVIRONMENT");
Environment.SetEnvironmentVariable("Solace__Host", "tcp://override.example.com:55555");
try
{
    var (overridden, _) = EnvironmentConfiguration.Build(settingsFile, noVariables);
    Require(overridden["Solace:Host"] == "tcp://override.example.com:55555", "an environment variable such as Solace__Host overrides the selected section");
}
finally { Environment.SetEnvironmentVariable("Solace__Host", null); }

// Settings validation
var messaging = MessagingOptions.Load(local);
Require(messaging.Transport == "Solace" && FeedWorkerService.Routes(messaging).Keys.SequenceEqual([FeedType.Seats]), "the Local settings route the Seats feed only");
var localSeats = FeedWorkerService.Routes(messaging)[FeedType.Seats];
Require(localSeats.Input == "q.seats.raw" && localSeats.Output == "airline/seats/protected" && localSeats.DeadLetter == "q.seats.dlq",
    "the Local Seats route keeps its queue, topic and dead-letter names");
// No Dev queue or topic names were supplied for Seats, so the Dev section has no route and stops startup until the
// deployment supplies one (deploy/.env.dev.example sets Messaging__Routes__0__*).
Reject(() => MessagingOptions.Load(dev), "Dev without a Seats route stops startup");
// Fixture names only, as Messaging__Routes__0__* variables would supply them; they are not Dev queue names.
var devRouted = new ConfigurationBuilder().AddConfiguration(dev).AddInMemoryCollection([new("Messaging:Routes:0:Feed", "Seats"),
    new("Messaging:Routes:0:Input", "fixture.seats.in"), new("Messaging:Routes:0:Output", "fixture/seats/out"),
    new("Messaging:Routes:0:DeadLetter", "fixture.seats.dlq")]).Build();
var devRoutes = FeedWorkerService.Routes(MessagingOptions.Load(devRouted));
Require(devRoutes.Keys.SequenceEqual([FeedType.Seats]) && devRoutes[FeedType.Seats].Input == "fixture.seats.in"
    && devRoutes[FeedType.Seats].Output == "fixture/seats/out" && devRoutes[FeedType.Seats].DeadLetter == "fixture.seats.dlq",
    "a Seats route supplied for Dev is used as given");
var devEmptyRoute = new ConfigurationBuilder().AddConfiguration(dev).AddInMemoryCollection([new("Messaging:Routes:0:Feed", "Seats"),
    new("Messaging:Routes:0:Input", ""), new("Messaging:Routes:0:Output", ""), new("Messaging:Routes:0:DeadLetter", "")]).Build();
Reject(() => MessagingOptions.Load(devEmptyRoute), "the empty Dev route names of deploy/.env.dev.example stop startup until they are filled in");
Require(SolaceOptions.Load(local, runRoot, password).ProvisionQueues && !SolaceOptions.Load(dev, runRoot, password).ProvisionQueues,
    "Local and Dev Solace sections validate; only Local creates queues");
var oneSeats = MessagingOptions.Load(new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Feed", "seats")]).Build());
Require(oneSeats.Routes.Count == 1 && FeedWorkerService.Routes(oneSeats).Keys.Single() == FeedType.Seats,
    "Messaging:Feed keeps only that feed's route, so one service runs one feed");
var oneDevSeats = MessagingOptions.Load(new ConfigurationBuilder().AddConfiguration(devRouted).AddInMemoryCollection([new("Messaging:Feed", "Seats")]).Build());
Require(oneDevSeats.Routes.Single().Input == "fixture.seats.in", "Messaging:Feed selects one route in Dev too");
RejectMessaging("Messaging:Feed", "IND", "Messaging:Feed without a route is rejected");
Require(MessagingOptions.Load(local).ConsumersPerTask == 1, "one consumer per feed is the default");
RejectMessaging("Messaging:ConsumersPerTask", "0", "zero consumers per task is rejected");
RejectMessaging("Messaging:ConsumersPerTask", "33", "more than 32 consumers per task is rejected");
var localSolace = SolaceOptions.Load(local, runRoot, password);
Require(localSolace.ClientName == "presidio-worker-local", "without UniqueClientName the client name is used as configured");
var devFirst = SolaceOptions.Load(dev, runRoot, password);
var devSecond = SolaceOptions.Load(dev, runRoot, password);
Require(devFirst.ClientName.StartsWith("piidemo-worker-dev-") && devFirst.ClientName != devSecond.ClientName,
    "Dev adds a per-process suffix, so scaled-out tasks never share a Solace client name");
string longName = SolaceOptions.WithInstanceSuffix(new string('w', 200), "ip-10-0-1-23.ec2.internal");
Require(longName.Length == SolaceOptions.MaxClientNameLength && longName.Contains("-ip-10-0-1-23-ec2-internal-"),
    "the suffixed client name stays within the broker limit and uses a safe host name");
RejectSolace("ClientName", new string('w', 161), "a client name longer than 160 characters is rejected");
Reject(() => SolaceOptions.Load(local, runRoot, noVariables), "a missing password environment variable stops startup");
RejectSolace("Host", "https://broker.example.com", "HTTP hosts are rejected; only native tcp:// or tcps://");
RejectSolace("ValidateCertificate", "false", "tcps:// without certificate validation is rejected", "Dev");
RejectSolace("Password", "secret", "a password in the settings is rejected as an unknown key");
RejectMessaging("Messaging:Transport", "", "a missing transport is rejected");
RejectMessaging("Messaging:MaxDeliveries", "0", "MaxDeliveries below 1 is rejected");
var sharedQueue = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:1:Feed", "Seats-2"),
    new("Messaging:Routes:1:Input", "q.seats.raw"), new("Messaging:Routes:1:Output", "airline/seats-2/protected"), new("Messaging:Routes:1:DeadLetter", "q.seats-2.dlq")]).Build();
Reject(() => MessagingOptions.Load(sharedQueue), "two routes sharing a queue are rejected");
RejectMessaging("Messaging:Routes:0:Output", "airline/>", "wildcard output topics are rejected");
RejectMessaging("Messaging:Routes:0:DeadLetter", "q.seats.raw", "a dead-letter queue equal to the input is rejected");
RejectMessaging("Messaging:Routes:0:Typo", "x", "unknown route keys are rejected");
var duplicateFeed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:1:Feed", "Seats"),
    new("Messaging:Routes:1:Input", "q.seats-2.raw"), new("Messaging:Routes:1:Output", "airline/seats-2/protected"), new("Messaging:Routes:1:DeadLetter", "q.seats-2.dlq")]).Build();
Reject(() => FeedWorkerService.Routes(MessagingOptions.Load(duplicateFeed)), "a feed with two routes is rejected");
var unknownFeed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:0:Feed", "IND")]).Build();
Reject(() => FeedWorkerService.Routes(MessagingOptions.Load(unknownFeed)), "a route for a feed without a codec is rejected");
// AC-006: a route left over from settings for a build with another feed (PNR, the binary feed this worker does not
// carry) stops startup and names the route and the feed.
var removedFeed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:0:Feed", "PNR")]).Build();
try
{
    FeedWorkerService.Routes(MessagingOptions.Load(removedFeed));
    throw new Exception("Expected rejection: a route naming a removed feed");
}
catch (InvalidOperationException ex) when (ex.Message == "Messaging:Routes:0 names feed 'PNR', which this build does not support.")
{
    Console.WriteLine("PASS a route naming a removed feed stops startup with its route index and feed name");
}
Require(PresidioOptions.Load(local, runRoot).PythonEnvironment is null, "without Presidio:PythonEnvironment the project's src/.venv applies");
string venv = Directory.CreateDirectory(Path.Combine(runRoot, "venv")).FullName;
File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = /usr/bin\nversion = 3.12.3\n");
var withVenv = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:PythonEnvironment", "venv")]).Build();
Require(PresidioOptions.Load(withVenv, runRoot).PythonEnvironment == venv, "Presidio:PythonEnvironment resolves against the application folder");
var noVenv = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:PythonEnvironment", "missing")]).Build();
Reject(() => PresidioOptions.Load(noVenv, runRoot), "a Python environment without pyvenv.cfg stops startup");
Require(PresidioOptions.Load(local, runRoot).DetectionLog && !PresidioOptions.Load(dev, runRoot).DetectionLog,
    "the detection log is on in Local and off in the base settings, so Dev (QF-26111601)");
var detectionLogOn = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:DetectionLog", "true")]).Build();
Require(PresidioOptions.Load(detectionLogOn, runRoot).DetectionLog, "Presidio:DetectionLog=true turns the detection log on");
var detectionLogTypo = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:DetectionLogs", "true")]).Build();
Reject(() => PresidioOptions.Load(detectionLogTypo, runRoot), "a misspelled detection-log key stops startup");
// WR-020: values in detection lines only with both flags on, and only in Local, Dev and QA. QF-26111601: the values flag
// is on by default, and outside those environments it is switched off instead of stopping startup.
var defaults = PresidioOptions.Load(local, runRoot);
Require(defaults.DetectionLogValues && defaults.WritesValues, "detection log values are on by default, so Local writes them (QF-26111601)");
var devDefaults = PresidioOptions.Load(dev, runRoot);
Require(devDefaults.DetectionLogValues && !devDefaults.WritesValues, "Dev has the values flag on but writes nothing while its detection log is off");
var valuesOff = PresidioOptions.Load(new ConfigurationBuilder().AddConfiguration(local)
    .AddInMemoryCollection([new("Presidio:DetectionLogValues", "false")]).Build(), runRoot);
Require(!valuesOff.ApplyValuesEnvironment("Prod") && !valuesOff.WritesValues, "with the values flag off, any environment starts unchanged");
foreach (string allowed in new[] { "Local", "Dev", "QA", "dev", localName })
{
    var envOptions = PresidioOptions.Load(local, runRoot);
    Require(!envOptions.ApplyValuesEnvironment(allowed) && envOptions.WritesValues, $"values stay on in the {allowed} environment");
}
Require(true, "values are allowed in Local, Dev and QA (any case), and when an unset DOTNET_ENVIRONMENT falls back to Local");
foreach (string refused in new[] { "Prod", "Production", "Stage" })
{
    var envOptions = PresidioOptions.Load(local, runRoot);
    Require(envOptions.ApplyValuesEnvironment(refused) && !envOptions.DetectionLogValues && !envOptions.WritesValues && envOptions.DetectionLog,
        $"in the {refused} environment the values flag is switched off and the worker still starts with a value-free log");
}
var valuesOnly = PresidioOptions.Load(new ConfigurationBuilder().AddConfiguration(dev)
    .AddInMemoryCollection([new("Presidio:DetectionLogValues", "true")]).Build(), runRoot);
Require(valuesOnly.DetectionLogValues && !valuesOnly.WritesValues, "values without the detection log write nothing (the worker warns at startup)");
foreach (string removed in new[] { "PolicyFile", "XdrLayoutFile", "ReferenceLayoutFile" })
{
    var oldKey = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:" + removed, "Configuration/x.xml")]).Build();
    Reject(() => PresidioOptions.Load(oldKey, runRoot), $"the removed Presidio:{removed} key stops startup");
}
foreach (string outside in new[] { Path.GetFullPath(Path.Combine(runRoot, "..", "mounted-recognizers")), "../recognizers", Path.GetPathRoot(runRoot)! })
{
    var mounted = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:RecognizerDirectory", outside)]).Build();
    Reject(() => PresidioOptions.Load(mounted, runRoot), "a recognizer folder outside the application folder stops startup (PR-005)");
}
Require(PresidioOptions.Load(local, runRoot).RecognizerDirectory == Path.GetFullPath("Configuration/recognizers", runRoot),
    "Presidio:RecognizerDirectory resolves against the application folder");

// FeedWorker delivery rules
var options = MessagingOptions.Load(local);
options.PublishRetryDelayMs = 20;
options.CopyHeaders = ["sourceSystem"];
var seats = options.Routes.Single(r => r.Feed == "Seats");
byte[] payload = Encoding.UTF8.GetBytes("{\"x\":\"y\"}");
var headers = new Dictionary<string, string> { ["sourceSystem"] = "res-host", ["internal"] = "drop-me" };

{
    var (broker, workflows) = Setup();
    string id = broker.Send(seats.Input, payload, headers, "corr-1");
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var output = broker.PublishedTo(seats.Output).Single();
    Require(output.Destination.Kind == DestinationKind.Topic && output.Payload.Span.SequenceEqual(payload), "validated output goes to the feed's output topic");
    Require(output.MessageId == FeedWorker.TransactionId(seats.Input, id).ToString("N") && output.CorrelationId == "corr-1",
        "output carries a transaction ID derived from the broker message ID, and the correlation ID");
    Require(output.Headers.Count == 2 && output.Headers["sourceSystem"] == "res-host" && output.Headers[MessageHeaders.Feed] == "Seats",
        "only allowed headers are copied, plus the feed name");
    Require(workflows.Names.Single().EndsWith(".bin"), "messages without a payload-format header are processed as raw payloads");
}
{
    var (broker, workflows) = Setup();
    broker.FailNextPublishes = 2;
    broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    Require(broker.PublishAttempts == 3 && broker.PublishedTo(seats.Output).Count == 1, "a broker outage is retried until the output is confirmed");
}
{
    var (broker, workflows) = Setup();
    workflows.BlockNext = true;
    string id = broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => workflows.Blocked);
    Require(broker.Pending(seats.Input) == 1 && broker.PublishedTo(seats.Output).Count == 0, "a crash during processing leaves the message unacknowledged");
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var output = broker.PublishedTo(seats.Output).Single();
    Require(output.MessageId == FeedWorker.TransactionId(seats.Input, id).ToString("N") && workflows.Deliveries.Last() == 2,
        "the redelivered message is processed again under the same transaction ID");
}
{
    var (broker, workflows) = Setup(maxDeliveries: 1);
    workflows.BlockNext = true;
    broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => workflows.Blocked);
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var rejected = broker.PublishedTo(seats.DeadLetter).Single();
    Require(rejected.Destination.Kind == DestinationKind.Queue && rejected.Headers[MessageHeaders.RejectReason].Contains("Exceeded 1 deliveries"),
        "a message delivered more than MaxDeliveries times is dead-lettered without processing");
}
{
    var (broker, workflows) = Setup();
    workflows.RejectNext = true;
    broker.Send(seats.Input, payload, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = FeedWorker.MqExportFormat });
    broker.Send(seats.Input, payload, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = "xml" });
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var dead = broker.PublishedTo(seats.DeadLetter);
    Require(workflows.Names.Single().EndsWith(".txt"), "payload-format mq-export is processed as an IBM MQ export");
    Require(dead.Count == 2 && dead[0].Payload.Span.SequenceEqual(payload) && dead[0].Headers[MessageHeaders.RejectReason] == "Fixture rejection." &&
        dead[0].Headers[MessageHeaders.PayloadFormat] == FeedWorker.MqExportFormat, "rejected input goes to the dead-letter queue with its original bytes, reason and format");
    Require(dead[1].Headers[MessageHeaders.RejectReason].Contains("Unsupported"), "an unknown payload format is dead-lettered");
    Require(broker.PublishedTo(seats.Output).Count == 0, "rejected input is never published to the output topic");
}
{
    // Two consumers on one queue, as Messaging:ConsumersPerTask=2 runs them: while one is still processing,
    // the other takes and completes the next message.
    var (broker, workflows) = Setup();
    var processor = new FeedMessageProcessor(new FeedWorkflowRouter(FeedCatalog.All.Select(f => workflows.For(f.Type))));
    using var stop = new CancellationTokenSource();
    var running = Enumerable.Range(0, 2)
        .Select(_ => new FeedWorker(FeedType.Seats, seats, options, broker, broker, processor, NullLogger.Instance).RunAsync(stop.Token)).ToArray();
    workflows.BlockNext = true;
    broker.Send(seats.Input, payload);
    await WaitFor(() => workflows.Blocked, "the first consumer to start processing");
    broker.Send(seats.Input, payload);
    await WaitFor(() => broker.PublishedTo(seats.Output).Count == 1, "the second consumer to publish");
    Require(workflows.Blocked && broker.Pending(seats.Input) == 1, "with two consumers, one message completes while the other is still processing");
    workflows.Release();
    await WaitFor(() => broker.Pending(seats.Input) == 0, "the first consumer to finish");
    stop.Cancel();
    await Task.WhenAll(running);
    Require(broker.PublishedTo(seats.Output).Count == 2, "both messages are published once the first consumer finishes");
}
Directory.Delete(runRoot, recursive: true);
Console.WriteLine("All worker checks passed.");

#endregion

#region Methods

(InMemoryMessageBroker Broker, FixtureWorkflows Workflows) Setup(int maxDeliveries = 5)
{
    options.MaxDeliveries = maxDeliveries;
    return (new InMemoryMessageBroker(), new FixtureWorkflows());
}

// Runs the Seats worker until the condition holds, then stops it as a crash or shutdown would.
async Task RunUntil(InMemoryMessageBroker broker, FixtureWorkflows workflows, Func<bool> condition)
{
    using var stop = new CancellationTokenSource();
    var worker = new FeedWorker(FeedType.Seats, seats, options, broker, broker,
        new FeedMessageProcessor(new FeedWorkflowRouter(FeedCatalog.All.Select(f => workflows.For(f.Type)))), NullLogger.Instance);
    var running = worker.RunAsync(stop.Token);
    var limit = DateTime.UtcNow.AddSeconds(20);
    while (!condition())
    {
        if (DateTime.UtcNow > limit)
        {
            throw new Exception("Worker condition timed out.");
        }
        await Task.Delay(20);
    }
    // Cancel before releasing a blocked workflow: released first, it could finish, publish and acknowledge before the
    // stop, so a simulated crash would sometimes complete the message.
    stop.Cancel();
    workflows.Release();
    await running;
}

// Polls until the condition holds, failing after 20 seconds.
static async Task WaitFor(Func<bool> condition, string label)
{
    var limit = DateTime.UtcNow.AddSeconds(20);
    while (!condition())
    {
        if (DateTime.UtcNow > limit)
        {
            throw new Exception("Timed out waiting for " + label + ".");
        }
        await Task.Delay(20);
    }
}

void RejectVariant(Action<JsonNode> change, string label)
{
    var json = JsonNode.Parse(File.ReadAllText(settingsFile))!;
    change(json);
    string path = Path.Combine(runRoot, "appsettings.json");
    File.WriteAllText(path, json.ToJsonString());
    Reject(() => EnvironmentConfiguration.Build(path, noVariables), label);
}

void RejectSolace(string key, string value, string label, string environment = "Local")
{
    var (configuration, _) = EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? environment : null);
    var changed = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection([new("Solace:" + key, value)]).Build();
    Reject(() => SolaceOptions.Load(changed, runRoot, password), label);
}

void RejectMessaging(string key, string value, string label)
{
    var changed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new(key, value)]).Build();
    Reject(() => MessagingOptions.Load(changed), label);
}

static void Reject(Action action, string label)
{
    try { action(); }
    catch (InvalidOperationException) { Console.WriteLine("PASS " + label); return; }
    throw new Exception("Expected rejection: " + label);
}

static void Require(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
    Console.WriteLine("PASS " + label);
}

#endregion

#region Nested Types

// Pass-through workflows that can block (to simulate a crash mid-processing) or reject the next message.
sealed class FixtureWorkflows
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockNext { get; set; }
    public bool Blocked { get; private set; }
    public bool RejectNext { get; set; }
    public List<int> Deliveries { get; } = [];
    public List<string> Names { get; } = [];
    public IFeedWorkflow For(FeedType feed) => new Workflow(feed, this);
    public void Release()
    {
        _release.TrySetResult();
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Blocked = false;
    }

    private sealed class Workflow(FeedType feed, FixtureWorkflows owner) : IFeedWorkflow
    {
        public FeedType Feed => feed;
        public async ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
        {
            owner.Names.Add(message.Delivery.OriginalName);
            owner.Deliveries.Add(owner.Deliveries.Count + 1);
            if (owner.BlockNext)
            {
                owner.BlockNext = false;
                owner.Blocked = true;
                await owner._release.Task.WaitAsync(cancellationToken);
            }
            if (owner.RejectNext)
            {
                owner.RejectNext = false;
                throw new InvalidDataException("Fixture rejection.");
            }
            return new(message.Content);
        }
    }
}

#endregion
