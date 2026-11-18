using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// Selects the Seats route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class SeatsWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The Seats route.</value>
    public override FeedType Feed => FeedType.Seats;

    #endregion
}
