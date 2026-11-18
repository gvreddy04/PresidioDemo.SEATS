using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;
using PresidioDemo.Messaging.Abstractions;
using PresidioDemo.Messaging.Models;
using PresidioDemo.Worker.Hosting;

namespace PresidioDemo.Worker.Pipeline;

/// <summary>
/// Connects the broker, starts embedded Presidio and runs one feed worker per route until the host stops.
/// </summary>
/// <param name="connection">The selected broker adapter's connection.</param>
/// <param name="consumer">The selected broker adapter's consumer.</param>
/// <param name="publisher">The selected broker adapter's publisher.</param>
/// <param name="messaging">The validated broker-neutral settings.</param>
/// <param name="pipeline">The validated protection configuration.</param>
/// <param name="presidio">The protection settings, including the Python virtual environment.</param>
/// <param name="loggers">Creates one logger per feed worker.</param>
/// <param name="lifetime">Stops the host when the worker cannot continue.</param>
public sealed class FeedWorkerService(IMessagingConnection connection, IMessageConsumer consumer, IMessagePublisher publisher,
    MessagingOptions messaging, FeedPipelineConfiguration pipeline, PresidioOptions presidio, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
{

    #region Properties, Indexers

    /// <summary>
    /// Gets the reason the worker stopped on its own, or null after a normal shutdown.
    /// </summary>
    /// <value>The failure message.</value>
    public string? Failure { get; private set; }

    #endregion

    #region Methods

    /// <summary>
    /// Maps each configured route to its feed. An environment routes only the feeds its broker carries;
    /// feeds without a route are not consumed.
    /// </summary>
    /// <param name="messaging">The validated broker-neutral settings.</param>
    /// <returns>The route for each routed feed.</returns>
    /// <exception cref="InvalidOperationException">A route names a feed this build does not support, such as one left over
    /// from settings written for another build, or a feed has more than one route.</exception>
    public static IReadOnlyDictionary<FeedType, MessageRoute> Routes(MessagingOptions messaging)
    {
        var routes = new Dictionary<FeedType, MessageRoute>();
        for (int index = 0; index < messaging.Routes.Count; index++)
        {
            var route = messaging.Routes[index];
            var feed = FeedCatalog.All.SingleOrDefault(f => f.FolderName.Equals(route.Feed, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Messaging:Routes:{index} names feed '{route.Feed}', which this build does not support.");
            if (!routes.TryAdd(feed.Type, route))
            {
                throw new InvalidOperationException($"Feed {route.Feed} has more than one route.");
            }
        }
        return routes;
    }

    /// <summary>
    /// Runs every feed worker; a worker failure stops the host.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host stops, for example on SIGTERM from ECS.</param>
    /// <returns>A task that completes once every worker has stopped and Presidio has shut down.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var log = loggers.CreateLogger<FeedWorkerService>();
        bool pythonStarted = false;
        try
        {
            await connection.ConnectAsync(stoppingToken);
            log.LogInformation("Initializing embedded local Presidio and the bundled NLP model...");
            PythonHost.Start(presidio.PythonEnvironment);
            pythonStarted = true;
            var protector = pipeline.CreateProtector();
            protector.Configure();
            var processor = new FeedMessageProcessor(pipeline.CreateRouter(protector));
            // ConsumersPerTask workers per feed, each on its own broker flow: .NET parsing and broker round trips overlap
            // across consumers and feeds; shared Python calls are serialized.
            var routes = Routes(messaging);
            var unrouted = FeedCatalog.All.Where(f => !routes.ContainsKey(f.Type)).Select(f => f.FolderName).ToArray();
            if (unrouted.Length > 0)
            {
                log.LogInformation("No route for {Feeds}; these feeds are not consumed.", string.Join(", ", unrouted));
            }
            var workers = routes.SelectMany(pair => Enumerable.Range(0, messaging.ConsumersPerTask).Select(_ =>
                new FeedWorker(pair.Key, pair.Value, messaging, consumer, publisher, processor,
                    loggers.CreateLogger("PresidioDemo.Worker." + FeedCatalog.Get(pair.Key).FolderName), detectionLog: presidio.DetectionLog,
                    detectionLogValues: presidio.WritesValues).RunAsync(stoppingToken))).ToArray();
            // Value-free, so every run's log shows which recognizers acted.
            log.LogInformation(pipeline.DefaultDetection
                ? "Default detection on: the recognizer files' definitions act, plus Presidio's predefined recognizers and spaCy NER."
                : "Default detection off: only the recognizer files' custom recognizers and mapped keys act; Presidio's predefined recognizers and spaCy NER do not run.");
            if (presidio.WritesValues)
            {
                log.LogWarning("Detection log on with values: each line also carries the actual decoded value (WR-020). Non-production only.");
            }
            else if (presidio.DetectionLog)
            {
                log.LogInformation("Detection log on: one value-free line per decoded string (WR-018).");
            }
            else if (presidio.DetectionLogValues)
            {
                log.LogWarning("Presidio:DetectionLogValues has no effect while Presidio:DetectionLog is off; no detection lines are written.");
            }
            if (messaging.ConsumersPerTask > 1)
            {
                log.LogInformation("{Consumers} consumers per feed.", messaging.ConsumersPerTask);
            }
            log.LogInformation("READY: {Count} feed workers on {Transport}.", routes.Count, connection.Transport);
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Unacknowledged messages stay on the broker; the orchestrator restarts the task.
            Failure = ex.Message;
            log.LogCritical("Worker stopped: {Reason}", ex.Message);
            lifetime.StopApplication();
        }
        finally
        {
            if (pythonStarted)
            {
                PythonHost.Stop();
            }
        }
    }

    #endregion
}
