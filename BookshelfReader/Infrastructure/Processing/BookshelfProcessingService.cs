using System.Diagnostics;
using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookshelfReader.Infrastructure.Processing;

public sealed partial class BookshelfProcessingService : IBookshelfProcessingService
{
    private readonly IVisionBookReader _visionBookReader;
    private readonly IGenreClassifier _genreClassifier;
    private readonly IBookEnrichmentService _enrichmentService;
    private readonly ClaudeVisionOptions _visionOptions;
    private readonly ILogger<BookshelfProcessingService> _logger;

    public BookshelfProcessingService(
        IVisionBookReader visionBookReader,
        IGenreClassifier genreClassifier,
        IBookEnrichmentService enrichmentService,
        IOptions<ClaudeVisionOptions> visionOptions,
        ILogger<BookshelfProcessingService> logger)
    {
        _visionBookReader = visionBookReader;
        _genreClassifier = genreClassifier;
        _enrichmentService = enrichmentService;
        _visionOptions = visionOptions.Value;
        _logger = logger;
    }

    public async Task<ParseResult> ProcessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var imageId = Guid.NewGuid();

        byte[] imageData = await ReadAllBytesAsync(imageStream, cancellationToken).ConfigureAwait(false);

        VisionReadResult visionResult = await _visionBookReader.ReadAsync(imageData, cancellationToken).ConfigureAwait(false);
        LogBooksIdentified(_logger, visionResult.Books.Count, imageId);

        var books = visionResult.Books.Select(CreateCandidate).ToList();

        if (books.Count > 0)
        {
            await _enrichmentService.EnrichAsync(books, cancellationToken).ConfigureAwait(false);
            int enriched = books.Count(b => b.Metadata is not null);
            LogCandidatesEnriched(_logger, enriched, books.Count, imageId);
        }

        stopwatch.Stop();

        var diagnostics = new DiagnosticsBuilder(books.Count);
        diagnostics.AddNotes(visionResult.Notes);
        diagnostics.SetElapsed(stopwatch.ElapsedMilliseconds);

        return new ParseResult
        {
            ImageId = imageId,
            Books = books,
            Diagnostics = diagnostics.Build()
        };
    }

    private BookCandidate CreateCandidate(VisionBookEntry entry)
    {
        string rawText = string.IsNullOrWhiteSpace(entry.Author)
            ? entry.Title
            : $"{entry.Title} — {entry.Author}";

        var candidate = new BookCandidate
        {
            Title = entry.Title,
            Author = entry.Author,
            Confidence = entry.Confidence,
            RawText = rawText
        };

        candidate.Genres.AddRange(_genreClassifier.Classify(candidate.Title, candidate.RawText));
        candidate.Notes.Add($"Read by vision model {_visionOptions.Model}");

        return candidate;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Vision model identified {Count} book(s) for image {ImageId}")]
    private static partial void LogBooksIdentified(ILogger logger, int count, Guid imageId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Enriched {Enriched}/{Total} candidates for image {ImageId}")]
    private static partial void LogCandidatesEnriched(ILogger logger, int enriched, int total, Guid imageId);
}
