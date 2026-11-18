using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Logging;

/// <summary>
/// Reports complete input, decoded payload, plain text and the detection block, followed by output payload and complete feed data.
/// </summary>
public static class FeedTransactionLog
{

    #region Fields and Constants

    /// <summary>
    /// The separator used between transaction-log sections.
    /// </summary>
    private const string Divider = "========================================================================";

    #endregion

    #region Methods

    /// <summary>
    /// Creates the readable local diagnostic representation of the supplied message or document.
    /// </summary>
    /// <param name="original">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="result">The validated protected output and its optional diagnostic field sets.</param>
    /// <param name="reason">The validated explanation for retaining or rejecting the complete input.</param>
    /// <param name="detection">The detection log block for this message, or null when detection did not run.</param>
    /// <returns>The complete readable diagnostic report retaining supplied source values.</returns>
    public static string Format(FeedMessage original, FeedWorkflowResult? result, string? reason, string? detection = null)
    {
        var report = new StringBuilder();
        report.AppendLine($"Transaction: {original.Delivery.MessageId:N}");
        report.AppendLine($"Feed: {FeedCatalog.Get(original.Delivery.Feed).FolderName}");
        report.AppendLine($"Source file: {JsonSerializer.Serialize(original.Delivery.OriginalName)}");
        report.AppendLine();
        AppendHeading(report, "BEFORE ANONYMIZATION");
        AppendMessage(report, original, completeFeedFirst: true);
        report.AppendLine();
        AppendDetection(report, detection);
        report.AppendLine();
        AppendHeading(report, "AFTER ANONYMIZATION");
        if (result is null)
        {
            report.AppendLine("No validated anonymized output was produced.");
            report.AppendLine($"Reason: {reason ?? "Processing failed."}");
        }
        else
        {
            AppendMessage(report, original with { Content = result.Content }, completeFeedFirst: false);
        }
        return report.ToString();
    }

    /// <summary>
    /// Appends the complete encoded feed data to the transaction report.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    private static void AppendCompleteFeed(StringBuilder report, FeedMessage message)
    {
        report.AppendLine($"Complete feed data ({message.Content.Length} bytes):");
        AppendData(report, message.Content.Span);
    }

    /// <summary>
    /// Appends bytes in the report&apos;s readable text or hexadecimal representation.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    private static void AppendData(StringBuilder report, ReadOnlySpan<byte> bytes)
    {
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            {
                report.AppendLine("UTF-8 text:");
                report.AppendLine(text);
                return;
            }
        }
        catch (DecoderFallbackException)
        {
            // Binary content uses lossless hexadecimal with a readable ASCII column.
        }
        report.AppendLine("Hex dump (offset | bytes | ASCII):");
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            var row = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            report.Append(offset.ToString("X8")).Append("  ");
            for (int index = 0; index < 16; index++)
            {
                report.Append(index < row.Length ? row[index].ToString("X2") + " " : "   ");
            }
            report.Append(" | ");
            foreach (byte value in row)
            {
                report.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
            report.AppendLine();
        }
    }

    /// <summary>
    /// Appends the detection log block, placed after the payload so its JSON Pointers can be followed there.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="detection">The block from <see cref="DetectionLog.FormatBlock"/>, or null when detection did not run.</param>
    private static void AppendDetection(StringBuilder report, string? detection)
    {
        if (detection is null)
        {
            return;
        }
        report.AppendLine("Detection (each JSON Pointer locates a string in the payload shown above):");
        report.Append(detection);
        report.AppendLine();
    }

    /// <summary>
    /// Appends a section heading and its diagnostic divider lines.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="heading">The diagnostic section heading to append.</param>
    private static void AppendHeading(StringBuilder report, string heading)
    {
        report.AppendLine(Divider);
        report.AppendLine(heading);
        report.AppendLine(Divider);
        report.AppendLine();
    }

    /// <summary>
    /// Appends complete transport data, decoded payload and available readable views.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="completeFeedFirst">Whether complete encoded input precedes the decoded payload in the report.</param>
    private static void AppendMessage(StringBuilder report, FeedMessage message, bool completeFeedFirst)
    {
        if (completeFeedFirst)
        {
            AppendCompleteFeed(report, message);
        }

        ReadOnlyMemory<byte> plainTextPayload = message.Content;
        bool decoded = false;
        if (Path.GetExtension(message.Delivery.OriginalName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var envelope = FeedPayloadReader.Read(message);
                if (envelope.Format != "Raw")
                {
                    if (completeFeedFirst)
                    {
                        report.AppendLine();
                    }

                    report.AppendLine($"Decoded MQ payload ({envelope.Payload.Length} bytes):");
                    AppendData(report, envelope.Payload);
                    plainTextPayload = envelope.Payload;
                    decoded = true;
                }
            }
            catch (InvalidDataException)
            {
                // Malformed envelopes still retain their complete bytes.
            }
        }
        if (completeFeedFirst)
        {
            report.AppendLine();
            AppendPlainText(report, plainTextPayload.Span);
        }
        else
        {
            if (decoded)
            {
                report.AppendLine();
            }

            AppendCompleteFeed(report, message);
        }
    }

    /// <summary>
    /// Appends readable UTF-8 payload text while replacing binary controls with spaces.
    /// </summary>
    /// <param name="report">The transaction-report buffer receiving diagnostic content.</param>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    private static void AppendPlainText(StringBuilder report, ReadOnlySpan<byte> bytes)
    {
        report.AppendLine("Plain text:");
        // Keep readable UTF-8 together, including text crossing hex-dump rows.
        // Binary controls and invalid UTF-8 become spaces; the dumps retain exact bytes.
        foreach (char character in Encoding.UTF8.GetString(bytes))
        {
            report.Append(character == '\uFFFD' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                ? ' ' : character);
        }
        report.AppendLine();
    }

    #endregion
}
