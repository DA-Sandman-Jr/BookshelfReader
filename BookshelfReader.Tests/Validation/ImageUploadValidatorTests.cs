using System.Buffers.Binary;
using System.Text;
using BookshelfReader.Api.Validation;
using BookshelfReader.Core.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenCvSharp;
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

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public void ValidateImageMetadata_WhenDimensionsAreWithinLimit_AcceptsImage(string extension)
    {
        using var image = new Mat(3, 4, MatType.CV_8UC3, Scalar.White);
        using var stream = new MemoryStream(image.ImEncode(extension));

        ValidationProblem? result = CreateValidator(new UploadsOptions { MaxImagePixels = 12 })
            .ValidateImageMetadata(stream);

        result.Should().BeNull();
        stream.Position.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ValidateImageMetadata_WhenPngDimensionsAreInvalid_RejectsImage(int width, int height)
    {
        byte[] bytes = CreateValidImageBytes();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        using var stream = new MemoryStream(bytes);

        CreateValidator().ValidateImageMetadata(stream).Should().NotBeNull();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void ValidateImageMetadata_WhenPngHeaderDeclaresHugeChunk_RejectsWithoutAllocatingChunk()
    {
        byte[] bytes = CreateValidImageBytes();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), int.MaxValue);
        using var stream = new MemoryStream(bytes);

        CreateValidator().ValidateImageMetadata(stream).Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateImageSignatureAsync_WhenStreamReturnsShortReads_AcceptsCompleteSignature()
    {
        using var stream = new ShortReadStream(CreateValidImageBytes());

        ValidationProblem? result = await CreateValidator()
            .ValidateImageSignatureAsync(stream, "image/png", CancellationToken.None);

        result.Should().BeNull();
        stream.Position.Should().Be(0);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }
    }

    [Fact]
    public async Task ValidateImageUploadAsync_WhenChunkedBodyExceedsAggregateLimit_StopsReading()
    {
        using var multipart = new MultipartFormDataContent();
        for (int i = 0; i < 80; i++)
        {
            multipart.Add(new ByteArrayContent(new byte[1024]), "image", "shelf.png");
        }

        using var stream = new ForwardReadStream(await multipart.ReadAsByteArrayAsync());
        var context = new DefaultHttpContext();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Body = stream;
        context.Request.ContentLength = null;

        UploadValidationResult result = await CreateValidator(new UploadsOptions { MaxBytes = 1024 })
            .ValidateImageUploadAsync(context.Request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Problem!.ProblemDetails.Errors["image"].Should().Contain(message => message.Contains("request limit"));
        stream.Position.Should().BeLessThan(stream.Length);
        await context.Response.CompleteAsync();
    }

    private sealed class ForwardReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
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
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        return image.ImEncode(".png");
    }
}
