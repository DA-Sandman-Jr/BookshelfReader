using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BookshelfReader.Api.Endpoints;
using BookshelfReader.Api.Extensions;
using BookshelfReader.Api.RateLimiting;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Extensions;
using BookshelfReader.Extensions.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BookshelfReader.Tests.Integration;

public class BookshelfReaderApiIntegrationTests
{
    private const string ApiKey = "integration-test-key";
    private const string ParsePath = "/api/bookshelf/parse";
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Parse_WhenApiKeyRequiredAndValid_ReturnsOk()
    {
        Dictionary<string, string?> settings = CreateSettings(requireApiKey: true);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage response = await SendParseAsync(client, ApiKey);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            ParseResult? result = await response.Content.ReadFromJsonAsync<ParseResult>();
            result.Should().NotBeNull();
            result!.Books.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Parse_WhenApiKeyRequiredAndMissing_ReturnsUnauthorized()
    {
        Dictionary<string, string?> settings = CreateSettings(requireApiKey: true);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage response = await SendParseAsync(client);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        });
    }

    [Fact]
    public async Task Parse_WhenApiKeyRequiredAndWrong_ReturnsUnauthorized()
    {
        Dictionary<string, string?> settings = CreateSettings(requireApiKey: true);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage response = await SendParseAsync(client, "wrong-key");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        });
    }

    [Fact]
    public async Task Parse_WhenApiKeyNotRequired_ReturnsOkAnonymously()
    {
        Dictionary<string, string?> settings = CreateSettings(requireApiKey: false);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage response = await SendParseAsync(client);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        });
    }

    [Fact]
    public async Task Parse_WhenRateLimitExceeded_ReturnsTooManyRequestsWithRetryAfter()
    {
        Dictionary<string, string?> settings = CreateSettings(
            requireApiKey: false,
            rateLimitingEnabled: true,
            permitLimit: 2);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage firstResponse = await SendParseAsync(client);
            using HttpResponseMessage secondResponse = await SendParseAsync(client);
            using HttpResponseMessage thirdResponse = await SendParseAsync(client);

            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            thirdResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            thirdResponse.Headers.RetryAfter.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Parse_WhenRateLimitingDisabled_DoesNotThrottleRapidRequests()
    {
        Dictionary<string, string?> settings = CreateSettings(
            requireApiKey: false,
            rateLimitingEnabled: false,
            permitLimit: 2);

        await RunWithAppAsync(settings, async client =>
        {
            using HttpResponseMessage firstResponse = await SendParseAsync(client);
            using HttpResponseMessage secondResponse = await SendParseAsync(client);
            using HttpResponseMessage thirdResponse = await SendParseAsync(client);

            firstResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            secondResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            thirdResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        });
    }

    private static Dictionary<string, string?> CreateSettings(
        bool requireApiKey,
        bool rateLimitingEnabled = false,
        int permitLimit = 10)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:ApiKey:RequireApiKey"] = requireApiKey.ToString(),
            ["Authentication:ApiKey:ValidKeys:0"] = ApiKey,
            ["RateLimiting:Parse:Enabled"] = rateLimitingEnabled.ToString(),
            ["RateLimiting:Parse:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting:Parse:WindowSeconds"] = "60",
            ["ClaudeVision:ApiKey"] = "integration-test-key"
        };
    }

    private static async Task RunWithAppAsync(
        IReadOnlyDictionary<string, string?> settings,
        Func<HttpClient, Task> test)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddBookshelfReader(builder.Configuration);
        builder.Services.AddBookshelfReaderRateLimiting(builder.Configuration);
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
            })
            .AddBookshelfReaderApiKey();
        builder.Services.AddAuthorization();
        builder.Services.AddBookshelfReaderApi();

        builder.Services.Replace(ServiceDescriptor.Singleton<IVisionBookReader>(new StubVisionBookReader()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IBookLookupService>(new StubBookLookupService()));

        WebApplication app = builder.Build();
        try
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapBookshelfReaderApi();

            await app.StartAsync(app.Lifetime.ApplicationStopping);
            using HttpClient client = app.GetTestClient();
            await test(client);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> SendParseAsync(HttpClient client, string? apiKey = null)
    {
        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(PngBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "bookshelf.png");

        using var request = new HttpRequestMessage(HttpMethod.Post, ParsePath)
        {
            Content = content
        };
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }

        return await client.SendAsync(request);
    }

    private sealed class StubVisionBookReader : IVisionBookReader
    {
        public Task<VisionReadResult> ReadAsync(byte[] imageData, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new VisionReadResult());
        }
    }

    private sealed class StubBookLookupService : IBookLookupService
    {
        public Task<BookLookupResult> LookupAsync(string query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BookLookupResult.Success(Array.Empty<BookMetadata>()));
        }
    }
}
