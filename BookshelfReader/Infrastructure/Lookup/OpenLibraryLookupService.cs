using System.Net.Http.Json;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Models.External;
using Microsoft.Extensions.Logging;

namespace BookshelfReader.Infrastructure.Lookup;

public sealed partial class OpenLibraryLookupService : IBookLookupService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenLibraryLookupService> _logger;

    public OpenLibraryLookupService(HttpClient httpClient, ILogger<OpenLibraryLookupService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private const int MaxRetries = 3;
    private const int MaxResults = 5;
    private const int MaxSubjects = 10;
    private const string RequestedFields = "title,author_name,first_publish_year,isbn,cover_i,subject";

    public async Task<BookLookupResult> LookupAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BookLookupResult.Success(Array.Empty<BookMetadata>());
        }

        // General-purpose search rather than the title-only field: parsed spine
        // text frequently concatenates title and author ("Dune Frank Herbert"),
        // which the title index misses but relevance search handles well.
        string requestUri = $"search.json?q={Uri.EscapeDataString(query)}&fields={RequestedFields}&limit={MaxResults}";

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                LogRetryingLookup(_logger, attempt + 1, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                OpenLibrarySearchResult? response = await _httpClient.GetFromJsonAsync<OpenLibrarySearchResult>(requestUri, cancellationToken).ConfigureAwait(false);

                if (response?.Docs is null || response.Docs.Count == 0)
                {
                    return BookLookupResult.Success(Array.Empty<BookMetadata>());
                }

                // Preserve Open Library's relevance ordering; re-sorting by publish
                // year buries the canonical edition behind newer derivative works.
                BookMetadata[] books = response.Docs
                    .Take(MaxResults)
                    .Select(d => new BookMetadata
                    {
                        Title = d.Title ?? string.Empty,
                        Author = d.AuthorName?.FirstOrDefault() ?? string.Empty,
                        PublishYear = d.FirstPublishYear,
                        Isbn = d.Isbn?.FirstOrDefault(),
                        CoverUrl = d.CoverId.HasValue
                            ? $"https://covers.openlibrary.org/b/id/{d.CoverId}-M.jpg"
                            : null,
                        Subjects = d.Subject?.Take(MaxSubjects).ToList() ?? new List<string>()
                    })
                    .ToArray();

                return BookLookupResult.Success(books);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogLookupFailed(_logger, query, attempt + 1, MaxRetries, ex);
            }
        }

        return BookLookupResult.Failure("Unable to reach the book lookup service. Please try again later.");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Retrying Open Library lookup (attempt {Attempt}/{Max}) after {Delay}s")]
    private static partial void LogRetryingLookup(ILogger logger, int attempt, int max, double delay);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Open Library lookup failed for query '{Query}' (attempt {Attempt}/{Max})")]
    private static partial void LogLookupFailed(ILogger logger, string query, int attempt, int max, Exception ex);
}
