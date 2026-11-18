using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// The validated per-feed recognizer files that drive every feed workflow (WR-014), and whether Presidio's predefined
/// recognizers run beside them.
/// </summary>
/// <param name="Recognizers">The recognizer file of each feed.</param>
/// <param name="DefaultDetection">The <c>Presidio:DefaultDetection</c> setting: false (the shipped default) acts only on
/// what the recognizer files define; true also runs Presidio's predefined recognizers.</param>
public sealed record FeedPipelineConfiguration(IReadOnlyDictionary<FeedType, RecognizerFile> Recognizers, bool DefaultDetection = false)
{

    #region Methods

    /// <summary>
    /// Loads and validates one recognizer XML file per feed before any input is read (WR-014).
    /// </summary>
    /// <param name="recognizerDirectory">The directory holding <c>seats.xml</c>.</param>
    /// <param name="defaultDetection">The <c>Presidio:DefaultDetection</c> setting.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidDataException">A file is missing or invalid.</exception>
    public static FeedPipelineConfiguration Load(string recognizerDirectory, bool defaultDetection = false) =>
        new(RecognizerFileReader.ReadAll(recognizerDirectory), defaultDetection);

    /// <summary>
    /// Creates the detecting protector for these recognizer files. Call <see cref="DetectingFeedProtector.Configure"/>
    /// after <see cref="PythonHost.Start"/>.
    /// </summary>
    /// <returns>The protector.</returns>
    public DetectingFeedProtector CreateProtector() => new(Recognizers, DefaultDetection);

    /// <summary>
    /// Creates the workflow registry using the given protector.
    /// </summary>
    /// <param name="protector">The local detection and protection service.</param>
    /// <returns>One workflow per supported feed.</returns>
    public FeedWorkflowRouter CreateRouter(IFeedProtector protector) => FeedWorkflowRouter.Create(protector, FeedCodecRegistry.Create);

    #endregion
}
