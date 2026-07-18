using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Infrastructure.VisionLlm;

/// <summary>
/// Reads book titles and authors from a bookshelf photo via the Anthropic
/// Messages API (<c>POST /v1/messages</c>). The image is normalized by
/// <see cref="ImagePreprocessor"/> - EXIF-corrected, downscaled, and re-encoded
/// as metadata-free JPEG - before it is sent.
/// </summary>
public sealed partial class ClaudeVisionBookReader : IVisionBookReader
{
    private const string MessagesPath = "v1/messages";
    private const string ImageMediaType = "image/jpeg";

    private const string PromptText =
        "You are looking at a photo of a bookshelf. Identify every book whose title " +
        "is clearly legible on its spine or cover. For each one, record its title, " +
        "its author if visible, and a confidence score from 0 to 1 for how certain " +
        "you are the title is correct. Skip books where the text is too blurry, too " +
        "small, obscured, or at too extreme an angle to read with confidence - do " +
        "not guess at titles you cannot read.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ClaudeVisionOptions _options;
    private readonly ILogger<ClaudeVisionBookReader> _logger;

    public ClaudeVisionBookReader(HttpClient httpClient, IOptions<ClaudeVisionOptions> options, ILogger<ClaudeVisionBookReader> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VisionReadResult> ReadAsync(byte[] imageData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        byte[] preprocessed = ImagePreprocessor.Preprocess(imageData, _options.MaxImageDimension);
        string base64Image = Convert.ToBase64String(preprocessed);

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = JsonContent.Create(BuildRequestBody(base64Image))
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VisionBookReaderException("Timed out waiting for the vision model.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new VisionBookReaderException("Unable to reach the vision model.", ex);
        }

        using (response)
        {
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw MapErrorResponse(response.StatusCode, responseBody);
            }

            return ParseResponse(responseBody);
        }
    }

    private JsonObject BuildRequestBody(string base64Image)
    {
        return new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = ImageMediaType,
                                ["data"] = base64Image
                            }
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = PromptText
                        }
                    }
                }
            },
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = BuildBooksSchema()
                }
            }
        };
    }

    private static JsonObject BuildBooksSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["books"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["title"] = new JsonObject { ["type"] = "string" },
                            ["author"] = new JsonObject { ["type"] = "string" },
                            ["confidence"] = new JsonObject { ["type"] = "number" }
                        },
                        ["required"] = new JsonArray { "title", "author", "confidence" },
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray { "books" },
            ["additionalProperties"] = false
        };
    }

    private VisionReadResult ParseResponse(string responseBody)
    {
        MessagesResponse message;
        try
        {
            message = JsonSerializer.Deserialize<MessagesResponse>(responseBody, JsonOptions)
                ?? throw new JsonException("Response body deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new VisionBookReaderException("The vision model returned an unexpected response.", ex);
        }

        string? booksJson = message.Content.FirstOrDefault(block => block.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(booksJson))
        {
            throw new VisionBookReaderException("The vision model did not return any readable content.");
        }

        BooksPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BooksPayload>(booksJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            payload = null;
            LogStructuredOutputParseFailed(_logger, ex);
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

        if (string.Equals(message.StopReason, "max_tokens", StringComparison.Ordinal))
        {
            LogResponseTruncated(_logger, _options.MaxTokens);
            result.Notes.Add("The vision model response was truncated at the configured token limit; some books may be missing.");
        }

        return result;
    }

    private static VisionBookReaderException MapErrorResponse(HttpStatusCode statusCode, string responseBody)
    {
        string? apiMessage = TryExtractErrorMessage(responseBody);
        string suffix = string.IsNullOrWhiteSpace(apiMessage) ? string.Empty : $" {apiMessage}";

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new VisionBookReaderException(
                $"The vision model rejected the configured API key (401).{suffix}"),
            HttpStatusCode.TooManyRequests => new VisionBookReaderException(
                $"The vision model is rate limiting requests (429).{suffix}"),
            (HttpStatusCode)529 => new VisionBookReaderException(
                $"The vision model is temporarily overloaded (529).{suffix}"),
            _ when (int)statusCode >= 500 => new VisionBookReaderException(
                $"The vision model returned a server error ({(int)statusCode}).{suffix}"),
            _ => new VisionBookReaderException(
                $"The vision model request failed ({(int)statusCode}).{suffix}")
        };
    }

    private static string? TryExtractErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement messageElement))
            {
                return messageElement.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not parse the vision model's structured output.")]
    private static partial void LogStructuredOutputParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The vision model response was truncated at max_tokens ({MaxTokens}).")]
    private static partial void LogResponseTruncated(ILogger logger, int maxTokens);
}
