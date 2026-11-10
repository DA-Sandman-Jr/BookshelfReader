using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions;
using BookshelfReader.Extensions.Authentication;
using BookshelfReader.Infrastructure.VisionLlm;
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

        services.AddBookshelfReader(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IVisionBookReader));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBookshelfProcessingService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBookLookupService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBookEnrichmentService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGenreClassifier));
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

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("Enrichment:MaxConcurrentLookups must be between 1 and 16.", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("Uploads:MaxBytes must be between 1 byte and 20 MB.", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("At least one non-empty API key must be configured when API keys are required.", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(1048576, formOptions.MultipartBodyLengthLimit);
        Assert.Equal(1048576, formOptions.MemoryBufferThreshold);
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

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Equal("OpenLibrary:BaseUrl must use HTTPS.", exception.Message, ignoreCase: true);
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

            OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
            Assert.Contains("ClaudeVision:ApiKey must be configured, or set the ANTHROPIC_API_KEY environment variable.", exception.Message, StringComparison.OrdinalIgnoreCase);
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

            Assert.Equal("env-api-key", options.ApiKey);
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

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("ClaudeVision:BaseUrl must be an absolute https URL.", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.IsType<ClaudeVisionBookReader>(reader);
    }
}
