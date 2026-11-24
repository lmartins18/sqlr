using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Xunit;

namespace Sqlr.Tests.Demo;

[SupportedOSPlatform("windows")]
public sealed class GifWriterTests
{
    [Fact]
    public void Frames_decode_back_pixel_for_pixel()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("sqlr-gif-").FullName;
        var colors = Enumerable.Range(0, 40).Select(i => Color.FromArgb(i * 6, 255 - i * 5, (i * 37) % 256)).ToArray();
        var frames = new List<(string, int)>();
        var expected = new List<Color[,]>();
        for (int f = 0; f < 3; f++)
        {
            using var bmp = new Bitmap(64, 40);
            var px = new Color[64, 40];
            for (int x = 0; x < 64; x++)
                for (int y = 0; y < 40; y++)
                {
                    // the last frame changes only a small block, so it's stored as a sub-rectangle
                    var c = f == 2 && (x < 20 || y < 10) ? expected[1][x, y] : colors[(x * 7 + y * 3 + f * 11) % colors.Length];
                    bmp.SetPixel(x, y, c);
                    px[x, y] = c;
                }
            var file = Path.Combine(dir, $"f{f}.png");
            bmp.Save(file, ImageFormat.Png);
            frames.Add((file, 10));
            expected.Add(px);
        }

        var gif = Path.Combine(dir, "out.gif");
        GifWriter.Write(gif, frames);

        using var decoded = Image.FromFile(gif);
        Assert.Equal(3, decoded.GetFrameCount(FrameDimension.Time));
        for (int f = 0; f < 3; f++)
        {
            decoded.SelectActiveFrame(FrameDimension.Time, f);
            using var frame = new Bitmap(decoded);
            for (int x = 0; x < 64; x++)
                for (int y = 0; y < 40; y++)
                    Assert.Equal(expected[f][x, y].ToArgb(), frame.GetPixel(x, y).ToArgb());
        }
    }
}
