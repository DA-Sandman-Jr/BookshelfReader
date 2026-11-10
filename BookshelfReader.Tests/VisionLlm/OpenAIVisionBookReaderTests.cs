using System.Net;
using System.Text;
using System.Text.Json;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.VisionLlm;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using RichardSzalay.MockHttp;
using Xunit;

namespace BookshelfReader.Tests.VisionLlm;

public class OpenAIVisionBookReaderTests
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    private static readonly byte[] TestImageBytes = CreateJpegBytes();

    [Fact]
    public async Task ReadAsync_OnSuccess_ParsesBooksFromJsonContent()
    {
        const string responseJson = """
            {
              "choices": [
                {
                  "message": { "content": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.95},{\"title\":\"Foundation\",\"author\":\"\",\"confidence\":0.6}]}" },
                  "finish_reason": "stop"
                }
              ]
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Equal(2, result.Books.Count);
        Assert.Equal(new VisionBookEntry("Dune", "Frank Herbert", 0.95), result.Books[0]);
        Assert.Equal(new VisionBookEntry("Foundation", string.Empty, 0.6), result.Books[1]);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task ReadAsync_SendsExpectedRequestShape()
    {
        const string responseJson = """{"choices":[{"message":{"content":"{\"books\":[]}"},"finish_reason":"stop"}]}""";

        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        OpenAIVisionBookReader reader = CreateReader(mockHttp, options =>
        {
            options.Model = "gpt-4o-mini";
            options.MaxTokens = 1234;
        });

        await reader.ReadAsync(TestImageBytes);

        Assert.NotNull(capturedBody);
        using var document = JsonDocument.Parse(capturedBody);
        JsonElement root = document.RootElement;

        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());

        JsonElement message = root.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());

        JsonElement textBlock = message.GetProperty("content")[0];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(textBlock.GetProperty("text").GetString()));

        JsonElement imageBlock = message.GetProperty("content")[1];
        Assert.Equal("image_url", imageBlock.GetProperty("type").GetString());
        Assert.StartsWith("data:image/jpeg;base64,", imageBlock.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReadAsync_SendsBearerAuthorizationHeader()
    {
        const string responseJson = """{"choices":[{"message":{"content":"{\"books\":[]}"},"finish_reason":"stop"}]}""";

        var mockHttp = new MockHttpMessageHandler();
        string? authHeader = null;

        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(request =>
            {
                authHeader = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://api.openai.com/");
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");

        var reader = new OpenAIVisionBookReader(
            httpClient,
            Microsoft.Extensions.Options.Options.Create(new OpenAIVisionOptions()),
            NullLogger<OpenAIVisionBookReader>.Instance);

        await reader.ReadAsync(TestImageBytes);

        Assert.Equal("Bearer test-api-key", authHeader);
    }

    [Fact]
    public async Task ReadAsync_WhenContentWrappedInProse_ExtractsEmbeddedJson()
    {
        const string responseJson = """
            {
              "choices": [
                { "message": { "content": "Here you go: {\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.9}]}" }, "finish_reason": "stop" }
              ]
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Single(result.Books);
        Assert.Equal("Dune", result.Books[0].Title);
    }

    [Fact]
    public async Task ReadAsync_WhenFinishReasonIsLength_KeepsParsedBooksAndAddsTruncationNote()
    {
        const string responseJson = """
            {
              "choices": [
                { "message": { "content": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.9}]}" }, "finish_reason": "length" }
              ]
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint).Respond("application/json", responseJson);

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Single(result.Books);
        Assert.Single(result.Notes, note => note.Contains("truncated"));
    }

    [Fact]
    public async Task ReadAsync_OnUnauthorized_ThrowsVisionBookReaderExceptionDescribingApiKeyRejection()
    {
        const string responseJson = """{"error":{"type":"invalid_request_error","message":"invalid api key"}}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(HttpStatusCode.Unauthorized, "application/json", responseJson);

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Matches(@"(?is)rejected the configured API key \(401\).*invalid api key", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_OnTooManyRequests_ThrowsVisionBookReaderExceptionDescribingRateLimit()
    {
        const string responseJson = """{"error":{"type":"rate_limit_error","message":"rate limited"}}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(HttpStatusCode.TooManyRequests, "application/json", responseJson);

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Matches(@"(?is)rate limiting requests \(429\).*rate limited", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_OnServerError_ThrowsVisionBookReaderExceptionDescribingServerError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, Endpoint)
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        OpenAIVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Contains("server error (500)", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OpenAIVisionBookReader CreateReader(MockHttpMessageHandler mockHttp, Action<OpenAIVisionOptions>? configure = null)
    {
        var options = new OpenAIVisionOptions { ApiKey = "test-api-key" };
        configure?.Invoke(options);

        var httpClient = mockHttp.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://api.openai.com/");

        return new OpenAIVisionBookReader(httpClient, Microsoft.Extensions.Options.Options.Create(options), NullLogger<OpenAIVisionBookReader>.Instance);
    }

    private static byte[] CreateJpegBytes()
    {
        using var mat = new Mat(8, 8, MatType.CV_8UC3, new Scalar(255, 255, 255));
        Cv2.ImEncode(".jpg", mat, out byte[] bytes);
        return bytes;
    }
}
