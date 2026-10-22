using System.Net;
using System.Net.Http.Headers;
using BookshelfReader.Api.Extensions;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BookshelfReader.Tests.Integration;

public class ApiSecurityRegressionTests
{
    private const string ParsePath = "/api/bookshelf/parse";
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parse_WhenEntireBodyExceedsLimit_RejectsKnownAndUnknownLengths(bool unknownLength)
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using var multipart = new MultipartFormDataContent();
        AddImage(multipart);
        // Each file fits the 1 KB file limit, but aggregate bytes exceed it.
        for (int i = 0; i < 80; i++)
        {
            multipart.Add(new ByteArrayContent(new byte[1024]), "extra", "extra.png");
        }

        using HttpContent content = unknownLength ? new UnknownLengthContent(multipart) : multipart;
        using HttpResponseMessage response = await client.PostAsync(ParsePath, content, app.Lifetime.ApplicationStopping);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Parse_WhenExtraFileIsIncluded_RejectsRequest()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();
        using var multipart = new MultipartFormDataContent();
        AddImage(multipart);
        AddImage(multipart);

        using HttpResponseMessage response = await client.PostAsync(ParsePath, multipart, app.Lifetime.ApplicationStopping);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Parse_WhenProviderFails_ReturnsBadGatewayWithoutUpstreamDetails()
    {
        await using WebApplication app = await CreateAppAsync(providerFails: true);
        using HttpClient client = app.GetTestClient();
        using var multipart = new MultipartFormDataContent();
        AddImage(multipart);

        using HttpResponseMessage response = await client.PostAsync(ParsePath, multipart, app.Lifetime.ApplicationStopping);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync(app.Lifetime.ApplicationStopping)).Should().NotContain("upstream-secret");
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
    }

    [Fact]
    public async Task Parse_WhenUnauthorized_IncludesSecurityHeaders()
    {
        await using WebApplication app = await CreateAppAsync(requireApiKey: true);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsync(ParsePath, new ByteArrayContent(Array.Empty<byte>()), app.Lifetime.ApplicationStopping);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    private static async Task<WebApplication> CreateAppAsync(bool providerFails = false, bool requireApiKey = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClaudeVision:ApiKey"] = "test-key",
            ["Authentication:ApiKey:RequireApiKey"] = requireApiKey.ToString(),
            ["Authentication:ApiKey:ValidKeys:0"] = "test-key",
            ["Uploads:MaxBytes"] = "1024",
            ["Enrichment:Enabled"] = "false"
        });
        builder.AddBookshelfReaderHost();
        builder.Services.Replace(ServiceDescriptor.Singleton<IVisionBookReader>(new StubVisionBookReader(providerFails)));
        WebApplication app = builder.Build();
        app.UseBookshelfReaderHost();
        await app.StartAsync(app.Lifetime.ApplicationStopping);
        // Requests use HTTPS so the host's HTTPS redirection doesn't mask validation.
        app.GetTestServer().BaseAddress = new Uri("https://localhost");
        return app;
    }

    private static void AddImage(MultipartFormDataContent multipart)
    {
        var image = new ByteArrayContent(PngBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(image, "image", "shelf.png");
    }

    private sealed class StubVisionBookReader(bool fails) : IVisionBookReader
    {
        public Task<VisionReadResult> ReadAsync(byte[] imageData, CancellationToken cancellationToken = default)
        {
            if (fails)
            {
                throw new VisionBookReaderException("Provider echoed upstream-secret");
            }

            return Task.FromResult(new VisionReadResult());
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _content;

        public UnknownLengthContent(HttpContent content)
        {
            _content = content;
            Headers.ContentType = content.Headers.ContentType;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _content.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
