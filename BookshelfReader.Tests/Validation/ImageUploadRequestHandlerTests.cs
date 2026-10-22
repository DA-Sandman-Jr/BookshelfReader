using BookshelfReader.Api.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace BookshelfReader.Tests.Validation;

public class ImageUploadRequestHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_WhenStreamExceedsReportedLength_RejectsAndDisposesStream(bool seekable)
    {
        using var stream = new TrackingStream(new byte[64], seekable);
        var validator = new StubValidator(new StreamFile(stream));
        var handler = new ImageUploadRequestHandler(validator);

        UploadProcessingResult result = await handler.PrepareAsync(new DefaultHttpContext().Request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Problem.Should().NotBeNull();
        stream.Disposed.Should().BeTrue();
        validator.SignatureChecked.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_WhenValidationThrows_DisposesStream()
    {
        using var stream = new TrackingStream(new byte[8], seekable: true);
        var handler = new ImageUploadRequestHandler(new StubValidator(new StreamFile(stream), cancelValidation: true));

        Func<Task> action = () => handler.PrepareAsync(new DefaultHttpContext().Request, CancellationToken.None);

        await action.Should().ThrowAsync<OperationCanceledException>();
        stream.Disposed.Should().BeTrue();
    }

    private sealed class TrackingStream(byte[] bytes, bool seekable) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override bool CanSeek => seekable;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StreamFile(Stream stream) : IFormFile
    {
        public string ContentType => "image/png";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => 8;
        public string Name => "image";
        public string FileName => "shelf.png";

        public Stream OpenReadStream() => stream;

        public void CopyTo(Stream target) => stream.CopyTo(target);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => stream.CopyToAsync(target, cancellationToken);
    }

    private sealed class StubValidator(IFormFile file, bool cancelValidation = false) : IImageUploadValidator
    {
        public bool SignatureChecked { get; private set; }

        public Task<UploadValidationResult> ValidateImageUploadAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(UploadValidationResult.Success(file, "image/png", maxBytes: 8));
        }

        public Task<ValidationProblem?> ValidateImageSignatureAsync(Stream imageStream, string canonicalContentType, CancellationToken cancellationToken)
        {
            SignatureChecked = true;
            if (cancelValidation)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult<ValidationProblem?>(null);
        }

        public ValidationProblem? ValidateImageMetadata(Stream imageStream) => null;
    }
}
