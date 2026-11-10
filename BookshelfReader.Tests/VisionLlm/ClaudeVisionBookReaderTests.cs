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

public class ClaudeVisionBookReaderTests
{
    private static readonly byte[] TestImageBytes = CreateJpegBytes();

    [Fact]
    public async Task ReadAsync_OnSuccess_ParsesBooksFromStructuredOutput()
    {
        const string responseJson = """
            {
              "content": [
                { "type": "text", "text": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.95},{\"title\":\"Foundation\",\"author\":\"\",\"confidence\":0.6}]}" }
              ],
              "stop_reason": "end_turn"
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond("application/json", responseJson);

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Equal(2, result.Books.Count);
        Assert.Equal(new VisionBookEntry("Dune", "Frank Herbert", 0.95), result.Books[0]);
        Assert.Equal(new VisionBookEntry("Foundation", string.Empty, 0.6), result.Books[1]);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task ReadAsync_SendsExpectedRequestShape()
    {
        const string responseJson = """{"content":[{"type":"text","text":"{\"books\":[]}"}],"stop_reason":"end_turn"}""";

        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        ClaudeVisionBookReader reader = CreateReader(mockHttp, options =>
        {
            options.Model = "claude-haiku-4-5";
            options.MaxTokens = 1234;
        });

        await reader.ReadAsync(TestImageBytes);

        Assert.NotNull(capturedBody);
        using var document = JsonDocument.Parse(capturedBody);
        JsonElement root = document.RootElement;

        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());

        JsonElement message = root.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());

        JsonElement imageBlock = message.GetProperty("content")[0];
        Assert.Equal("image", imageBlock.GetProperty("type").GetString());
        Assert.Equal("base64", imageBlock.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", imageBlock.GetProperty("source").GetProperty("media_type").GetString());
        Assert.False(string.IsNullOrEmpty(imageBlock.GetProperty("source").GetProperty("data").GetString()));

        JsonElement textBlock = message.GetProperty("content")[1];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(textBlock.GetProperty("text").GetString()));

        JsonElement schema = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", schema.GetProperty("type").GetString());
        Assert.Equal("books", schema.GetProperty("schema").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task ReadAsync_WhenStructuredOutputIsUnparseable_ReturnsNoBooksWithNote()
    {
        const string responseJson = """
            {
              "content": [ { "type": "text", "text": "not json" } ],
              "stop_reason": "end_turn"
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond("application/json", responseJson);

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Empty(result.Books);
        Assert.Single(result.Notes, note => note.Contains("could not be parsed"));
    }

    [Fact]
    public async Task ReadAsync_WhenStopReasonIsMaxTokens_KeepsParsedBooksAndAddsTruncationNote()
    {
        const string responseJson = """
            {
              "content": [ { "type": "text", "text": "{\"books\":[{\"title\":\"Dune\",\"author\":\"Frank Herbert\",\"confidence\":0.9}]}" } ],
              "stop_reason": "max_tokens"
            }
            """;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond("application/json", responseJson);

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        VisionReadResult result = await reader.ReadAsync(TestImageBytes);

        Assert.Single(result.Books);
        Assert.Single(result.Notes, note => note.Contains("truncated"));
    }

    [Fact]
    public async Task ReadAsync_OnUnauthorized_ThrowsVisionBookReaderExceptionDescribingApiKeyRejection()
    {
        const string responseJson = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", responseJson);

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Matches(@"(?is)rejected the configured API key \(401\).*invalid x-api-key", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_OnTooManyRequests_ThrowsVisionBookReaderExceptionDescribingRateLimit()
    {
        const string responseJson = """{"type":"error","error":{"type":"rate_limit_error","message":"rate limited"}}""";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", responseJson);

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Matches(@"(?is)rate limiting requests \(429\).*rate limited", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_OnServerError_ThrowsVisionBookReaderExceptionDescribingServerError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        ClaudeVisionBookReader reader = CreateReader(mockHttp);

        Func<Task> act = () => reader.ReadAsync(TestImageBytes);

        VisionBookReaderException exception = await Assert.ThrowsAnyAsync<VisionBookReaderException>(act);
        Assert.Contains("server error (500)", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaudeVisionBookReader CreateReader(MockHttpMessageHandler mockHttp, Action<ClaudeVisionOptions>? configure = null)
    {
        var options = new ClaudeVisionOptions
        {
            ApiKey = "test-api-key"
        };
        configure?.Invoke(options);

        var httpClient = mockHttp.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://api.anthropic.com/");

        return new ClaudeVisionBookReader(httpClient, Microsoft.Extensions.Options.Options.Create(options), NullLogger<ClaudeVisionBookReader>.Instance);
    }

    private static byte[] CreateJpegBytes()
    {
        using var mat = new Mat(8, 8, MatType.CV_8UC3, new Scalar(255, 255, 255));
        Cv2.ImEncode(".jpg", mat, out byte[] bytes);
        return bytes;
    }
}
