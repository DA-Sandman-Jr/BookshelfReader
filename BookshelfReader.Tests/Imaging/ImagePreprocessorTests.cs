using BookshelfReader.Infrastructure.Imaging;
using OpenCvSharp;
using Xunit;

namespace BookshelfReader.Tests.Imaging;

public class ImagePreprocessorTests
{
    [Fact]
    public void Preprocess_StripsExifMetadataFromOutput()
    {
        byte[] input = JpegTestImages.WithExifOrientation(JpegTestImages.CreatePlainJpeg(20, 10), orientation: 1);

        byte[] output = ImagePreprocessor.Preprocess(input, maxImageDimension: 1568);

        Assert.False(ContainsApp1Segment(output));
    }

    [Fact]
    public void Preprocess_AppliesExifOrientationToUprightTheImage()
    {
        // Orientation 6 means "rotate 90deg clockwise to display upright", so a
        // 20x10 source should come out as 10x20.
        byte[] input = JpegTestImages.WithExifOrientation(JpegTestImages.CreatePlainJpeg(20, 10), orientation: 6);

        byte[] output = ImagePreprocessor.Preprocess(input, maxImageDimension: 1568);

        using Mat decoded = Cv2.ImDecode(output, ImreadModes.Color);
        Assert.Equal(10, decoded.Width);
        Assert.Equal(20, decoded.Height);
    }

    [Fact]
    public void Preprocess_DownscalesImagesLargerThanMaxDimension()
    {
        byte[] input = JpegTestImages.CreatePlainJpeg(2000, 1000);

        byte[] output = ImagePreprocessor.Preprocess(input, maxImageDimension: 500);

        using Mat decoded = Cv2.ImDecode(output, ImreadModes.Color);
        Assert.Equal(500, decoded.Width);
        Assert.Equal(250, decoded.Height);
    }

    [Fact]
    public void Preprocess_LeavesImagesSmallerThanMaxDimensionAtOriginalSize()
    {
        byte[] input = JpegTestImages.CreatePlainJpeg(40, 20);

        byte[] output = ImagePreprocessor.Preprocess(input, maxImageDimension: 1568);

        using Mat decoded = Cv2.ImDecode(output, ImreadModes.Color);
        Assert.Equal(40, decoded.Width);
        Assert.Equal(20, decoded.Height);
    }

    [Fact]
    public void Preprocess_UndecodableData_ThrowsInvalidOperationException()
    {
        byte[] input = { 0x00, 0x01, 0x02, 0x03 };

        Action act = () => ImagePreprocessor.Preprocess(input, maxImageDimension: 1568);

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Equal("Unable to decode the uploaded image.", exception.Message, ignoreCase: true);
    }

    private static bool ContainsApp1Segment(byte[] jpegBytes)
    {
        for (int i = 0; i + 1 < jpegBytes.Length; i++)
        {
            if (jpegBytes[i] == 0xFF && jpegBytes[i + 1] == 0xE1)
            {
                return true;
            }
        }

        return false;
    }
}
