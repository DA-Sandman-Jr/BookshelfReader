using BookshelfReader.Core.Abstractions;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Options;
using BookshelfReader.Infrastructure.Enrichment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace BookshelfReader.Tests.Enrichment;

public class BookEnrichmentServiceTests
{
    [Fact]
    public async Task EnrichAsync_AttachesBestFuzzyMatch_NotFirstResult()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata { Title = "History of the Hobbit", Author = "John D. Rateliff" },
            new BookMetadata { Title = "The Hobbit", Author = "J.R.R. Tolkien" }
        }));

        var candidate = new BookCandidate { Title = "The Hobbit" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.NotNull(candidate.Metadata);
        Assert.Equal("The Hobbit", candidate.Metadata.Title);
        Assert.Single(candidate.Notes, note => note.StartsWith("Catalog match score", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnrichAsync_UsesAuthorToBreakTitleTies()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata { Title = "Dune", Author = "Brian Herbert" },
            new BookMetadata { Title = "Dune", Author = "Frank Herbert" }
        }));

        var candidate = new BookCandidate { Title = "Dune", Author = "Frank Herbert" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.NotNull(candidate.Metadata);
        Assert.Equal("Frank Herbert", candidate.Metadata.Author);
    }

    [Fact]
    public async Task EnrichAsync_ConcatenatedTitleAuthor_PrefersCombinedMatch()
    {
        // Single-line spines parse as "title author" in one string; the real
        // novel must beat derivative works whose titles embed the author name.
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata { Title = "Frank Herbert's Dune", Author = "Kara Kennedy" },
            new BookMetadata { Title = "Dune", Author = "Frank Herbert" }
        }));

        var candidate = new BookCandidate { Title = "Dune Frank Herbert" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.NotNull(candidate.Metadata);
        Assert.Equal("Dune", candidate.Metadata.Title);
        Assert.Equal("Frank Herbert", candidate.Metadata.Author);
    }

    [Fact]
    public async Task EnrichAsync_BelowThreshold_AddsNoteWithoutMetadata()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata { Title = "Quantum Mechanics Primer" }
        }));

        var candidate = new BookCandidate { Title = "Zzgh Blorp Vex" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.Null(candidate.Metadata);
        Assert.Single(candidate.Notes, note => note.StartsWith("No confident catalog match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnrichAsync_MapsSubjectsToGenres_DroppingGenericFiction()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata
            {
                Title = "Dune",
                Subjects = new List<string> { "American science fiction", "Fiction", "Space opera" }
            }
        }));

        var candidate = new BookCandidate { Title = "Dune" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.Contains("Science Fiction", candidate.Genres, StringComparer.Ordinal);
        Assert.DoesNotContain("Fiction", candidate.Genres, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnrichAsync_MatchesSubjectsOnWordBoundaries()
    {
        // Real Open Library subjects: hyphenated forms must map to the specific
        // genre, and embedded fragments ("Psychohistory") must not match at all.
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(new[]
        {
            new BookMetadata
            {
                Title = "Foundation",
                Subjects = new List<string> { "Science-fiction", "Psychohistory", "Fiction" }
            }
        }));

        var candidate = new BookCandidate { Title = "Foundation" };
        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.Equal("Science Fiction", Assert.Single(candidate.Genres));
    }

    [Fact]
    public async Task EnrichAsync_WhenDisabled_SkipsLookups()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(Array.Empty<BookMetadata>()));
        var candidate = new BookCandidate { Title = "Dune" };

        await CreateService(lookup, new EnrichmentOptions { Enabled = false }).EnrichAsync(new[] { candidate });

        Assert.Equal(0, lookup.CallCount);
        Assert.Null(candidate.Metadata);
        Assert.Empty(candidate.Notes);
    }

    [Fact]
    public async Task EnrichAsync_SkipsCandidatesWithoutTitle()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Success(Array.Empty<BookMetadata>()));
        var candidate = new BookCandidate { Title = string.Empty, RawText = "unreadable" };

        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task EnrichAsync_LookupFailure_AddsNote()
    {
        var lookup = new FakeLookupService(_ => BookLookupResult.Failure("offline"));
        var candidate = new BookCandidate { Title = "Dune" };

        await CreateService(lookup).EnrichAsync(new[] { candidate });

        Assert.Null(candidate.Metadata);
        Assert.Single(candidate.Notes, note => note == "Catalog lookup unavailable");
    }

    [Fact]
    public async Task EnrichAsync_LookupThrows_AddsNoteAndContinues()
    {
        var lookup = new FakeLookupService(_ => throw new InvalidOperationException("boom"));
        BookCandidate[] candidates = new[]
        {
            new BookCandidate { Title = "Dune" },
            new BookCandidate { Title = "Foundation" }
        };

        await CreateService(lookup).EnrichAsync(candidates);

        Assert.All(candidates, candidate =>
        {
            Assert.Null(candidate.Metadata);
            Assert.Single(candidate.Notes, note => note == "Catalog lookup failed");
        });
    }

    [Fact]
    public async Task EnrichAsync_RespectsConcurrencyCap()
    {
        var lookup = new FakeLookupService(
            _ => BookLookupResult.Success(Array.Empty<BookMetadata>()),
            delay: TimeSpan.FromMilliseconds(50));

        BookCandidate[] candidates = Enumerable.Range(0, 8)
            .Select(i => new BookCandidate { Title = $"Book {i}" })
            .ToArray();

        await CreateService(lookup, new EnrichmentOptions { MaxConcurrentLookups = 2 }).EnrichAsync(candidates);

        Assert.Equal(8, lookup.CallCount);
        Assert.True(lookup.MaxObservedConcurrency <= 2);
    }

    private static BookEnrichmentService CreateService(IBookLookupService lookup, EnrichmentOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(lookup);
        ServiceProvider provider = services.BuildServiceProvider();

        return new BookEnrichmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            MsOptions.Create(options ?? new EnrichmentOptions()),
            NullLogger<BookEnrichmentService>.Instance);
    }

    private sealed class FakeLookupService : IBookLookupService
    {
        private readonly Func<string, BookLookupResult> _responder;
        private readonly TimeSpan _delay;
        private int _active;
        private int _callCount;
        private int _maxObservedConcurrency;

        public FakeLookupService(Func<string, BookLookupResult> responder, TimeSpan delay = default)
        {
            _responder = responder;
            _delay = delay;
        }

        public int CallCount => _callCount;

        public int MaxObservedConcurrency => _maxObservedConcurrency;

        public async Task<BookLookupResult> LookupAsync(string query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            int active = Interlocked.Increment(ref _active);
            UpdateMax(active);

            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                return _responder(query);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMax(int observed)
        {
            int current;
            while (observed > (current = Volatile.Read(ref _maxObservedConcurrency)))
            {
                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, observed, current) == current)
                {
                    break;
                }
            }
        }
    }
}
