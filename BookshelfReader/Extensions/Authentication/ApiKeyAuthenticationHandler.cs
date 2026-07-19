using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace BookshelfReader.Extensions.Authentication;

internal sealed partial class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.RequireApiKey)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (Options.ValidKeys.Count == 0)
        {
            LogNoValidKeysConfigured(Logger);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        if (!Request.Headers.TryGetValue(Options.HeaderName, out StringValues headerValues))
        {
            LogMissingApiKeyHeader(Logger, Context.Connection.RemoteIpAddress, Options.HeaderName);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        string providedKey = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            LogEmptyApiKeyHeader(Logger, Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        foreach (string validKey in Options.ValidKeys)
        {
            if (ApiKeyValidator.IsMatch(validKey, providedKey))
            {
                Claim[] claims = new[]
                {
                    new Claim(ApiKeyAuthenticationDefaults.ApiKeyClaimType, ApiKeyValidator.CreateKeyIdentifier(validKey))
                };
                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }

        LogInvalidApiKey(Logger, Context.Connection.RemoteIpAddress);
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "API key authentication is configured without any valid keys. Rejecting request.")]
    private static partial void LogNoValidKeysConfigured(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Request from {RemoteIp} is missing the required API key header {HeaderName}.")]
    private static partial void LogMissingApiKeyHeader(ILogger logger, IPAddress? remoteIp, string headerName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Request from {RemoteIp} provided an empty API key header.")]
    private static partial void LogEmptyApiKeyHeader(ILogger logger, IPAddress? remoteIp);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Request from {RemoteIp} presented an invalid API key.")]
    private static partial void LogInvalidApiKey(ILogger logger, IPAddress? remoteIp);
}
