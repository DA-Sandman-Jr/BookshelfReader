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
/// Reads book titles and authors from a bookshelf photo via the OpenAI Chat
/// Completions API (<c>POST /v1/chat/completions</c>) using a vision-capable
/// chat model. The image is normalized by <see cref="ImagePreprocessor"/> -
/// EXIF-corrected, downscaled, and re-encoded as metadata-free JPEG - and sent
/// as a base64 data URL. JSON mode (<c>response_format: json_object</c>) is
/// requested so the model returns the structured books payload.
/// </summary>
public sealed class OpenAIVisionBookReader : IVisionBookReader
{
    private const string ChatCompletionsPath = "v1/chat/completions";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OpenAIVisionOptions _options;
    private readonly ILogger<OpenAIVisionBookReader> _logger;

    public OpenAIVisionBookReader(HttpClient httpClient, IOptions<OpenAIVisionOptions> options, ILogger<OpenAIVisionBookReader> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VisionReadResult> ReadAsync(byte[] imageData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        byte[] preprocessed = ImagePreprocessor.Preprocess(imageData, _options.MaxImageDimension);
        string dataUrl = $"data:{VisionPrompt.ImageMediaType};base64,{Convert.ToBase64String(preprocessed)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = JsonContent.Create(BuildRequestBody(dataUrl))
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
                throw VisionErrorMapper.Map(response.StatusCode, responseBody);
            }

            return ParseResponse(responseBody);
        }
    }

    private JsonObject BuildRequestBody(string dataUrl)
    {
        return new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = VisionPrompt.PromptText
                        },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = dataUrl
                            }
                        }
                    }
                }
            }
        };
    }

    private VisionReadResult ParseResponse(string responseBody)
    {
        OpenAIChatResponse chat;
        try
        {
            chat = JsonSerializer.Deserialize<OpenAIChatResponse>(responseBody, JsonOptions)
                ?? throw new JsonException("Response body deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new VisionBookReaderException("The vision model returned an unexpected response.", ex);
        }

        OpenAIChoice? choice = chat.Choices.FirstOrDefault();
        string? booksJson = choice?.Message?.Content;
        bool wasTruncated = string.Equals(choice?.FinishReason, "length", StringComparison.Ordinal);

        return VisionResultParser.Parse(booksJson, wasTruncated, _logger, _options.MaxTokens);
    }
}
