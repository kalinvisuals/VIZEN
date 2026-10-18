namespace VIZEN.App.Capture;

/// <summary>
/// Frame-number marker used by VIZEN's test sources: a row of square blocks at the top-left corner.
/// Block 0 is white and block 1 black (they give the block size); blocks 2..33 are the 32-bit frame number,
/// most significant bit first, white = 1. Reading it back on the displayed frame counts skipped and repeated
/// frames exactly, end to end.
/// </summary>
public static class FrameMarker
{
    public const int Bits = 32;
    public const int Blocks = Bits + 2;

    /// <summary>Writes <paramref name="index"/> into a BGRA buffer with blocks of <paramref name="block"/> pixels.</summary>
    public static void Encode(byte[] bgra, int width, int height, int stride, uint index, int block)
    {
        if (block * Blocks > width || block > height)
        {
            return;
        }

        for (var k = 0; k < Blocks; k++)
        {
            var white = k == 0 || (k >= 2 && ((index >> (Bits - 1 - (k - 2))) & 1) == 1);
            var value = white ? (byte)255 : (byte)0;
            for (var y = 0; y < block; y++)
            {
                var row = y * stride + k * block * 4;
                bgra.AsSpan(row, block * 4).Fill(value);
                for (var x = 0; x < block; x++)
                {
                    bgra[row + x * 4 + 3] = 255;
                }
            }
        }
    }

    /// <summary>Reads the frame number, or null when the marker is not there (not a test source, or cropped).</summary>
    public static uint? Decode(byte[] bgra, int width, int height, int stride)
    {
        if (width < Blocks * 2 || height < 2 || bgra.Length < stride * 2)
        {
            return null;
        }

        // Block size = length of the white run at the start of row 0, followed by black.
        var block = 0;
        while (block < width && block < 64 && IsWhite(bgra, block * 4))
        {
            block++;
        }

        if (block < 2 || block >= 64 || block * Blocks > width || block > height || IsWhite(bgra, (block + block / 2) * 4))
        {
            return null;
        }

        var y = block / 2;
        uint value = 0;
        for (var k = 2; k < Blocks; k++)
        {
            var x = k * block + block / 2;
            value = (value << 1) | (IsWhite(bgra, y * stride + x * 4) ? 1u : 0u);
        }

        return value;
    }

    private static bool IsWhite(byte[] bgra, int offset) =>
        offset + 2 < bgra.Length && bgra[offset] + bgra[offset + 1] + bgra[offset + 2] > 3 * 160;
}
