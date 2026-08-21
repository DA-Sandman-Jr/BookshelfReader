using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions.Authentication;
using BookshelfReader.Infrastructure.Enrichment;
using BookshelfReader.Infrastructure.Genres;
using BookshelfReader.Infrastructure.Lookup;
using BookshelfReader.Infrastructure.Processing;
using BookshelfReader.Infrastructure.VisionLlm;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Extensions;

public static class BookshelfReaderServiceCollectionExtensions
{
    private const string AnthropicVersion = "2023-06-01";

    public static IServiceCollection AddBookshelfReader(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddProblemDetails();

        services.AddOptions<ApiKeyAuthenticationOptions>()
            .Bind(configuration.GetSection(ApiKeyAuthenticationOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.HeaderName), "API key header name must be configured.")
            .Validate(options => !options.RequireApiKey || options.ValidKeys is { Count: > 0 }
                    && options.ValidKeys.All(key => !string.IsNullOrWhiteSpace(key)),
                "At least one non-empty API key must be configured when API keys are required.")
            .ValidateOnStart();

        // AuthenticationHandler resolves the scheme-named options instance, which
        // does not inherit the unnamed binding above; bind it to the same section
        // or the handler sees defaults (RequireApiKey=false, no keys) and rejects
        // every request when enforcement is enabled.
        services.AddOptions<ApiKeyAuthenticationOptions>(ApiKeyAuthenticationDefaults.AuthenticationScheme)
            .Bind(configuration.GetSection(ApiKeyAuthenticationOptions.SectionName));

        services.AddOptions<UploadsOptions>()
            .Bind(configuration.GetSection(UploadsOptions.SectionName))
            .Validate(options => options.MaxBytes is > 0 and <= 20 * 1024 * 1024,
                "Uploads:MaxBytes must be between 1 byte and 20 MB.")
            .Validate(options => options.AllowedContentTypes is { Count: > 0 }
                && options.AllowedContentTypes.All(UploadsOptions.IsSupportedContentType),
                $"Uploads:AllowedContentTypes must contain only supported image MIME types ({string.Join(", ", UploadsOptions.SupportedImageSignatures.Keys)}).")
            .Validate(options => options.MaxImagePixels is > 0 and <= 50_000_000,
                "Uploads:MaxImagePixels must be between 1 and 50,000,000.")
            .ValidateOnStart();

        services.AddOptions<FormOptions>()
            .Configure<IOptions<UploadsOptions>>((formOptions, uploadOptions) =>
            {
                formOptions.MultipartBodyLengthLimit = uploadOptions.Value.MaxBytes;

                // Keep uploads fully in memory: the form feature buffers multipart
                // sections larger than MemoryBufferThreshold (default 64 KB) to temp
                // files on disk, which would break the guarantee that uploaded images
                // are never stored. MaxBytes is validated at <= 20 MB above, so the
                // cast is safe and per-request memory stays bounded.
                formOptions.MemoryBufferThreshold = (int)Math.Min(uploadOptions.Value.MaxBytes, int.MaxValue);
            });

        VisionProvider provider = ResolveVisionProvider(configuration);

        // Configure rather than Bind: the raw string is parsed by
        // ResolveVisionProvider so an unknown value surfaces through the
        // validator below (with a friendly message) instead of a binder
        // conversion exception.
        services.AddOptions<VisionOptions>()
            .Configure(options => options.Provider = provider)
            .Validate(options => Enum.IsDefined(options.Provider),
                $"Vision:Provider must be one of: {string.Join(", ", Enum.GetNames<VisionProvider>())}.")
            .ValidateOnStart();

        services.AddOptions<EnrichmentOptions>()
            .Bind(configuration.GetSection(EnrichmentOptions.SectionName))
            .Validate(options => options.MaxConcurrentLookups is >= 1 and <= 16,
                "Enrichment:MaxConcurrentLookups must be between 1 and 16.")
            .Validate(options => options.MinMatchScore is >= 0 and <= 100,
                "Enrichment:MinMatchScore must be between 0 and 100.")
            .ValidateOnStart();

        services.AddSingleton<IGenreClassifier, KeywordGenreClassifier>();
        services.AddSingleton<IBookEnrichmentService, BookEnrichmentService>();
        services.AddSingleton<IBookshelfProcessingService, BookshelfProcessingService>();

        AddVisionBookReader(services, configuration, provider);

        services.AddHttpClient<IBookLookupService, OpenLibraryLookupService>((_, client) =>
        {
            string baseUrl = configuration["OpenLibrary:BaseUrl"] ?? "https://openlibrary.org/";

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            {
                throw new InvalidOperationException("OpenLibrary:BaseUrl must be a valid absolute URI.");
            }

            if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("OpenLibrary:BaseUrl must use HTTPS.");
            }

            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(10);

            // Open Library's API guidelines ask clients to identify themselves;
            // anonymous high-volume traffic risks throttling.
            string userAgent = configuration["OpenLibrary:UserAgent"]
                ?? "BookshelfReader/1.0 (+https://github.com/DA-Sandman-Jr/BookshelfReader)";
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        });

