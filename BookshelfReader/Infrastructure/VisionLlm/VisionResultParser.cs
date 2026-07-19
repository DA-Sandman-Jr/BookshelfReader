using System.Text.Json;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using Microsoft.Extensions.Logging;

namespace BookshelfReader.Infrastructure.VisionLlm;

/// <summary>
/// Turns a model's raw text output into a <see cref="VisionReadResult"/>. Shared
/// by the OpenAI and Gemini readers so structured-output parsing, title
/// trimming, and the truncation note behave identically regardless of provider.
/// </summary>
internal static partial class VisionResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses the model's <paramref name="booksJson"/> text into a result. When
    /// <paramref name="wasTruncated"/> is true (the model stopped at its token
    /// limit), a note is added so callers know some books may be missing.
    /// </summary>
    public static VisionReadResult Parse(string? booksJson, bool wasTruncated, ILogger logger, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(booksJson))
        {
            throw new VisionBookReaderException("The vision model did not return any readable content.");
        }

        BooksPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BooksPayload>(ExtractJsonObject(booksJson), JsonOptions);
        }
        catch (JsonException ex)
        {
            payload = null;
            LogStructuredOutputParseFailed(logger, ex);
        }

        var result = new VisionReadResult();

        if (payload is not null)
        {
            foreach (BookEntryPayload entry in payload.Books)
            {
                if (!string.IsNullOrWhiteSpace(entry.Title))
                {
                    result.Books.Add(new VisionBookEntry(entry.Title.Trim(), entry.Author?.Trim() ?? string.Empty, entry.Confidence));
                }
            }
        }
        else
        {
            result.Notes.Add("The vision model's response could not be parsed.");
        }

        if (wasTruncated)
        {
            LogResponseTruncated(logger, maxTokens);
            result.Notes.Add("The vision model response was truncated at the configured token limit; some books may be missing.");
        }

        return result;
    }

    /// <summary>
    /// Returns the JSON object embedded in <paramref name="text"/>. Some models
    /// wrap their reply in Markdown code fences or add prose around the object
    /// even when asked not to; this trims to the outermost <c>{ ... }</c> so the
    /// payload still parses.
    /// </summary>
    private static string ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1);
        }

        return text;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not parse the vision model's structured output.")]
    private static partial void LogStructuredOutputParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The vision model response was truncated at max_tokens ({MaxTokens}).")]
    private static partial void LogResponseTruncated(ILogger logger, int maxTokens);
}
