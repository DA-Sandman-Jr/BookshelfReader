using System.Text;
using BookshelfReader.Api.Validation;
using BookshelfReader.Core.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace BookshelfReader.Tests.Validation;

public class ImageUploadValidatorTests
{
    private static ImageUploadValidator CreateValidator(UploadsOptions? uploadsOptions = null)
    {
        return new ImageUploadValidator(OptionsFactory.Create(uploadsOptions ?? new UploadsOptions()));
    }

    [Fact]
    public async Task ValidateImageUploadAsync_WhenContentTypeUnsupported_ReturnsFailure()
    {
        ImageUploadValidator validator = CreateValidator();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "text/plain";

        UploadValidationResult result = await validator.ValidateImageUploadAsync(context.Request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Problem.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateImageUploadAsync_WhenImageMissing_ReturnsFailure()
    {
        ImageUploadValidator validator = CreateValidator();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data";
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.Ordinal));

        UploadValidationResult result = await validator.ValidateImageUploadAsync(context.Request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Problem.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateImageUploadAsync_WhenValidImage_ReturnsSuccessWithCanonicalType()
    {
        ImageUploadValidator validator = CreateValidator();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data";

        FormFile file = CreateFormFile("image/jpg", CreateValidImageBytes());
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.Ordinal),
            new FormFileCollection { file });

        UploadValidationResult result = await validator.ValidateImageUploadAsync(context.Request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.CanonicalContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task ValidateImageSignatureAsync_WhenSignatureDoesNotMatch_ReturnsProblem()
    {
        ImageUploadValidator validator = CreateValidator();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not an image"));

        ValidationProblem? result = await validator.ValidateImageSignatureAsync(stream, "image/png", CancellationToken.None);

        result.Should().NotBeNull();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public async Task ValidateImageSignatureAsync_WhenSignatureMatches_ReturnsNull()
    {
        ImageUploadValidator validator = CreateValidator();
        await using var stream = new MemoryStream(CreateValidImageBytes());

        ValidationProblem? result = await validator.ValidateImageSignatureAsync(stream, "image/png", CancellationToken.None);

        result.Should().BeNull();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void ValidateImageMetadata_WhenPixelCountExceedsLimit_ReturnsProblem()
    {
        var uploadsOptions = new UploadsOptions { MaxImagePixels = 10 };
        ImageUploadValidator validator = CreateValidator(uploadsOptions);
        using var stream = new MemoryStream(CreateValidImageBytes(width: 4, height: 4));

        ValidationProblem? result = validator.ValidateImageMetadata(stream);

        result.Should().NotBeNull();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void ValidateImageMetadata_WhenImageUnreadable_ReturnsProblem()
    {
        ImageUploadValidator validator = CreateValidator();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not an image"));

        ValidationProblem? result = validator.ValidateImageMetadata(stream);

        result.Should().NotBeNull();
        stream.Position.Should().Be(0);
    }

    private static FormFile CreateFormFile(string contentType, byte[] data)
    {
        var stream = new MemoryStream(data);
        return new FormFile(stream, 0, data.Length, "image", "upload")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] CreateValidImageBytes(int width = 1, int height = 1)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }
}
