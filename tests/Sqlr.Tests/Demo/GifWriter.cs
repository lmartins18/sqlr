using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sqlr.Tests.Demo;

/// <summary>
/// A small animated-GIF encoder for terminal frames: one global palette (exact when the frames use
/// 256 colours or fewer, else the most used ones), and each frame stores only the rectangle that
/// changed since the previous one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GifWriter
{
    public static void Write(string path, IReadOnlyList<(string Png, int Centiseconds)> frames)
    {
        var (palette, index) = BuildPalette(frames.Select(f => f.Png));
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        int width = 0, height = 0;
        byte[]? previous = null;
        for (int f = 0; f < frames.Count; f++)
        {
            var (pixels, fw, fh) = ReadArgb(frames[f].Png);
            var indexed = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) indexed[i] = index(pixels[i]);

            if (f == 0)
            {
                (width, height) = (fw, fh);
                w.Write("GIF89a"u8);
                w.Write((ushort)width);
                w.Write((ushort)height);
                w.Write((byte)0xF7); // global colour table, 8 bits, 256 entries
                w.Write((byte)0);
                w.Write((byte)0);
                foreach (var c in palette) { w.Write(c.R); w.Write(c.G); w.Write(c.B); }
                w.Write([0x21, 0xFF, 0x0B]);
                w.Write("NETSCAPE2.0"u8);
                w.Write([0x03, 0x01, 0x00, 0x00, 0x00]); // loop forever
            }

            var (x0, y0, x1, y1) = previous is null ? (0, 0, width - 1, height - 1) : Changed(previous, indexed, width, height);
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
            var region = new byte[rw * rh];
            for (int y = 0; y < rh; y++) Array.Copy(indexed, (y0 + y) * width + x0, region, y * rw, rw);

            w.Write([0x21, 0xF9, 0x04, 0x04]); // graphic control: leave the frame in place
            w.Write((ushort)frames[f].Centiseconds);
            w.Write([0x00, 0x00]);
            w.Write((byte)0x2C);
            w.Write((ushort)x0);
            w.Write((ushort)y0);
            w.Write((ushort)rw);
            w.Write((ushort)rh);
            w.Write((byte)0);
            w.Write((byte)8);
            WriteBlocks(w, Lzw(region, 8));
            previous = indexed;
        }
        w.Write((byte)0x3B);
    }

    private static (int, int, int, int) Changed(byte[] a, byte[] b, int width, int height)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (a[row + x] == b[row + x]) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }
        return x1 < 0 ? (0, 0, 0, 0) : (x0, y0, x1, y1);
    }

    private static (Color[] Palette, Func<int, byte> Index) BuildPalette(IEnumerable<string> pngs)
    {
        var counts = new Dictionary<int, long>();
        foreach (var png in pngs)
        {
            var (pixels, _, _) = ReadArgb(png);
            foreach (var p in pixels) counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        var top = counts.OrderByDescending(kv => kv.Value).Take(256).Select(kv => kv.Key).ToArray();
        var palette = new Color[256];
        for (int i = 0; i < 256; i++) palette[i] = i < top.Length ? Color.FromArgb(top[i]) : Color.Black;

        var exact = new Dictionary<int, byte>();
        for (int i = 0; i < top.Length; i++) exact[top[i]] = (byte)i;
        byte Index(int argb)
        {
            if (exact.TryGetValue(argb, out var i)) return i;
            var c = Color.FromArgb(argb);
            int best = 0, bestDist = int.MaxValue;
            for (int k = 0; k < top.Length; k++)
            {
                var p = palette[k];
                int dr = p.R - c.R, dg = p.G - c.G, db = p.B - c.B, d = dr * dr + dg * dg + db * db;
                if (d < bestDist) (best, bestDist) = (k, d);
            }
            return exact[argb] = (byte)best;
        }
        return (palette, Index);
    }

    private static (int[] Pixels, int Width, int Height) ReadArgb(string png)
    {
        using var bmp = new Bitmap(png);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bmp.Width, bmp.Width);
            for (int i = 0; i < pixels.Length; i++) pixels[i] |= unchecked((int)0xFF000000);
            return (pixels, bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>GIF LZW: variable code width from 9 to 12 bits, a clear code when the table fills.</summary>
    private static byte[] Lzw(byte[] pixels, int minCodeSize)
    {
        int clear = 1 << minCodeSize, eoi = clear + 1, initBits = minCodeSize + 1;
        int nBits = initBits, maxCode = (1 << nBits) - 1, freeEnt = clear + 2;
        bool clearFlag = false;
        var table = new Dictionary<int, int>();
        var output = new List<byte>(pixels.Length / 4);
        int acc = 0, accBits = 0;

        void Output(int code)
        {
            acc |= code << accBits;
            accBits += nBits;
            while (accBits >= 8)
            {
                output.Add((byte)acc);
                acc >>= 8;
                accBits -= 8;
            }
            if (clearFlag)
            {
                nBits = initBits;
                maxCode = (1 << nBits) - 1;
                clearFlag = false;
            }
            else if (freeEnt > maxCode)
            {
                nBits++;
                maxCode = nBits == 12 ? 4096 : (1 << nBits) - 1;
            }
        }

        Output(clear);
        int ent = pixels[0];
        for (int i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            int key = (ent << 8) | c;
            if (table.TryGetValue(key, out int code))
            {
                ent = code;
                continue;
            }
            Output(ent);
            ent = c;
            if (freeEnt < 4096)
            {
                table[key] = freeEnt++;
            }
            else
            {
                table.Clear();
                freeEnt = clear + 2;
                clearFlag = true;
                Output(clear);
            }
        }
        Output(ent);
        Output(eoi);
        if (accBits > 0) output.Add((byte)acc);
        return output.ToArray();
    }

    private static void WriteBlocks(BinaryWriter w, byte[] data)
    {
        for (int i = 0; i < data.Length; i += 255)
        {
            int n = Math.Min(255, data.Length - i);
            w.Write((byte)n);
            w.Write(data, i, n);
        }
        w.Write((byte)0);
    }
}
