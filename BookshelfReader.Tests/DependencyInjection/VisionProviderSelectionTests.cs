using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions;
using BookshelfReader.Infrastructure.VisionLlm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookshelfReader.Tests.DependencyInjection;

public class VisionProviderSelectionTests
{
    [Fact]
    public void AddBookshelfReader_DefaultsToClaudeProvider()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClaudeVision:ApiKey"] = "test-api-key"
        });

        IVisionBookReader reader = provider.GetRequiredService<IVisionBookReader>();

        reader.Should().BeOfType<ClaudeVisionBookReader>();
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

        reader.Should().BeOfType<OpenAIVisionBookReader>();
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

        reader.Should().BeOfType<GeminiVisionBookReader>();
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

            Action act = () => provider.GetRequiredService<IVisionBookReader>();

            act.Should().NotThrow();
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

            act.Should().Throw<OptionsValidationException>()
                .WithMessage("*OpenAIVision:ApiKey must be configured, or set the OPENAI_API_KEY environment variable.*");
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

            options.ApiKey.Should().Be("env-openai-key");
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

            act.Should().Throw<OptionsValidationException>()
                .WithMessage("*GeminiVision:ApiKey must be configured, or set the GEMINI_API_KEY environment variable.*");
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

            options.ApiKey.Should().Be("env-gemini-key");
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

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Vision:Provider must be one of*");
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

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*OpenAIVision:BaseUrl must be an absolute https URL.*");
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
