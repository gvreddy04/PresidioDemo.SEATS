using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Logging;

/// <summary>
/// Formats transaction logs for the Seats feed.
/// </summary>
public sealed class SeatsFeedLogger : FeedLogger
{

    #region Properties, Indexers

    /// <inheritdoc />
    public override FeedType Feed => FeedType.Seats;

    #endregion
}
