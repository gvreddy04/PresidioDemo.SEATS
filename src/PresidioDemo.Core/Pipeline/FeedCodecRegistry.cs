using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// Selects the decoded-text codec for a delivery route: the shared System.Text.Json codec for the JSON feeds (no per-feed
/// schema; every string value is a candidate).
/// </summary>
public static class FeedCodecRegistry
{

    #region Methods

    /// <summary>
    /// Returns the codec for a feed route.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <returns>The codec.</returns>
    public static IFeedCodec Create(FeedType feed) => feed switch
    {
        FeedType.Seats => new SeatsParser(),
        _ => throw new InvalidDataException("No codec is registered for this feed.")
    };

    #endregion
}
