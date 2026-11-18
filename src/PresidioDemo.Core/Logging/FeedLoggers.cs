using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Logging;

/// <summary>Resolves the feed-specific transaction logger.</summary>
public static class FeedLoggers
{
    private static readonly IReadOnlyDictionary<FeedType, FeedLogger> Loggers = new FeedLogger[]
    {
        new SeatsFeedLogger()
    }.ToDictionary(logger => logger.Feed);

    /// <summary>Formats a transaction using the logger for the supplied feed.</summary>
    public static string Format(FeedMessage message, FeedWorkflowResult? result, string? reason, string? detection = null)
    {
        if (!Loggers.TryGetValue(message.Delivery.Feed, out var logger))
        {
            throw new InvalidDataException("No transaction logger is configured for this feed.");
        }
        return logger.Format(message, result, reason, detection);
    }
}
