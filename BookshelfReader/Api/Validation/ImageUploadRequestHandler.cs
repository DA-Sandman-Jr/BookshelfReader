using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BookshelfReader.Api.Validation;

public sealed class ImageUploadRequestHandler : IImageUploadRequestHandler
{
    private readonly IImageUploadValidator _validator;

    public ImageUploadRequestHandler(IImageUploadValidator validator)
    {
        _validator = validator;
    }

    public async Task<UploadProcessingResult> PrepareAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        UploadValidationResult uploadValidation = await _validator.ValidateImageUploadAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!uploadValidation.IsSuccess)
        {
            return new UploadProcessingResult(null, uploadValidation.Problem);
        }

        IFormFile file = uploadValidation.File!;
        string canonicalContentType = uploadValidation.CanonicalContentType!;

        Stream? seekableStream = await CreateSeekableStreamAsync(file, uploadValidation.MaxBytes, cancellationToken)
            .ConfigureAwait(false);
        if (seekableStream is null)
        {
            return new UploadProcessingResult(null,
                ImageUploadValidator.CreateValidationProblem("image", "Uploaded image exceeds the configured byte limit."));
        }

        bool transferred = false;
        try
        {
            ValidationProblem? signatureValidation = await _validator.ValidateImageSignatureAsync(
                    seekableStream,
                    canonicalContentType,
                    cancellationToken)
                .ConfigureAwait(false);
            if (signatureValidation is not null)
            {
                return new UploadProcessingResult(null, signatureValidation);
            }

            ValidationProblem? metadataValidation = _validator.ValidateImageMetadata(seekableStream);
            if (metadataValidation is not null)
            {
                return new UploadProcessingResult(null, metadataValidation);
            }

            var upload = new ValidatedImageUpload(seekableStream, canonicalContentType, uploadValidation.MaxBytes);
            transferred = true;
            return new UploadProcessingResult(upload, null);
        }
        finally
        {
            if (!transferred)
            {
                await seekableStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<Stream?> CreateSeekableStreamAsync(IFormFile file, long maxBytes, CancellationToken cancellationToken)
    {
        Stream stream = file.OpenReadStream();

        if (stream.CanSeek)
        {
            if (stream.Length > maxBytes)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            stream.Position = 0;
            return stream;
        }

        var buffer = new MemoryStream();
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                byte[] chunk = new byte[81920];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(
                    chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length + 1)), cancellationToken)
                    .ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + bytesRead > maxBytes)
                    {
                        await buffer.DisposeAsync().ConfigureAwait(false);
                        return null;
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
