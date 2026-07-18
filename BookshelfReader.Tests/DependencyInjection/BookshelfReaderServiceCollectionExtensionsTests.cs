using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions;
using BookshelfReader.Extensions.Authentication;
using BookshelfReader.Infrastructure.VisionLlm;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookshelfReader.Tests.DependencyInjection;

public class BookshelfReaderServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBookshelfReader_RegistersExpectedServices()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:ApiKey:RequireApiKey"] = "false",
            ["Uploads:MaxBytes"] = "1048576",
            ["Uploads:AllowedContentTypes:0"] = "image/jpeg",
            ["Uploads:AllowedContentTypes:1"] = "image/png"
        }).Build();

        var services = new ServiceCollection();

        services.Invoking(s => s.AddBookshelfReader(configuration))
            .Should().NotThrow();

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IVisionBookReader));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBookshelfProcessingService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBookLookupService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBookEnrichmentService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IGenreClassifier));
    }

    [Fact]
    public void AddBookshelfReader_InvalidEnrichmentOptions_ThrowsValidationException()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Enrichment:MaxConcurrentLookups"] = "0"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptions<BookshelfReader.Core.Options.EnrichmentOptions>>().Value.ToString();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Enrichment:MaxConcurrentLookups must be between 1 and 16.*");
    }

    [Fact]
    public void AddBookshelfReader_InvalidUploadsOptions_ThrowsValidationException()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Uploads:MaxBytes"] = "25000000", // 25 MB exceeds allowed maximum
            ["Uploads:AllowedContentTypes:0"] = "image/jpeg"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptions<BookshelfReader.Core.Options.UploadsOptions>>().Value.ToString();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Uploads:MaxBytes must be between 1 byte and 20 MB.*");
    }

    [Fact]
    public void AddBookshelfReader_RequiresApiKeyWithoutKeys_ThrowsValidationException()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:ApiKey:RequireApiKey"] = "true",
            ["Authentication:ApiKey:HeaderName"] = "X-API-Key"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptions<ApiKeyAuthenticationOptions>>().Value.RequireApiKey.ToString();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*At least one non-empty API key must be configured when API keys are required.*");
    }

    [Fact]
    public void AddBookshelfReader_ConfiguresFormOptionsToKeepUploadsInMemory()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Uploads:MaxBytes"] = "1048576",
            ["Uploads:AllowedContentTypes:0"] = "image/jpeg"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        FormOptions formOptions = provider.GetRequiredService<IOptions<FormOptions>>().Value;

        formOptions.MultipartBodyLengthLimit.Should().Be(1048576);
        formOptions.MemoryBufferThreshold.Should().Be(1048576);
    }

    [Fact]
    public void AddBookshelfReader_RejectsNonHttpsOpenLibraryBaseUrl()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OpenLibrary:BaseUrl"] = "http://example.com/"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IBookLookupService>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("OpenLibrary:BaseUrl must use HTTPS.");
    }

    [Fact]
    public void AddBookshelfReader_WithoutClaudeVisionApiKeyOrEnvVar_ThrowsValidationException()
    {
        string? originalEnvVar = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)).Build();

            var services = new ServiceCollection();
            services.AddBookshelfReader(configuration);

            ServiceProvider provider = services.BuildServiceProvider();

            Action act = () => provider.GetRequiredService<IOptions<ClaudeVisionOptions>>().Value.ToString();

            act.Should().Throw<OptionsValidationException>()
                .WithMessage("*ClaudeVision:ApiKey must be configured, or set the ANTHROPIC_API_KEY environment variable.*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalEnvVar);
        }
    }

    [Fact]
    public void AddBookshelfReader_FallsBackToAnthropicApiKeyEnvironmentVariable()
    {
        string? originalEnvVar = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-api-key");
        try
        {
            IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)).Build();

            var services = new ServiceCollection();
            services.AddBookshelfReader(configuration);

            ServiceProvider provider = services.BuildServiceProvider();

            ClaudeVisionOptions options = provider.GetRequiredService<IOptions<ClaudeVisionOptions>>().Value;

            options.ApiKey.Should().Be("env-api-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalEnvVar);
        }
    }

    [Fact]
    public void AddBookshelfReader_RejectsNonHttpsClaudeVisionBaseUrl()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClaudeVision:ApiKey"] = "test-api-key",
            ["ClaudeVision:BaseUrl"] = "http://example.com/"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptions<ClaudeVisionOptions>>().Value.ToString();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*ClaudeVision:BaseUrl must be an absolute https URL.*");
    }

    [Fact]
    public void AddBookshelfReader_WithValidApiKey_ResolvesVisionBookReader()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClaudeVision:ApiKey"] = "test-api-key"
        }).Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        IVisionBookReader reader = provider.GetRequiredService<IVisionBookReader>();

        reader.Should().BeOfType<ClaudeVisionBookReader>();
    }
}
