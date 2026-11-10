using System.Text.Json;
using BookshelfReader.Core.Models;
using BookshelfReader.Core.Models.External;
using BookshelfReader.Infrastructure.Lookup;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;
using Xunit;

namespace BookshelfReader.Tests.Lookup;

public class OpenLibraryLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_MapsTopResults()
    {
        var handler = new MockHttpMessageHandler();
        var response = new OpenLibrarySearchResult
        {
            Docs = new List<OpenLibraryDoc>
            {
                new() { Title = "Book A", AuthorName = new() { "Author A" }, FirstPublishYear = 2020, Isbn = new() { "123" }, CoverId = 42, Subject = new() { "Fantasy", "Fiction" } },
                new() { Title = "Book B", AuthorName = new() { "Author B" }, FirstPublishYear = 2018, Isbn = new() { "321" } }
            }
        };

        handler.When(HttpMethod.Get, "https://openlibrary.org/search.json")
            .WithQueryString("q", "Test")
            .WithQueryString("limit", "5")
            .WithQueryString("fields", "title,author_name,first_publish_year,isbn,cover_i,subject")
            .Respond("application/json", JsonSerializer.Serialize(response));

        var httpClient = handler.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://openlibrary.org/");
        var service = new OpenLibraryLookupService(httpClient, NullLogger<OpenLibraryLookupService>.Instance);

        BookLookupResult result = await service.LookupAsync("Test");
        var books = result.Books.ToList();

        Assert.Null(result.ErrorMessage);
        Assert.Equal(2, books.Count);
        Assert.Equal("Book A", books[0].Title);
        Assert.Equal("Author A", books[0].Author);
        Assert.Equal(2020, books[0].PublishYear);
        Assert.Equal("123", books[0].Isbn);
        Assert.Equal("https://covers.openlibrary.org/b/id/42-M.jpg", books[0].CoverUrl);
        Assert.Equal(new[] { "Fantasy", "Fiction" }, books[0].Subjects, StringComparer.Ordinal);
        Assert.Empty(books[1].Subjects);
    }

    [Fact]
    public async Task LookupAsync_PreservesCatalogRelevanceOrder()
    {
        var handler = new MockHttpMessageHandler();
        var response = new OpenLibrarySearchResult
        {
            Docs = new List<OpenLibraryDoc>
            {
                new() { Title = "Most Relevant", FirstPublishYear = 1937 },
                new() { Title = "Newer But Less Relevant", FirstPublishYear = 2023 }
            }
        };

        handler.When(HttpMethod.Get, "https://openlibrary.org/search.json*")
            .Respond("application/json", JsonSerializer.Serialize(response));

        var httpClient = handler.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://openlibrary.org/");
        var service = new OpenLibraryLookupService(httpClient, NullLogger<OpenLibraryLookupService>.Instance);

        BookLookupResult result = await service.LookupAsync("Test");
        var books = result.Books.ToList();

        Assert.Equal(new[] { "Most Relevant", "Newer But Less Relevant" }, books.Select(b => b.Title), StringComparer.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_WhenRequestFails_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://openlibrary.org/*")
            .Throw(new HttpRequestException("boom"));

        var httpClient = handler.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://openlibrary.org/");

        var service = new OpenLibraryLookupService(httpClient, NullLogger<OpenLibraryLookupService>.Instance);

        BookLookupResult result = await service.LookupAsync("Test");
        Assert.Empty(result.Books.ToList());
    }

    [Fact]
    public async Task LookupAsync_WithEmptyQuery_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = handler.ToHttpClient();
        httpClient.BaseAddress = new Uri("https://openlibrary.org/");

        var service = new OpenLibraryLookupService(httpClient, NullLogger<OpenLibraryLookupService>.Instance);

        BookLookupResult result = await service.LookupAsync(string.Empty);
        Assert.Empty(result.Books.ToList());
    }
}
