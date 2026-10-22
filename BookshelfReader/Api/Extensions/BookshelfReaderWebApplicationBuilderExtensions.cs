using BookshelfReader.Api.Endpoints;
using BookshelfReader.Api.RateLimiting;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions;
using BookshelfReader.Extensions.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Api.Extensions;

public static class BookshelfReaderWebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddBookshelfReaderHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddBookshelfReader(builder.Configuration);
        builder.Services.AddBookshelfReaderRateLimiting(builder.Configuration);

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
            })
            .AddBookshelfReaderApiKey();
        builder.Services.AddAuthorization();

        builder.Services.AddBookshelfReaderApi();

        return builder;
    }

    public static WebApplication UseBookshelfReaderHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(SetSecurityHeadersAsync);

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Services.GetRequiredService<IOptions<ParseRateLimitOptions>>().Value.Enabled)
        {
            app.UseRateLimiter();
        }

        app.MapBookshelfReaderApi();

        return app;
    }

    private static Task SetSecurityHeadersAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });

        return next(context);
    }
}
