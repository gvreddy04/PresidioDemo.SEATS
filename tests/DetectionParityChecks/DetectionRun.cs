using System.Text.Json;
using PresidioDemo.Core.Logging;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

#region Models

/// <summary>
/// The decoded-text result for one sample.
/// </summary>
/// <param name="Path">The sample path.</param>
/// <param name="Feed">The feed route.</param>
/// <param name="Original">The original message bytes.</param>
/// <param name="Output">The protected and validated output, or null when rejected.</param>
/// <param name="Reason">The value-free reject reason, or null.</param>
/// <param name="Parsed">The decoded-text parse, or null when the input could not be decoded.</param>
/// <param name="Outcome">The protector outcome, or null when rejected before detection finished.</param>
public sealed record SampleResult(string Path, FeedType Feed, byte[] Original, byte[]? Output, string? Reason, ParsedFeed? Parsed, DetectionOutcome? Outcome);

#endregion

/// <summary>
/// Runs samples through the decoded-text path: JSON parser, recognizer files, detecting protector, rebuild, validate.
/// </summary>
public static class DetectionRun
{

    #region Methods

    /// <summary>
    /// Returns the decoded-text codec of a feed.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <returns>The shared System.Text.Json parser.</returns>
    public static IFeedCodec Codec(FeedType feed) => FeedCodecRegistry.Create(feed);

    /// <summary>
    /// Returns the shipped <c>Presidio:DefaultDetection</c> value from the worker's base settings, so the checks run the way
    /// a deployment without an override does.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The base setting; false when the file does not set it, as the options class defaults it.</returns>
    public static bool ShippedDefaultDetection(string root)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "PresidioDemo.Worker", "appsettings.json")));
        return settings.RootElement.GetProperty("Presidio").TryGetProperty("DefaultDetection", out var value) && value.GetBoolean();
    }

    /// <summary>
    /// Loads the recognizer sets and configures the Python bridge with the shipped <c>Presidio:DefaultDetection</c> value.
    /// Python must be started.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The configured protector.</returns>
    public static DetectingFeedProtector CreateProtector(string root)
    {
        var protector = new DetectingFeedProtector(RecognizerFileReader.ReadAll(Path.Combine(root, "src", "PresidioDemo.Core", "Configuration", "recognizers")),
            ShippedDefaultDetection(root));
        protector.Configure();
        return protector;
    }

    /// <summary>
    /// Processes one sample; structural failures become a reject reason, as in the worker.
    /// </summary>
    /// <param name="protector">The configured protector.</param>
    /// <param name="path">The sample path.</param>
    /// <param name="cancellationToken">The token that stops processing.</param>
    /// <returns>The result.</returns>
    public static async Task<SampleResult> ProcessAsync(DetectingFeedProtector protector, string path, CancellationToken cancellationToken)
    {
        var feed = FeedFileRouting.FromPath(path);
        byte[] original = File.ReadAllBytes(path);
        var message = new FeedMessage(new FeedDelivery(feed, Guid.NewGuid(), System.IO.Path.GetFileName(path), path), original);
        var codec = Codec(feed);
        ParsedFeed? parsed = null;
        DetectionOutcome? outcome = null;
        try
        {
            parsed = codec.Parse(message);
            outcome = await protector.ProtectAsync(feed, parsed, cancellationToken);
            byte[] output = codec.Serialize(parsed, outcome.Values);
            codec.Validate(parsed, output);
            return new SampleResult(path, feed, original, output, null, parsed, outcome);
        }
        catch (InvalidDataException ex)
        {
            return new SampleResult(path, feed, original, null, ex.Message, parsed, outcome);
        }
    }

    /// <summary>
    /// Writes the detection log for every sample: one value-free block per sample when on, nothing when off (V-05).
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="enabled">Whether the detection log is on, as <c>Presidio:DetectionLog</c> would set it.</param>
    /// <returns>A task that completes when every sample was processed.</returns>
    public static async Task WriteLogAsync(string root, bool enabled)
    {
        var protector = CreateProtector(root);
        foreach (string path in SampleFiles.All(root))
        {
            var result = await ProcessAsync(protector, path, CancellationToken.None);
            string name = System.IO.Path.GetFileName(path);
            Console.WriteLine($"SAMPLE {name} candidates={result.Outcome?.Events.Count ?? 0} status={(result.Output is null ? "rejected" : "published")}");
            if (enabled && result.Outcome is not null)
            {
                Console.Write(DetectionLog.FormatBlock(result.Feed, Guid.Empty, result.Outcome.Events, result.Output is null ? result.Reason : null));
            }
        }
    }

    #endregion
}
