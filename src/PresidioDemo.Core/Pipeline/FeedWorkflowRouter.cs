using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// Resolves exactly one registered workflow for every supported feed route.
/// </summary>
public sealed class FeedWorkflowRouter
{

    #region Fields and Constants

    /// <summary>
    /// Maps each supported feed route to its single registered workflow.
    /// </summary>
    private readonly IReadOnlyDictionary<FeedType, IFeedWorkflow> _workflows;

    #endregion

    #region Constructors

    /// <summary>
    /// Registers and validates exactly one processing workflow for every supported route.
    /// </summary>
    /// <param name="workflows">The feed-specific processing strategies to register.</param>
    /// <exception cref="ArgumentException">A feed has no workflow.</exception>
    public FeedWorkflowRouter(IEnumerable<IFeedWorkflow> workflows)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Feed);
        foreach (var feed in FeedCatalog.All)
        {
            if (!_workflows.ContainsKey(feed.Type))
            {
                throw new ArgumentException($"No workflow registered for {feed.FolderName}.", nameof(workflows));
            }
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Registers one protection workflow for every supported feed route.
    /// </summary>
    /// <param name="protector">The local detection and protection service.</param>
    /// <param name="codecFactory">The factory that selects the codec for each feed route.</param>
    /// <returns>A router with one registered workflow for every supported feed route.</returns>
    public static FeedWorkflowRouter Create(IFeedProtector protector, Func<FeedType, IFeedCodec> codecFactory) => new([
        new SeatsWorkflow(protector, codecFactory(FeedType.Seats))
    ]);

    /// <summary>
    /// Returns the workflow registered for the requested feed route.
    /// </summary>
    /// <param name="type">The feed route.</param>
    /// <returns>The registered processing workflow.</returns>
    public IFeedWorkflow Get(FeedType type) => _workflows[type];

    #endregion
}
