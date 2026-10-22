using System.Buffers.Binary;
using BookshelfReader.Core.Options;
using MetadataExtractor;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;

namespace BookshelfReader.Api.Validation;

/// <summary>Reads dimensions without decoding pixels or compressed metadata.</summary>
internal static class ImageDimensionsReader
{
    public static (int Width, int Height)? Read(Stream stream)
    {
        int firstByte = stream.ReadByte();
        stream.Position = 0;
        if (firstByte == 0xFF)
        {
            JpegDirectory[] frames = JpegMetadataReader.ReadMetadata(stream,
                    new IJpegSegmentMetadataReader[] { new JpegReader() })
                .OfType<JpegDirectory>().ToArray();
            if (frames.Length == 1
                && frames[0].TryGetInt32(JpegDirectory.TagImageWidth, out int width)
                && frames[0].TryGetInt32(JpegDirectory.TagImageHeight, out int height))
            {
                return (width, height);
            }
        }
        else if (firstByte == 0x89)
        {
            // Read only IHDR. EXIF, text, and ICC chunks are unnecessary for
            // validation and may contain compressed, attacker-controlled data.
            Span<byte> bytes = stackalloc byte[33];
            if (stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) == bytes.Length
                && bytes[..8].SequenceEqual(UploadsOptions.SupportedImageSignatures["image/png"].Span)
                && BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) == 13
                && bytes[12..16].SequenceEqual("IHDR"u8))
            {
                var header = new PngHeader(bytes[16..29].ToArray());
                return (header.ImageWidth, header.ImageHeight);
            }
        }

        return null;
    }
}
