using System.Text.Json;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Decoder;

/// <summary>
/// Parses, rebuilds and validates Seats messages (a JSON object root); the shared logic is in <see cref="JsonFeedParser"/>.
/// </summary>
public sealed class SeatsParser() : JsonFeedParser(FeedType.Seats, JsonTokenType.StartObject)
{
}
