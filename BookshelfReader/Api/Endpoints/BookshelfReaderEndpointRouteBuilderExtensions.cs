using BookshelfReader.Api.RateLimiting;
using BookshelfReader.Api.Validation;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Extensions.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Api.Endpoints;

public static class BookshelfReaderEndpointRouteBuilderExtensions
{
    private const string ApiBasePath = "/api";
    private const string BooksTag = "Books";
    private const string BookshelfTag = "Bookshelf";

    public static RouteGroupBuilder MapBookshelfReaderApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder apiGroup = app.MapGroup(ApiBasePath);

        apiGroup.MapGet("/books/lookup", LookupAsync)
            .WithName("LookupBooks")
            .WithTags(BooksTag)
            .Produces<IEnumerable<BookMetadata>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesValidationProblem();

        ApiKeyAuthenticationOptions apiKeyOptions = app.ServiceProvider.GetRequiredService<IOptions<ApiKeyAuthenticationOptions>>().Value;

        RouteHandlerBuilder parseEndpoint = apiGroup.MapPost("/bookshelf/parse", ParseAsync)
            .Accepts<IFormFile>("multipart/form-data")
            .WithName("ParseBookshelf")
            .WithTags(BookshelfTag)
            .Produces<ParseResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem();

        if (apiKeyOptions.RequireApiKey)
        {
            parseEndpoint.RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = ApiKeyAuthenticationDefaults.AuthenticationScheme
            });
        }

        ParseRateLimitOptions rateLimitOptions = app.ServiceProvider
            .GetRequiredService<IOptions<ParseRateLimitOptions>>().Value;
        if (rateLimitOptions.Enabled)
        {
            parseEndpoint
                .RequireRateLimiting(BookshelfReaderRateLimitingExtensions.ParsePolicyName)
                .Produces(StatusCodes.Status429TooManyRequests);
        }

        return apiGroup;
    }

    private static async Task<Results<Ok<IEnumerable<BookMetadata>>, ProblemHttpResult, ValidationProblem>> LookupAsync(
        string name,
        IBookLookupService lookupService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CreateValidationProblem("name", "Query 'name' is required.");
        }

        BookLookupResult result = await lookupService.LookupAsync(name, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return TypedResults.Problem(
                title: "Book lookup failed",
                detail: result.ErrorMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Ok(result.Books);
    }

    private static async Task<Results<Ok<ParseResult>, ValidationProblem>> ParseAsync(
        HttpRequest request,
        IBookshelfProcessingService processor,
        IImageUploadRequestHandler uploadRequestHandler,
        CancellationToken cancellationToken)
    {
        UploadProcessingResult preparationResult = await uploadRequestHandler.PrepareAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!preparationResult.IsSuccess)
        {
            return preparationResult.Problem!;
        }

        await using Stream imageStream = preparationResult.Upload!.Stream;

        try
        {
            ParseResult result = await processor.ProcessAsync(imageStream, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return CreateValidationProblem("image", ex.Message);
        }
    }

    private static ValidationProblem CreateValidationProblem(string key, string message)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal) { [key] = new[] { message } };
        return TypedResults.ValidationProblem(errors);
    }
}
