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
/// Reads book titles and authors from a bookshelf photo via the Google Gemini
/// Generative Language API (<c>POST v1beta/models/{model}:generateContent</c>)
/// using a multimodal model. The image is normalized by
/// <see cref="ImagePreprocessor"/> - EXIF-corrected, downscaled, and re-encoded
/// as metadata-free JPEG - and sent as base64 <c>inline_data</c>. JSON output
/// is requested via <c>generationConfig.responseMimeType</c>.
/// </summary>
public sealed class GeminiVisionBookReader : IVisionBookReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly GeminiVisionOptions _options;
    private readonly ILogger<GeminiVisionBookReader> _logger;

    public GeminiVisionBookReader(HttpClient httpClient, IOptions<GeminiVisionOptions> options, ILogger<GeminiVisionBookReader> logger)
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

        string requestPath = $"v1beta/models/{_options.Model}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestPath)
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
                throw VisionErrorMapper.Map(response.StatusCode, responseBody);
            }

            return ParseResponse(responseBody);
        }
    }

    private JsonObject BuildRequestBody(string base64Image)
    {
        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = VisionPrompt.PromptText
                        },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = VisionPrompt.ImageMediaType,
                                ["data"] = base64Image
                            }
                        }
                    }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = _options.MaxTokens,
                ["responseMimeType"] = "application/json"
            }
        };
    }

    private VisionReadResult ParseResponse(string responseBody)
    {
        GeminiGenerateContentResponse generated;
        try
        {
            generated = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(responseBody, JsonOptions)
                ?? throw new JsonException("Response body deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new VisionBookReaderException("The vision model returned an unexpected response.", ex);
        }

        GeminiCandidate? candidate = generated.Candidates.FirstOrDefault();
        string? booksJson = candidate?.Content?.Parts.FirstOrDefault(part => !string.IsNullOrWhiteSpace(part.Text))?.Text;
        bool wasTruncated = string.Equals(candidate?.FinishReason, "MAX_TOKENS", StringComparison.Ordinal);

        return VisionResultParser.Parse(booksJson, wasTruncated, _logger, _options.MaxTokens);
    }
}
