using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using FuzzySharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Infrastructure.Enrichment;

public sealed partial class BookEnrichmentService : IBookEnrichmentService
{
    private const int MaxGenres = 4;

    // Ordered most-specific first; the first key found in a subject wins, so
    // "science fiction" matches before "science" or "fiction". Keys are matched
    // on whole-word boundaries after punctuation is normalized to spaces, so
    // "Science-fiction" maps here but "Psychohistory" does not match "history".
    private static readonly (string Keyword, string Genre)[] SubjectGenreMap =
    {
        ("science fiction", "Science Fiction"),
        ("juvenile fiction", "Children's"),
        ("young adult", "Young Adult"),
        ("children", "Children's"),
        ("fantasy", "Fantasy"),
        ("mystery", "Mystery"),
        ("detective", "Mystery"),
        ("thriller", "Thriller"),
        ("suspense", "Thriller"),
        ("horror", "Horror"),
        ("romance", "Romance"),
        ("poetry", "Poetry"),
        ("autobiography", "Biography"),
        ("biography", "Biography"),
        ("history", "History"),
        ("cooking", "Cooking"),
        ("self help", "Self-Help"),
        ("philosophy", "Philosophy"),
        ("science", "Science"),
        ("fiction", "Fiction")
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EnrichmentOptions _options;
    private readonly ILogger<BookEnrichmentService> _logger;

    public BookEnrichmentService(
        IServiceScopeFactory scopeFactory,
        IOptions<EnrichmentOptions> options,
        ILogger<BookEnrichmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnrichAsync(IReadOnlyList<BookCandidate> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (!_options.Enabled)
        {
            return;
        }

        var enrichable = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .ToList();

        if (enrichable.Count == 0)
        {
            return;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IBookLookupService lookupService = scope.ServiceProvider.GetRequiredService<IBookLookupService>();
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrentLookups, _options.MaxConcurrentLookups);

        await Task.WhenAll(enrichable.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnrichCandidateAsync(candidate, lookupService, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        })).ConfigureAwait(false);
    }

    private async Task EnrichCandidateAsync(
        BookCandidate candidate,
        IBookLookupService lookupService,
        CancellationToken cancellationToken)
    {
        try
        {
            BookLookupResult result = await lookupService.LookupAsync(candidate.Title, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                candidate.Notes.Add("Catalog lookup unavailable");
                return;
            }

            (BookMetadata Metadata, int Score)? best = SelectBestMatch(candidate, result.Books);

            if (best is null)
            {
                candidate.Notes.Add("No catalog results for parsed title");
                return;
            }

            if (best.Value.Score < _options.MinMatchScore)
            {
                candidate.Notes.Add($"No confident catalog match (best score {best.Value.Score})");
                return;
            }

            candidate.Metadata = best.Value.Metadata;
            candidate.Notes.Add($"Catalog match score {best.Value.Score}");
            MergeGenres(candidate, best.Value.Metadata.Subjects);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEnrichmentFailed(_logger, candidate.Title, ex);
            candidate.Notes.Add("Catalog lookup failed");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Enrichment failed for candidate title '{Title}'")]
    private static partial void LogEnrichmentFailed(ILogger logger, string title, Exception ex);

    private static (BookMetadata Metadata, int Score)? SelectBestMatch(
        BookCandidate candidate,
        IEnumerable<BookMetadata> results)
    {
        (BookMetadata Metadata, int Score)? best = null;

        foreach (BookMetadata metadata in results)
        {
            if (string.IsNullOrWhiteSpace(metadata.Title))
            {
                continue;
            }

            int titleScore = Fuzz.WeightedRatio(candidate.Title, metadata.Title);
            int score = titleScore;

            if (!string.IsNullOrWhiteSpace(candidate.Author) && !string.IsNullOrWhiteSpace(metadata.Author))
            {
                int authorScore = Fuzz.WeightedRatio(candidate.Author, metadata.Author);
                score = (int)Math.Round((titleScore * 0.75) + (authorScore * 0.25));
            }

            // Parsed spine titles often concatenate title and author, so also
            // compare against the catalog's combined form and keep the better fit.
            if (!string.IsNullOrWhiteSpace(metadata.Author))
            {
                int combinedScore = Fuzz.WeightedRatio(candidate.Title, $"{metadata.Title} {metadata.Author}");
                score = Math.Max(score, combinedScore);
            }

            if (best is null || score > best.Value.Score)
            {
                best = (metadata, score);
            }
        }

        return best;
    }

    private static void MergeGenres(BookCandidate candidate, IReadOnlyList<string> subjects)
    {
        var mapped = new List<string>();

        foreach (string subject in subjects)
        {
            string normalized = NormalizeSubject(subject);

            foreach ((string keyword, string genre) in SubjectGenreMap)
            {
                if (normalized.Contains($" {keyword} ", StringComparison.Ordinal))
                {
                    mapped.Add(genre);
                    break;
                }
            }
        }

        // Generic "Fiction" adds nothing once a more specific genre matched.
        if (mapped.Count > 1)
        {
            mapped.RemoveAll(genre => string.Equals(genre, "Fiction", StringComparison.OrdinalIgnoreCase));
        }

        foreach (string genre in mapped.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (candidate.Genres.Count >= MaxGenres)
            {
                break;
            }

            if (!candidate.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            {
                candidate.Genres.Add(genre);
            }
        }
    }

    private static string NormalizeSubject(string subject)
    {
        char[] buffer = new char[subject.Length + 2];
        buffer[0] = ' ';

        for (int i = 0; i < subject.Length; i++)
        {
            char c = subject[i];
            buffer[i + 1] = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ';
        }

        buffer[^1] = ' ';
        return new string(buffer);
    }
}
