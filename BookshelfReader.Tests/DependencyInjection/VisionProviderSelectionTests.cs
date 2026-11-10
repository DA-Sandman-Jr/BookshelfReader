using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions;
using BookshelfReader.Infrastructure.VisionLlm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookshelfReader.Tests.DependencyInjection;

public class VisionProviderSelectionTests
{
    [Theory]
    [InlineData("Claude", "ClaudeVision")]
    [InlineData("OpenAI", "OpenAIVision")]
    [InlineData("Gemini", "GeminiVision")]
    public void AddBookshelfReader_ProviderHttpClientsDoNotForwardKeysAcrossRedirects(string selectedProvider, string section)
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Vision:Provider"] = selectedProvider,
            [$"{section}:ApiKey"] = "test-api-key"
        });

        IHttpMessageHandlerFactory factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = factory.CreateHandler(nameof(IVisionBookReader));
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public void AddBookshelfReader_DefaultsToClaudeProvider()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClaudeVision:ApiKey"] = "test-api-key"
        });

        IVisionBookReader reader = provider.GetRequiredService<IVisionBookReader>();

        Assert.IsType<ClaudeVisionBookReader>(reader);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("openai")]
    [InlineData("OPENAI")]
    public void AddBookshelfReader_WhenProviderIsOpenAI_ResolvesOpenAIReader(string providerValue)
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Vision:Provider"] = providerValue,
            ["OpenAIVision:ApiKey"] = "openai-key"
        });

        IVisionBookReader reader = provider.GetRequiredService<IVisionBookReader>();

        Assert.IsType<OpenAIVisionBookReader>(reader);
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("gemini")]
    public void AddBookshelfReader_WhenProviderIsGemini_ResolvesGeminiReader(string providerValue)
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Vision:Provider"] = providerValue,
            ["GeminiVision:ApiKey"] = "gemini-key"
        });

        IVisionBookReader reader = provider.GetRequiredService<IVisionBookReader>();

        Assert.IsType<GeminiVisionBookReader>(reader);
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsOpenAI_DoesNotRequireAnthropicKey()
    {
        string? originalAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Vision:Provider"] = "OpenAI",
                ["OpenAIVision:ApiKey"] = "openai-key"
            });

            provider.GetRequiredService<IVisionBookReader>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalAnthropic);
        }
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsOpenAIWithoutKeyOrEnvVar_ThrowsValidationException()
    {
        string? original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Vision:Provider"] = "OpenAI"
            });

            Action act = () => provider.GetRequiredService<IOptions<OpenAIVisionOptions>>().Value.ToString();

            OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
            Assert.Contains("OpenAIVision:ApiKey must be configured, or set the OPENAI_API_KEY environment variable.", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        }
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsOpenAI_FallsBackToOpenAiEnvVar()
    {
        string? original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "env-openai-key");
        try
        {
            ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Vision:Provider"] = "OpenAI"
            });

            OpenAIVisionOptions options = provider.GetRequiredService<IOptions<OpenAIVisionOptions>>().Value;

            Assert.Equal("env-openai-key", options.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        }
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsGeminiWithoutKeyOrEnvVar_ThrowsValidationException()
    {
        string? original = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        try
        {
            ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Vision:Provider"] = "Gemini"
            });

            Action act = () => provider.GetRequiredService<IOptions<GeminiVisionOptions>>().Value.ToString();

            OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
            Assert.Contains("GeminiVision:ApiKey must be configured, or set the GEMINI_API_KEY environment variable.", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", original);
        }
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsGemini_FallsBackToGeminiEnvVar()
    {
        string? original = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "env-gemini-key");
        try
        {
            ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Vision:Provider"] = "Gemini"
            });

            GeminiVisionOptions options = provider.GetRequiredService<IOptions<GeminiVisionOptions>>().Value;

            Assert.Equal("env-gemini-key", options.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", original);
        }
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsUnknown_ThrowsValidationException()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Vision:Provider"] = "Llama"
        });

        Action act = () => provider.GetRequiredService<IOptions<VisionOptions>>().Value.ToString();

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("Vision:Provider must be one of", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddBookshelfReader_WhenProviderIsOpenAI_RejectsNonHttpsBaseUrl()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Vision:Provider"] = "OpenAI",
            ["OpenAIVision:ApiKey"] = "openai-key",
            ["OpenAIVision:BaseUrl"] = "http://example.com/"
        });

        Action act = () => provider.GetRequiredService<IOptions<OpenAIVisionOptions>>().Value.ToString();

        OptionsValidationException exception = Assert.ThrowsAny<OptionsValidationException>(act);
        Assert.Contains("OpenAIVision:BaseUrl must be an absolute https URL.", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddBookshelfReader(configuration);
        return services.BuildServiceProvider();
    }
}
