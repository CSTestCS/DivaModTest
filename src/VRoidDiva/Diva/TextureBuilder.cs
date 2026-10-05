using MikuMikuLibrary.Textures;
using StbImageSharp;

namespace VRoidDiva.Diva;

/// <summary>An 8-bit RGBA image, rows top to bottom.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[] pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
    }

    public static RgbaImage Decode(byte[] encoded)
    {
        var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        return new RgbaImage(image.Width, image.Height, image.Data);
    }

    public bool HasTransparency()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] < 250)
                return true;
        }

        return false;
    }

    /// <summary>2x2 box filter down-sample (odd sizes clamp at the edge).</summary>
    public RgbaImage HalfSize()
    {
        int w = Math.Max(1, Width / 2);
        int h = Math.Max(1, Height / 2);
        var result = new RgbaImage(w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        for (int c = 0; c < 4; c++)
        {
            int sum = 0;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int sx = Math.Min(x * 2 + dx, Width - 1);
                int sy = Math.Min(y * 2 + dy, Height - 1);
                sum += Pixels[(sy * Width + sx) * 4 + c];
            }

            result.Pixels[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
        }

        return result;
    }

    /// <summary>Returns a copy with each channel multiplied by the given factor (0..1).</summary>
    public RgbaImage Tinted(float r, float g, float b, float a)
    {
        var result = new RgbaImage(Width, Height, (byte[])Pixels.Clone());
        if (r >= 0.999f && g >= 0.999f && b >= 0.999f && a >= 0.999f)
            return result;

        for (int i = 0; i < result.Pixels.Length; i += 4)
        {
            result.Pixels[i] = (byte)Math.Round(result.Pixels[i] * Math.Clamp(r, 0, 1));
            result.Pixels[i + 1] = (byte)Math.Round(result.Pixels[i + 1] * Math.Clamp(g, 0, 1));
            result.Pixels[i + 2] = (byte)Math.Round(result.Pixels[i + 2] * Math.Clamp(b, 0, 1));
            result.Pixels[i + 3] = (byte)Math.Round(result.Pixels[i + 3] * Math.Clamp(a, 0, 1));
        }

        return result;
    }
}

/// <summary>
/// Builds DIVA textures (DXT1 / DXT5 with a full mip chain) from RGBA images
/// in managed code, so no native encoder is required.
/// </summary>
public static class TextureBuilder
{
    public static Texture Build(RgbaImage image, string name, uint id, int maxSize, bool? alpha = null)
    {
        while (image.Width > maxSize || image.Height > maxSize)
            image = image.HalfSize();

        bool hasAlpha = alpha ?? image.HasTransparency();
        var format = hasAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1;

        int mipCount = 1;
        for (int size = Math.Max(image.Width, image.Height); size > 1; size >>= 1)
            mipCount++;

        var texture = new Texture(image.Width, image.Height, format, 1, mipCount)
        {
            Name = name,
            Id = id
        };

        var level = image;
        for (int mip = 0; mip < mipCount; mip++)
        {
            var subTexture = texture[mip];
            var encoded = BlockCompressor.Encode(level, hasAlpha);

            if (encoded.Length != subTexture.Data.Length)
            {
                throw new InvalidOperationException(
                    $"Encoded mip {mip} of '{name}' is {encoded.Length} bytes, expected {subTexture.Data.Length}.");
            }

            Buffer.BlockCopy(encoded, 0, subTexture.Data, 0, encoded.Length);

            if (mip + 1 < mipCount)
                level = level.HalfSize();
        }

        return texture;
    }
}

