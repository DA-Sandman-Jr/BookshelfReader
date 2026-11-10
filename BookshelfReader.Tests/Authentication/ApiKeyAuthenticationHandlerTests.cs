using System.Text.Encodings.Web;
using BookshelfReader.Extensions.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookshelfReader.Tests.Authentication;

public class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_Fails_WhenHeaderMissing()
    {
        ApiKeyAuthenticationOptions options = CreateOptions();
        var context = new DefaultHttpContext();
        ApiKeyAuthenticationHandler handler = CreateHandler(options, context);

        AuthenticateResult result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task AuthenticateAsync_Fails_WhenHeaderEmpty()
    {
        ApiKeyAuthenticationOptions options = CreateOptions();
        var context = new DefaultHttpContext();
        context.Request.Headers[options.HeaderName] = string.Empty;
        ApiKeyAuthenticationHandler handler = CreateHandler(options, context);

        AuthenticateResult result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task AuthenticateAsync_Succeeds_WithValidKey()
    {
        const string expectedKey = "valid-secret";
        ApiKeyAuthenticationOptions options = CreateOptions(validKey: expectedKey);
        var context = new DefaultHttpContext();
        context.Request.Headers[options.HeaderName] = expectedKey;
        ApiKeyAuthenticationHandler handler = CreateHandler(options, context);

        AuthenticateResult result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal(ApiKeyValidator.CreateKeyIdentifier(expectedKey),
            result.Principal.FindFirst(ApiKeyAuthenticationDefaults.ApiKeyClaimType)!.Value);
    }

    private static ApiKeyAuthenticationHandler CreateHandler(ApiKeyAuthenticationOptions options, HttpContext context)
    {
        var optionsMonitor = new StaticOptionsMonitor<ApiKeyAuthenticationOptions>(options);
        var handler = new ApiKeyAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default);
        var scheme = new AuthenticationScheme(ApiKeyAuthenticationDefaults.AuthenticationScheme,
            ApiKeyAuthenticationDefaults.AuthenticationScheme, typeof(ApiKeyAuthenticationHandler));

        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();
        return handler;
    }

    private static ApiKeyAuthenticationOptions CreateOptions(string validKey = "valid")
    {
        return new ApiKeyAuthenticationOptions
        {
            RequireApiKey = true,
            ValidKeys = { validKey }
        };
    }

    private sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> where TOptions : class
    {
        public StaticOptionsMonitor(TOptions options)
        {
            CurrentValue = options;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable OnChange(Action<TOptions, string?> listener)
        {
            return NullDisposable.Instance;
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
