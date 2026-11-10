using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.Genres;
using BookshelfReader.Infrastructure.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookshelfReader.Tests.Processing;

public class BookshelfProcessingServiceTests
{
    [Fact]
    public async Task ProcessAsync_MapsVisionResultsToCandidatesAndEnrichesThem()
    {
        var visionReader = new StubVisionBookReader(new VisionReadResult
        {
            Books =
            {
                new VisionBookEntry("Dune", "Frank Herbert", 0.95),
                new VisionBookEntry("Foundation", "", 0.6)
            }
        });

        var enrichmentService = new RecordingEnrichmentService();
        BookshelfProcessingService service = CreateService(visionReader, enrichmentService);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        ParseResult result = await service.ProcessAsync(stream);

        Assert.Equal(2, result.Books.Count);

        Assert.Equal("Dune", result.Books[0].Title);
        Assert.Equal("Frank Herbert", result.Books[0].Author);
        Assert.Equal(0.95, result.Books[0].Confidence);
        Assert.Equal("Dune — Frank Herbert", result.Books[0].RawText);
        Assert.True(result.Books[0].BoundingBox.IsEmpty);
        Assert.Single(result.Books[0].Notes, note => note.Contains("claude-haiku-4-5", StringComparison.Ordinal));

        Assert.Equal("Foundation", result.Books[1].Title);
        Assert.Empty(result.Books[1].Author);
        Assert.Equal("Foundation", result.Books[1].RawText);

        Assert.Equal(2, result.Diagnostics.SegmentCount);

        Assert.NotNull(enrichmentService.Received);
        Assert.Equal(2, enrichmentService.Received.Count);
    }

    [Fact]
    public async Task ProcessAsync_ReportsSelectedModelInsteadOfClaudeDefault()
    {
        var visionReader = new StubVisionBookReader(new VisionReadResult
        {
            Model = "selected-provider-model",
            Books = { new VisionBookEntry("Dune", "Frank Herbert", 0.95) }
        });
        BookshelfProcessingService service = CreateService(visionReader, new RecordingEnrichmentService());
        using var stream = new MemoryStream();

        ParseResult result = await service.ProcessAsync(stream);

        Assert.Contains("Read by vision model selected-provider-model", result.Books[0].Notes, StringComparer.Ordinal);
        Assert.DoesNotContain(result.Books[0].Notes, note => note.Contains("claude-haiku", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_WhenNoBooksFound_SkipsEnrichmentAndReturnsEmptyResult()
    {
        var visionReader = new StubVisionBookReader(new VisionReadResult());
        var enrichmentService = new RecordingEnrichmentService();
        BookshelfProcessingService service = CreateService(visionReader, enrichmentService);

        using var stream = new MemoryStream();
        ParseResult result = await service.ProcessAsync(stream);

        Assert.Empty(result.Books);
        Assert.Equal(0, result.Diagnostics.SegmentCount);
        Assert.Null(enrichmentService.Received);
    }

    [Fact]
    public async Task ProcessAsync_IncludesVisionNotesInDiagnostics()
    {
        var visionReader = new StubVisionBookReader(new VisionReadResult
        {
            Notes = { "The vision model response was truncated at the configured token limit; some books may be missing." }
        });

        BookshelfProcessingService service = CreateService(visionReader, new RecordingEnrichmentService());

        using var stream = new MemoryStream();
        ParseResult result = await service.ProcessAsync(stream);

        Assert.Single(result.Diagnostics.Notes, note => note.Contains("truncated", StringComparison.Ordinal));
    }

    private static BookshelfProcessingService CreateService(
        IVisionBookReader visionBookReader,
        IBookEnrichmentService enrichmentService)
    {
        return new BookshelfProcessingService(
            visionBookReader,
            new KeywordGenreClassifier(),
            enrichmentService,
            Microsoft.Extensions.Options.Options.Create(new ClaudeVisionOptions()),
            NullLogger<BookshelfProcessingService>.Instance);
    }

    private sealed class StubVisionBookReader : IVisionBookReader
    {
        private readonly VisionReadResult _result;

        public StubVisionBookReader(VisionReadResult result)
        {
            _result = result;
        }

        public Task<VisionReadResult> ReadAsync(byte[] imageData, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class RecordingEnrichmentService : IBookEnrichmentService
    {
        public IReadOnlyList<BookCandidate>? Received { get; private set; }

        public Task EnrichAsync(IReadOnlyList<BookCandidate> candidates, CancellationToken cancellationToken = default)
        {
            Received = candidates;
            return Task.CompletedTask;
        }
    }
}
