namespace BookshelfReader.Infrastructure.Imaging;

/// <summary>
/// Reads the EXIF orientation tag (0x0112) from a JPEG's APP1 segment, without
/// pulling in a full EXIF library. Returns 1 (normal) for non-JPEG input, JPEGs
/// without an EXIF APP1 segment, or any value outside the valid 1-8 range.
/// </summary>
internal static class ExifOrientationReader
{
    private const int DefaultOrientation = 1;
    private const int OrientationTag = 0x0112;

    public static int ReadOrientation(byte[] jpegData)
    {
        ArgumentNullException.ThrowIfNull(jpegData);

        if (jpegData.Length < 4 || jpegData[0] != 0xFF || jpegData[1] != 0xD8)
        {
            return DefaultOrientation;
        }

        int offset = 2;
        while (offset + 4 <= jpegData.Length)
        {
            if (jpegData[offset] != 0xFF)
            {
                break;
            }

            byte marker = jpegData[offset + 1];

            // SOI/EOI and the RSTn restart markers carry no length field.
            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            // SOS marks the start of compressed scan data; no metadata follows.
            if (marker == 0xDA)
            {
                break;
            }

            int segmentLength = (jpegData[offset + 2] << 8) | jpegData[offset + 3];
            if (segmentLength < 2 || offset + 2 + segmentLength > jpegData.Length)
            {
                break;
            }

            if (marker == 0xE1)
            {
                int orientation = TryReadOrientationFromApp1(jpegData, offset + 4, segmentLength - 2);
                if (orientation != 0)
                {
                    return orientation;
                }
            }

            offset += 2 + segmentLength;
        }

        return DefaultOrientation;
    }

    private static int TryReadOrientationFromApp1(byte[] data, int start, int length)
    {
        if (!TryParseTiffHeader(data, start, length, out int tiffStart, out bool littleEndian))
        {
            return 0;
        }

        return FindOrientationValue(data, tiffStart, littleEndian);
    }

    private static bool TryParseTiffHeader(byte[] data, int start, int length, out int tiffStart, out bool littleEndian)
    {
        tiffStart = 0;
        littleEndian = false;

        // "Exif\0\0" header followed by a TIFF header (byte-order + IFD0 offset).
        if (length < 14 || start + 14 > data.Length)
        {
            return false;
        }

        if (data[start] != 'E' || data[start + 1] != 'x' || data[start + 2] != 'i' || data[start + 3] != 'f'
            || data[start + 4] != 0x00 || data[start + 5] != 0x00)
        {
            return false;
        }

        int candidateTiffStart = start + 6;
        bool isLittleEndian = data[candidateTiffStart] == 'I' && data[candidateTiffStart + 1] == 'I';
        bool isBigEndian = data[candidateTiffStart] == 'M' && data[candidateTiffStart + 1] == 'M';
        if (!isLittleEndian && !isBigEndian)
        {
            return false;
        }

        tiffStart = candidateTiffStart;
        littleEndian = isLittleEndian;
        return true;
    }

    private static int FindOrientationValue(byte[] data, int tiffStart, bool littleEndian)
    {
        uint ifd0Offset = ReadUInt32(data, tiffStart + 4, littleEndian);
        long ifd0Start = tiffStart + ifd0Offset;
        if (ifd0Start < 0 || ifd0Start + 2 > data.Length)
        {
            return 0;
        }

        int entryCount = ReadUInt16(data, (int)ifd0Start, littleEndian);
        long entriesStart = ifd0Start + 2;

        for (int i = 0; i < entryCount; i++)
        {
            long entryOffset = entriesStart + (i * 12L);
            if (entryOffset + 12 > data.Length)
            {
                break;
            }

            int tag = ReadUInt16(data, (int)entryOffset, littleEndian);
            if (tag == OrientationTag)
            {
                int value = ReadUInt16(data, (int)entryOffset + 8, littleEndian);
                return value is >= 1 and <= 8 ? value : 0;
            }
        }

        return 0;
    }

    private static int ReadUInt16(byte[] data, int offset, bool littleEndian) =>
        littleEndian
            ? data[offset] | (data[offset + 1] << 8)
            : (data[offset] << 8) | data[offset + 1];

    private static uint ReadUInt32(byte[] data, int offset, bool littleEndian) =>
        littleEndian
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
