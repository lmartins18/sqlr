using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using System.Text;
using Sqlr.Tui;
using Terminal.Gui;
using DColor = System.Drawing.Color;
using TColor = Terminal.Gui.Color;

namespace Sqlr.Tests.Demo;

/// <summary>
/// Turns the FakeDriver's screen into PNG frames and the frames into a GIF (written by GifWriter).
/// Box-drawing characters are drawn as lines so borders join up between rows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GifRecorder : IDisposable
{
    private const int CellW = 9, CellH = 18, Pad = 14;

    private readonly string _dir;
    private readonly List<(string File, int Ms)> _frames = [];
    private readonly Font _font = new("Cascadia Mono", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Dictionary<int, SolidBrush> _brushes = [];
    private string? _lastSignature;

    public GifRecorder(string name)
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlr-demo-frames", name);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
    }

    public int TotalMs => _frames.Sum(f => f.Ms);

    /// <summary>Adds the current screen, shown for <paramref name="ms"/>; an unchanged screen just stays longer.</summary>
    public void Capture(int ms, Point? cursor)
    {
        var cells = Application.Driver!.Contents!;
        var signature = Signature(cells, cursor);
        if (signature == _lastSignature && _frames.Count > 0)
        {
            _frames[^1] = (_frames[^1].File, _frames[^1].Ms + ms);
            return;
        }
        _lastSignature = signature;

        var file = Path.Combine(_dir, $"f{_frames.Count:D4}.png");
        using (var bmp = Render(cells, cursor)) bmp.Save(file, ImageFormat.Png);
        _frames.Add((file, ms));
    }

    /// <summary>Writes the GIF. Delays are in hundredths of a second, so short holds round up.</summary>
    public void Save(string gifPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(gifPath)!);
        GifWriter.Write(gifPath, _frames.Select(f => (f.File, Math.Max(2, (f.Ms + 5) / 10))).ToList());
    }

    private static string Signature(Cell[,] cells, Point? cursor)
    {
        var sb = new StringBuilder(cells.Length * 4);
        foreach (var c in cells)
            sb.Append(c.Rune.Value).Append(c.Attribute?.Foreground.Argb).Append(c.Attribute?.Background.Argb);
        return sb.Append(cursor).ToString();
    }

    private Bitmap Render(Cell[,] cells, Point? cursor)
    {
        int rows = cells.GetLength(0), cols = cells.GetLength(1);
        var bmp = new Bitmap(cols * CellW + 2 * Pad, rows * CellH + 2 * Pad);
        using var g = Graphics.FromImage(bmp);
        g.Clear(ToDrawing(Theme.Current.Bg));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var format = StringFormat.GenericTypographic;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = cells[r, c];
                var fg = cell.Attribute?.Foreground ?? Theme.Current.Fg;
                var bg = cell.Attribute?.Background ?? Theme.Current.Bg;
                if (cursor is { } cp && cp.X == c && cp.Y == r) (fg, bg) = (bg, fg);

                int x = Pad + c * CellW, y = Pad + r * CellH;
                g.FillRectangle(Brush(bg), x, y, CellW, CellH);

                var rune = cell.Rune;
                if (rune.Value <= ' ') continue;
                if (DrawBox(g, rune.Value, x, y, ToDrawing(fg))) continue;
                g.DrawString(rune.ToString(), _font, Brush(fg), x - 1, y + 1, format);
            }
        }
        return bmp;
    }

    /// <summary>Lines, corners and tees drawn from the cell's centre; blocks filled. False: use the font.</summary>
    private static bool DrawBox(Graphics g, int ch, int x, int y, DColor color)
    {
        (bool up, bool down, bool left, bool right, bool round) = ch switch
        {
            0x2500 or 0x2501 or 0x2550 => (false, false, true, true, false),
            0x2502 or 0x2503 or 0x2551 => (true, true, false, false, false),
            0x250C or 0x2554 => (false, true, false, true, false),
            0x2510 or 0x2557 => (false, true, true, false, false),
            0x2514 or 0x255A => (true, false, false, true, false),
            0x2518 or 0x255D => (true, false, true, false, false),
            0x251C or 0x2560 => (true, true, false, true, false),
            0x2524 or 0x2563 => (true, true, true, false, false),
            0x252C or 0x2566 => (false, true, true, true, false),
            0x2534 or 0x2569 => (true, false, true, true, false),
            0x253C or 0x256C => (true, true, true, true, false),
            0x256D => (false, true, false, true, true),
            0x256E => (false, true, true, false, true),
            0x256F => (true, false, true, false, true),
            0x2570 => (true, false, false, true, true),
            _ => (false, false, false, false, false)
        };
        int cx = x + CellW / 2, cy = y + CellH / 2;
        using var pen = new Pen(color, 1);

        if (round)
        {
            // A quarter circle from the centre to the two edges it joins, then straight on to them.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int rad = 4;
            int sx = right ? 1 : -1, sy = down ? 1 : -1;
            g.DrawArc(pen, sx > 0 ? cx : cx - 2 * rad, sy > 0 ? cy : cy - 2 * rad, 2 * rad, 2 * rad,
                (sx, sy) switch { (1, 1) => 180, (-1, 1) => 270, (-1, -1) => 0, _ => 90 }, 90);
            g.SmoothingMode = SmoothingMode.None;
            g.DrawLine(pen, cx + sx * rad, cy, sx > 0 ? x + CellW : x, cy);
            g.DrawLine(pen, cx, cy + sy * rad, cx, sy > 0 ? y + CellH : y);
            return true;
        }
        if (up || down || left || right)
        {
            if (left) g.DrawLine(pen, x, cy, cx, cy);
            if (right) g.DrawLine(pen, cx, cy, x + CellW, cy);
            if (up) g.DrawLine(pen, cx, y, cx, cy);
            if (down) g.DrawLine(pen, cx, cy, cx, y + CellH);
            return true;
        }

        using var brush = new SolidBrush(color);
        switch (ch)
        {
            case 0x2588: g.FillRectangle(brush, x, y, CellW, CellH); return true;            // █
            case 0x2580: g.FillRectangle(brush, x, y, CellW, CellH / 2); return true;        // ▀
            case 0x2584: g.FillRectangle(brush, x, y + CellH / 2, CellW, CellH / 2); return true; // ▄
            case 0x258C: g.FillRectangle(brush, x, y, CellW / 2, CellH); return true;        // ▌
            case 0x2590: g.FillRectangle(brush, x + CellW / 2, y, CellW / 2, CellH); return true; // ▐
        }
        return false;
    }

    private SolidBrush Brush(TColor c)
    {
        int key = (int)c.Argb;
        if (!_brushes.TryGetValue(key, out var b)) _brushes[key] = b = new SolidBrush(ToDrawing(c));
        return b;
    }

    private static DColor ToDrawing(TColor c) => DColor.FromArgb(c.R, c.G, c.B);

    public void Dispose()
    {
        _font.Dispose();
        foreach (var b in _brushes.Values) b.Dispose();
    }
}
