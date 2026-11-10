using BookshelfReader.Infrastructure.Imaging;
using Xunit;

namespace BookshelfReader.Tests.Imaging;

public class ExifOrientationReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ReadOrientation_ReadsOrientationTagFromApp1Segment(int orientation)
    {
        byte[] jpegBytes = JpegTestImages.WithExifOrientation(JpegTestImages.CreatePlainJpeg(8, 8), orientation);

        Assert.Equal(orientation, ExifOrientationReader.ReadOrientation(jpegBytes));
    }

    [Fact]
    public void ReadOrientation_WithoutApp1Segment_ReturnsDefaultOrientation()
    {
        byte[] jpegBytes = JpegTestImages.CreatePlainJpeg(8, 8);

        Assert.Equal(1, ExifOrientationReader.ReadOrientation(jpegBytes));
    }

    [Fact]
    public void ReadOrientation_NonJpegData_ReturnsDefaultOrientation()
    {
        byte[] data = { 0x00, 0x01, 0x02, 0x03, 0x04 };

        Assert.Equal(1, ExifOrientationReader.ReadOrientation(data));
    }

    [Fact]
    public void ReadOrientation_TooShortToContainAMarker_ReturnsDefaultOrientation()
    {
        byte[] data = { 0xFF, 0xD8 };

        Assert.Equal(1, ExifOrientationReader.ReadOrientation(data));
    }

    [Fact]
    public void ReadOrientation_OutOfRangeOrientationValue_ReturnsDefaultOrientation()
    {
        byte[] jpegBytes = JpegTestImages.WithExifOrientation(JpegTestImages.CreatePlainJpeg(8, 8), orientation: 9);

        Assert.Equal(1, ExifOrientationReader.ReadOrientation(jpegBytes));
    }
}
