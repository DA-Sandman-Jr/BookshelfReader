using System.Net;
using System.Text;
using System.Text.Json;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.VisionLlm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using RichardSzalay.MockHttp;
using Xunit;

namespace BookshelfReader.Tests.VisionLlm;

public class GeminiVisionBookReaderTests
{
    private const string Endpoint =
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent";

    private static readonly byte[] TestImageBytes = CreateJpegBytes();

    [Fact]
    public async Task ReadAsync_OnSuccess_ParsesBooksFromCandidateText()
    {
        const string responseJson = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.95},{\"title\":\"Foundation\",\"author\":\"\",\"confidence\":0.6}]}" }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        GeminiVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        result.Books.Should().HaveCount(2);
        result.Books[0].Should().Be(new VisionBookEntry("Dune", "Frank Herbert", 0.95));
        result.Books[1].Should().Be(new VisionBookEntry("Foundation", string.Empty, 0.6));
        result.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_SendsExpectedRequestShape()
    {
        const string responseJson = """{"candidates":[{"content":{"parts":[{"text":"{\"books\":[]}"}]},"finishReason":"STOP"}]}""";

        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;
        string? apiKeyHeader = null;

        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync();
                apiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        GeminiVisionBookReader reader = CreateReader(mockHttp, options => options.MaxTokens = 1234);

        await reader.ReadAsync(TestImageBytes);

        apiKeyHeader.Should().Be("test-api-key");

        capturedBody.Should().NotBeNull();
        using var document = JsonDocument.Parse(capturedBody!);
        JsonElement root = document.RootElement;

        JsonElement part0 = root.GetProperty("contents")[0].GetProperty("parts")[0];
        part0.GetProperty("text").GetString().Should().NotBeNullOrEmpty();

        JsonElement part1 = root.GetProperty("contents")[0].GetProperty("parts")[1];
        JsonElement inlineData = part1.GetProperty("inline_data");
        inlineData.GetProperty("mime_type").GetString().Should().Be("image/jpeg");
        inlineData.GetProperty("data").GetString().Should().NotBeNullOrEmpty();

        JsonElement generationConfig = root.GetProperty("generationConfig");
        generationConfig.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        generationConfig.GetProperty("maxOutputTokens").GetInt32().Should().Be(1234);
    }

    [Fact]
    public async Task ReadAsync_UsesConfiguredModelInRequestPath()
    {
        const string responseJson = """{"candidates":[{"content":{"parts":[{"text":"{\"books\":[]}"}]},"finishReason":"STOP"}]}""";

        string customEndpoint =
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, customEndpoint).Respond("application/json", responseJson);

        GeminiVisionBookReader reader = CreateReader(mockHttp, options => options.Model = "gemini-1.5-flash");

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        result.Books.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WhenFinishReasonIsMaxTokens_KeepsParsedBooksAndAddsTruncationNote()
    {
        const string responseJson = """
            {
              "candidates": [
                {
                  "content": { "parts": [ { "text": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.9}]}" } ] },
                  "finishReason": "MAX_TOKENS"
                }
              ]
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        GeminiVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        result.Books.Should().ContainSingle();
        result.Notes.Should().ContainSingle(note => note.Contains("truncated"));
    }

    [Fact]
    public async Task ReadAsync_WhenContentUnparseable_ReturnsNoBooksWithNote()
    {
        const string responseJson = """{"candidates":[{"content":{"parts":[{"text":"not json"}]},"finishReason":"STOP"}]}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        GeminiVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        result.Books.Should().BeEmpty();
        result.Notes.Should().ContainSingle(note => note.Contains("could not be parsed"));
    }

    [Fact]
    public async Task ReadAsync_OnUnauthorized_ThrowsVisionBookReaderExceptionDescribingApiKeyRejection()
    {
        const string responseJson = """{"error":{"code":401,"message":"API key not valid","status":"UNAUTHENTICATED"}}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(HttpStatusCode.Unauthorized, "application/json", responseJson);

        GeminiVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        (await act.Should().ThrowAsync<VisionBookReaderException>())
            .WithMessage("*rejected the configured API key (401)*API key not valid*");
    }

    [Fact]
    public async Task ReadAsync_OnServerError_ThrowsVisionBookReaderExceptionDescribingServerError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        GeminiVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        (await act.Should().ThrowAsync<VisionBookReaderException>())
            .WithMessage("*server error (500)*");
    }

    private static GeminiVisionBookReader CreateReader(MockHttpMessageHandler mockHttp, Action<GeminiVisionOptions>? configure = null)
    {
        var options = new GeminiVisionOptions { ApiKey = "test-api-key" };
        configure?.Invoke(options);

        var httpClient = mockHttp.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);

        return new GeminiVisionBookReader(httpClient, Microsoft.Extensions.Options.Options.Create(options), NullLogger<GeminiVisionBookReader>.Instance);
    }

    private static byte[] CreateJpegBytes()
    {
        using var mat = new Mat(8, 8, MatType.CV_8UC3, new Scalar(255, 255, 255));
        Cv2.ImEncode(".jpg", mat, out byte[] bytes);
        return bytes;
    }
}
