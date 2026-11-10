using BookshelfReader.Core.Options;
using Xunit;

namespace BookshelfReader.Tests.Options;

public class UploadsOptionsTests
{
    [Fact]
    public void IsSupportedContentType_ReturnsFalse_ForUnsupportedImage()
    {
        Assert.False(UploadsOptions.IsSupportedContentType("image/gif"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetCanonicalContentType_ReturnsFalse_ForMissingValues(string? input)
    {
        Assert.False(UploadContentTypeHelper.TryGetCanonicalContentType(input, out string? canonical));
        Assert.Empty(canonical);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/jpg")]
    [InlineData("image/pjpeg")]
    public void IsSupportedContentType_AcceptsCommonJpegAliases(string alias)
    {
        Assert.True(UploadsOptions.IsSupportedContentType(alias));
    }

    [Fact]
    public void AllowedContentTypes_FiltersUnsupportedEntries()
    {
        var options = new UploadsOptions
        {
            AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/gif",
                "image/jpeg"
            }
        };

        Assert.Equal("image/jpeg", Assert.Single(options.AllowedContentTypes));
    }

    [Fact]
    public void AllowedContentTypes_NormalizesAliases()
    {
        var options = new UploadsOptions
        {
            AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpg",
                "image/pjpeg"
            }
        };

        Assert.Equal("image/jpeg", Assert.Single(options.AllowedContentTypes));
    }

    [Fact]
    public void TryGetImageSignature_ReturnsSameSignature_ForAliases()
    {
        Assert.True(UploadContentTypeHelper.TryGetImageSignature("image/jpg", out ReadOnlyMemory<byte> aliasSignature));
        Assert.True(UploadContentTypeHelper.TryGetImageSignature("image/jpeg", out ReadOnlyMemory<byte> canonicalSignature));

        Assert.Equal(canonicalSignature.ToArray(), aliasSignature.ToArray());
    }
}
