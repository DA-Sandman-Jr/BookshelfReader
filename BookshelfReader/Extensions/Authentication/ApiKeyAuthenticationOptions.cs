using Microsoft.AspNetCore.Authentication;

namespace BookshelfReader.Extensions.Authentication;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SectionName = "Authentication:ApiKey";

    public string HeaderName { get; set; } = "X-API-Key";

    public bool RequireApiKey { get; set; }

    public IList<string> ValidKeys { get; set; } = new List<string>();
}
