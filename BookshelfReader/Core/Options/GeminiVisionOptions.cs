namespace BookshelfReader.Core.Options;

/// <summary>
/// Options for the Google Gemini vision provider (Generative Language API).
/// Used only when <c>Vision:Provider</c> is set to <c>Gemini</c>.
/// </summary>
public sealed class GeminiVisionOptions
{
    public const string SectionName = "GeminiVision";

    /// <summary>
    /// Gemini API key. If left empty, <c>AddBookshelfReader</c> falls back to the
    /// <c>GEMINI_API_KEY</c> environment variable.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    /// <summary>
    /// A current multimodal Gemini model. Defaults to a fast, low-cost model that
    /// accepts image inputs.
    /// </summary>
    public string Model { get; set; } = "gemini-3.6-flash";

    public int MaxTokens { get; set; } = 2048;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Images are downscaled so their longest edge does not exceed this many
    /// pixels before being sent to the Gemini API.
    /// </summary>
    public int MaxImageDimension { get; set; } = 1568;
}
