using BookshelfReader.Infrastructure.Genres;
using Xunit;

namespace BookshelfReader.Tests.Genres;

public class KeywordGenreClassifierTests
{
    private readonly KeywordGenreClassifier _classifier = new();

    [Fact]
    public void Classify_ReturnsEmpty_WhenNoKeywordsMatch()
    {
        IReadOnlyList<string> result = _classifier.Classify("The Old Man and the Sea", "The Old Man and the Sea");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("dragon", "Fantasy")]
    [InlineData("wizard", "Fantasy")]
    [InlineData("spaceship", "Science Fiction")]
    [InlineData("galaxy", "Science Fiction")]
    [InlineData("murder", "Mystery")]
    [InlineData("detective", "Mystery")]
    [InlineData("love", "Romance")]
    [InlineData("recipe", "Cooking")]
    [InlineData("history", "History")]
    [InlineData("biography", "Biography")]
    public void Classify_MatchesKeyword_WhenExactWordPresent(string keyword, string expectedGenre)
    {
        IReadOnlyList<string> result = _classifier.Classify(keyword, string.Empty);

        Assert.Equal(expectedGenre, Assert.Single(result));
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        IReadOnlyList<string> result = _classifier.Classify("DRAGON", string.Empty);

        Assert.Equal("Fantasy", Assert.Single(result));
    }

    [Fact]
    public void Classify_DoesNotMatchSubstrings()
    {
        // "love" inside "beloved" should not trigger Romance
        IReadOnlyList<string> result = _classifier.Classify("The Beloved", "beloved gloves loveable");

        Assert.Empty(result);
    }

    [Fact]
    public void Classify_MatchesKeywordInRawText_WhenNotInTitle()
    {
        IReadOnlyList<string> result = _classifier.Classify("A Dark Night", "The detective investigated the scene");

        Assert.Equal("Mystery", Assert.Single(result));
    }

    [Fact]
    public void Classify_ReturnsMultipleGenres_WhenMultipleKeywordsPresent()
    {
        IReadOnlyList<string> result = _classifier.Classify("Love and Murder", string.Empty);

        Assert.Equal(2, result.Count);
        Assert.Contains("Romance", result, StringComparer.Ordinal);
        Assert.Contains("Mystery", result, StringComparer.Ordinal);
    }

    [Fact]
    public void Classify_ReturnsEmpty_WhenBothInputsAreEmpty()
    {
        IReadOnlyList<string> result = _classifier.Classify(string.Empty, string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Classify_ReturnsEmpty_WhenBothInputsAreWhitespace()
    {
        IReadOnlyList<string> result = _classifier.Classify("   ", "\t\n");

        Assert.Empty(result);
    }

    [Fact]
    public void Classify_DoesNotDuplicateGenres_WhenKeywordAppearsMultipleTimes()
    {
        IReadOnlyList<string> result = _classifier.Classify("Dragon", "dragon Dragon DRAGON");

        Assert.Equal("Fantasy", Assert.Single(result));
    }
}
