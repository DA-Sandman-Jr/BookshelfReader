using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.Genres;
using BookshelfReader.Infrastructure.Processing;
using FluentAssertions;
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

        result.Books.Should().HaveCount(2);

        result.Books[0].Title.Should().Be("Dune");
        result.Books[0].Author.Should().Be("Frank Herbert");
        result.Books[0].Confidence.Should().Be(0.95);
        result.Books[0].RawText.Should().Be("Dune — Frank Herbert");
        result.Books[0].BoundingBox.IsEmpty.Should().BeTrue();
        result.Books[0].Notes.Should().ContainSingle(note => note.Contains("claude-haiku-4-5"));

        result.Books[1].Title.Should().Be("Foundation");
        result.Books[1].Author.Should().BeEmpty();
        result.Books[1].RawText.Should().Be("Foundation");

        result.Diagnostics.SegmentCount.Should().Be(2);

        enrichmentService.Received.Should().NotBeNull();
        enrichmentService.Received.Should().HaveCount(2);
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

        result.Books[0].Notes.Should().Contain("Read by vision model selected-provider-model");
        result.Books[0].Notes.Should().NotContain(note => note.Contains("claude-haiku"));
    }

    [Fact]
    public async Task ProcessAsync_WhenNoBooksFound_SkipsEnrichmentAndReturnsEmptyResult()
    {
        var visionReader = new StubVisionBookReader(new VisionReadResult());
        var enrichmentService = new RecordingEnrichmentService();
        BookshelfProcessingService service = CreateService(visionReader, enrichmentService);

        using var stream = new MemoryStream();
        ParseResult result = await service.ProcessAsync(stream);

        result.Books.Should().BeEmpty();
        result.Diagnostics.SegmentCount.Should().Be(0);
        enrichmentService.Received.Should().BeNull();
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

        result.Diagnostics.Notes.Should().ContainSingle(note => note.Contains("truncated"));
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
