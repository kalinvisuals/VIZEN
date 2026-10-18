using System.Windows.Media.Imaging;
using VIZEN.App.Media;
using VIZEN.App.Models;

namespace VIZEN.Tests;

public class GifAnimationTests
{
    // Palette: 0 = transparent, 1 = red, 2 = blue, 3 = white.
    private static readonly byte[][] Palette = [[0, 0, 0], [255, 0, 0], [0, 0, 255], [255, 255, 255]];

    [Fact]
    public async Task ComposesPartialFramesWithDelaysAndTransparency()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vizen-test-{Guid.NewGuid():N}.gif");
        File.WriteAllBytes(path, BuildGif());
        try
        {
            var gif = await GifAnimation.LoadAsync(path);

            Assert.Equal(2, gif.Frames.Count);
            Assert.Equal(4, gif.PixelWidth);
            Assert.Equal(4, gif.PixelHeight);
            Assert.Equal(TimeSpan.FromMilliseconds(200), gif.Delays[0]);
            Assert.Equal(TimeSpan.FromMilliseconds(50), gif.Delays[1]);

            var first = Pixels(gif.Frames[0]);
            Assert.Equal((255, 0, 0, 255), At(first, 0, 0));    // red
            Assert.Equal(0, At(first, 1, 1).A);                 // transparent pixel kept

            var second = Pixels(gif.Frames[1]);
            Assert.Equal((255, 0, 0, 255), At(second, 0, 0));   // frame 1 kept underneath (disposal 1)
            Assert.Equal((0, 0, 255, 255), At(second, 3, 3));   // frame 2 drawn at offset (2,2)
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("logo.PNG", MediaKind.Image)]
    [InlineData("anim.gif", MediaKind.Gif)]
    [InlineData("promo.mp4", MediaKind.Video)]
    [InlineData("photo.webp", MediaKind.Image)]
    public void KindFromExtension(string file, MediaKind expected) => Assert.Equal(expected, MediaFormats.KindFor(file));

    [Fact]
    public void UnsupportedExtensionIsRejected() => Assert.Null(MediaFormats.KindFor("notes.txt"));

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    // Pbgra32 → (R, G, B, A)
    private static (int R, int G, int B, int A) At(byte[] pixels, int x, int y)
    {
        var i = (y * 4 + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }

    /// <summary>4x4 GIF: frame 1 all red except a transparent pixel at (1,1), 200 ms; frame 2 a 2x2 blue square at (2,2), 50 ms.</summary>
    private static byte[] BuildGif()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([4, 0, 4, 0, 0x91, 0, 0]); // 4x4, global colour table of 4
        foreach (var color in Palette)
        {
            gif.AddRange(color);
        }

        var frame1 = Enumerable.Repeat((byte)1, 16).ToArray();
        frame1[1 * 4 + 1] = 0;
        AddFrame(gif, 0, 0, 4, 4, delay: 20, disposal: 1, frame1);
        AddFrame(gif, 2, 2, 2, 2, delay: 5, disposal: 1, [2, 2, 2, 2]);
        gif.Add(0x3B);
        return [.. gif];
    }

    private static void AddFrame(List<byte> gif, int left, int top, int width, int height, int delay, int disposal, byte[] indices)
    {
        gif.AddRange([0x21, 0xF9, 0x04, (byte)((disposal << 2) | 1), (byte)delay, (byte)(delay >> 8), 0, 0]);
        gif.AddRange([0x2C, (byte)left, (byte)(left >> 8), (byte)top, (byte)(top >> 8),
            (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0]);
        gif.Add(2); // LZW minimum code size
        var data = Lzw(indices);
        for (var i = 0; i < data.Length; i += 255)
        {
            var count = Math.Min(255, data.Length - i);
            gif.Add((byte)count);
            gif.AddRange(data.AsSpan(i, count).ToArray());
        }

        gif.Add(0);
    }

    /// <summary>"Uncompressed" LZW: a clear code every two pixels keeps the code width at 3 bits.</summary>
    private static byte[] Lzw(byte[] indices)
    {
        const int clear = 4, end = 5;
        var codes = new List<int>();
        for (var i = 0; i < indices.Length; i++)
        {
            if (i % 2 == 0)
            {
                codes.Add(clear);
            }

            codes.Add(indices[i]);
        }

        codes.Add(end);
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var code in codes)
        {
            buffer |= code << bits;
            bits += 3;
            while (bits >= 8)
            {
                bytes.Add((byte)buffer);
                buffer >>= 8;
                bits -= 8;
            }
        }

        if (bits > 0)
        {
            bytes.Add((byte)buffer);
        }

        return [.. bytes];
    }
}