        return services;
    }

    /// <summary>
    /// Reads <c>Vision:Provider</c> from configuration. Defaults to
    /// <see cref="VisionProvider.Claude"/> (preserving the original behavior) and
    /// accepts case-insensitive enum names. An unrecognized value is returned as
    /// an out-of-range sentinel so the <see cref="VisionOptions"/> validator can
    /// fail fast with a clear message instead of silently falling back.
    /// </summary>
    private static VisionProvider ResolveVisionProvider(IConfiguration configuration)
    {
        string? raw = configuration[$"{VisionOptions.SectionName}:Provider"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return VisionProvider.Claude;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out VisionProvider parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        // Unknown value: surface it (as an undefined enum value) so the
        // VisionOptions validator reports the misconfiguration on start.
        return (VisionProvider)(-1);
    }

    private static void AddVisionBookReader(IServiceCollection services, IConfiguration configuration, VisionProvider provider)
    {
        switch (provider)
        {
            case VisionProvider.OpenAI:
                AddOpenAIVisionBookReader(services, configuration);
                break;
            case VisionProvider.Gemini:
                AddGeminiVisionBookReader(services, configuration);
                break;
            case VisionProvider.Claude:
            default:
                AddClaudeVisionBookReader(services, configuration);
                break;
        }
    }

    private static void AddClaudeVisionBookReader(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClaudeVisionOptions>()
            .Bind(configuration.GetSection(ClaudeVisionOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    options.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
                "ClaudeVision:ApiKey must be configured, or set the ANTHROPIC_API_KEY environment variable.")
            .Validate(options => IsAbsoluteHttps(options.BaseUrl),
                "ClaudeVision:BaseUrl must be an absolute https URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "ClaudeVision:Model must be configured.")
            .Validate(options => options.MaxTokens > 0, "ClaudeVision:MaxTokens must be greater than 0.")
            .Validate(options => options.TimeoutSeconds > 0, "ClaudeVision:TimeoutSeconds must be greater than 0.")
            .Validate(options => options.MaxImageDimension > 0, "ClaudeVision:MaxImageDimension must be greater than 0.")
            .ValidateOnStart();

        services.AddHttpClient<IVisionBookReader, ClaudeVisionBookReader>((sp, client) =>
        {
            ClaudeVisionOptions options = sp.GetRequiredService<IOptions<ClaudeVisionOptions>>().Value;

            client.BaseAddress = ParseBaseAddress(options.BaseUrl, ClaudeVisionOptions.SectionName);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", options.ApiKey);
            client.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        });
    }

    private static void AddOpenAIVisionBookReader(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIVisionOptions>()
            .Bind(configuration.GetSection(OpenAIVisionOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    options.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
                "OpenAIVision:ApiKey must be configured, or set the OPENAI_API_KEY environment variable.")
            .Validate(options => IsAbsoluteHttps(options.BaseUrl),
                "OpenAIVision:BaseUrl must be an absolute https URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "OpenAIVision:Model must be configured.")
            .Validate(options => options.MaxTokens > 0, "OpenAIVision:MaxTokens must be greater than 0.")
            .Validate(options => options.TimeoutSeconds > 0, "OpenAIVision:TimeoutSeconds must be greater than 0.")
            .Validate(options => options.MaxImageDimension > 0, "OpenAIVision:MaxImageDimension must be greater than 0.")
            .ValidateOnStart();

        services.AddHttpClient<IVisionBookReader, OpenAIVisionBookReader>((sp, client) =>
        {
            OpenAIVisionOptions options = sp.GetRequiredService<IOptions<OpenAIVisionOptions>>().Value;

            client.BaseAddress = ParseBaseAddress(options.BaseUrl, OpenAIVisionOptions.SectionName);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        });
    }

    private static void AddGeminiVisionBookReader(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GeminiVisionOptions>()
            .Bind(configuration.GetSection(GeminiVisionOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    options.ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
                "GeminiVision:ApiKey must be configured, or set the GEMINI_API_KEY environment variable.")
            .Validate(options => IsAbsoluteHttps(options.BaseUrl),
                "GeminiVision:BaseUrl must be an absolute https URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "GeminiVision:Model must be configured.")
            .Validate(options => options.MaxTokens > 0, "GeminiVision:MaxTokens must be greater than 0.")
            .Validate(options => options.TimeoutSeconds > 0, "GeminiVision:TimeoutSeconds must be greater than 0.")
            .Validate(options => options.MaxImageDimension > 0, "GeminiVision:MaxImageDimension must be greater than 0.")
            .ValidateOnStart();

        services.AddHttpClient<IVisionBookReader, GeminiVisionBookReader>((sp, client) =>
        {
            GeminiVisionOptions options = sp.GetRequiredService<IOptions<GeminiVisionOptions>>().Value;

            client.BaseAddress = ParseBaseAddress(options.BaseUrl, GeminiVisionOptions.SectionName);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);
        });
    }

    private static bool IsAbsoluteHttps(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ParseBaseAddress(string baseUrl, string sectionName)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            throw new InvalidOperationException($"{sectionName}:BaseUrl must be a valid absolute URI.");
        }

        return baseUri;
    }
}