/// <summary>Straightforward BC1 (DXT1) / BC3 (DXT5) block encoder.</summary>
public static class BlockCompressor
{
    public static byte[] Encode(RgbaImage image, bool withAlpha)
    {
        int blocksX = Math.Max(1, (image.Width + 3) / 4);
        int blocksY = Math.Max(1, (image.Height + 3) / 4);
        int blockSize = withAlpha ? 16 : 8;
        var output = new byte[blocksX * blocksY * blockSize];

        Parallel.For(0, blocksY, () => new byte[64], (by, _, local) =>
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                FetchBlock(image, bx * 4, by * 4, local);
                int offset = (by * blocksX + bx) * blockSize;

                if (withAlpha)
                {
                    EncodeAlphaBlock(local, output, offset);
                    EncodeColorBlock(local, output, offset + 8);
                }
                else
                {
                    EncodeColorBlock(local, output, offset);
                }
            }

            return local;
        }, _ => { });

        return output;
    }

    private static void FetchBlock(RgbaImage image, int x0, int y0, byte[] block)
    {
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int sx = Math.Min(x0 + x, image.Width - 1);
            int sy = Math.Min(y0 + y, image.Height - 1);
            Buffer.BlockCopy(image.Pixels, (sy * image.Width + sx) * 4, block, (y * 4 + x) * 4, 4);
        }
    }

    private static ushort To565(int r, int g, int b) =>
        (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));

    private static (int R, int G, int B) From565(ushort c)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    private static void EncodeColorBlock(byte[] block, byte[] output, int offset)
    {
        // Endpoints: the bounding-box diagonal that best follows the colour
        // distribution, inset slightly to reduce quantisation error.
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
            maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
        }

        float meanR = 0, meanG = 0, meanB = 0;
        for (int i = 0; i < 16; i++)
        {
            meanR += block[i * 4]; meanG += block[i * 4 + 1]; meanB += block[i * 4 + 2];
        }

        meanR /= 16; meanG /= 16; meanB /= 16;
        float covRG = 0, covRB = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = block[i * 4] - meanR, g = block[i * 4 + 1] - meanG, b = block[i * 4 + 2] - meanB;
            covRG += r * g; covRB += r * b;
        }

        if (covRG < 0) (minG, maxG) = (maxG, minG);
        if (covRB < 0) (minB, maxB) = (maxB, minB);

        int insetR = (maxR - minR) / 16, insetG = (maxG - minG) / 16, insetB = (maxB - minB) / 16;
        maxR = Math.Clamp(maxR - insetR, 0, 255); minR = Math.Clamp(minR + insetR, 0, 255);
        maxG = Math.Clamp(maxG - insetG, 0, 255); minG = Math.Clamp(minG + insetG, 0, 255);
        maxB = Math.Clamp(maxB - insetB, 0, 255); minB = Math.Clamp(minB + insetB, 0, 255);

        ushort c0 = To565(maxR, maxG, maxB);
        ushort c1 = To565(minR, minG, minB);

        if (c0 < c1)
            (c0, c1) = (c1, c0);

        var palette = new (int R, int G, int B)[4];
        palette[0] = From565(c0);
        palette[1] = From565(c1);

        uint indices = 0;
        if (c0 != c1) // otherwise a single-colour block: all indices stay 0
        {
            palette[2] = ((2 * palette[0].R + palette[1].R) / 3, (2 * palette[0].G + palette[1].G) / 3,
                (2 * palette[0].B + palette[1].B) / 3);
            palette[3] = ((palette[0].R + 2 * palette[1].R) / 3, (palette[0].G + 2 * palette[1].G) / 3,
                (palette[0].B + 2 * palette[1].B) / 3);

            for (int i = 0; i < 16; i++)
            {
                int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
                int best = 0, bestDistance = int.MaxValue;
                for (int p = 0; p < 4; p++)
                {
                    int dr = r - palette[p].R, dg = g - palette[p].G, db = b - palette[p].B;
                    int distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = p;
                    }
                }

                indices |= (uint)best << (i * 2);
            }
        }

        output[offset] = (byte)c0;
        output[offset + 1] = (byte)(c0 >> 8);
        output[offset + 2] = (byte)c1;
        output[offset + 3] = (byte)(c1 >> 8);
        output[offset + 4] = (byte)indices;
        output[offset + 5] = (byte)(indices >> 8);
        output[offset + 6] = (byte)(indices >> 16);
        output[offset + 7] = (byte)(indices >> 24);
    }

    private static void EncodeAlphaBlock(byte[] block, byte[] output, int offset)
    {
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = block[i * 4 + 3];
            min = Math.Min(min, a);
            max = Math.Max(max, a);
        }

        output[offset] = (byte)max;
        output[offset + 1] = (byte)min;

        ulong bits = 0;
        if (max != min)
        {
            // 8-value mode (a0 > a1): palette[0]=max, [1]=min, [2..7] interpolated.
            var palette = new int[8];
            palette[0] = max;
            palette[1] = min;
            for (int i = 1; i <= 6; i++)
                palette[i + 1] = ((7 - i) * max + i * min) / 7;

            for (int i = 0; i < 16; i++)
            {
                int a = block[i * 4 + 3];
                int best = 0, bestDistance = int.MaxValue;
                for (int p = 0; p < 8; p++)
                {
                    int distance = Math.Abs(a - palette[p]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = p;
                    }
                }

                bits |= (ulong)best << (i * 3);
            }
        }

        for (int i = 0; i < 6; i++)
            output[offset + 2 + i] = (byte)(bits >> (i * 8));
    }
}
