using Terminal.Gui;
using TAttr = Terminal.Gui.Attribute;

namespace Sqlr.Tui;

/// <summary>Line numbers to the left of a TextView: follows its scrolling and brightens the cursor's line.</summary>
internal sealed class LineNumberGutter : View
{
    private readonly TextView _editor;

    public LineNumberGutter(TextView editor)
    {
        _editor = editor;
        CanFocus = false;
        Height = Dim.Fill();
        Width = WidthFor(editor.Lines);
        // Added after the editor, so marking it here still gets it drawn in the same pass.
        editor.DrawComplete += (_, _) => Sync();
    }

    /// <summary>Digits for the last line number (at least two) plus a space.</summary>
    internal static int WidthFor(int lines) => Math.Max(2, Math.Max(1, lines).ToString().Length) + 1;

    private void Sync()
    {
        int width = WidthFor(_editor.Lines);
        if (Frame.Width != width) Width = width;
        SetNeedsDraw();
    }

    /// <summary>The numbers as drawn, top to bottom (blank past the last line).</summary>
    internal IReadOnlyList<string> VisibleNumbers() =>
        Enumerable.Range(0, Viewport.Height).Select(y => Label(_editor.TopRow + y).Trim()).ToList();

    private string Label(int line) =>
        line < _editor.Lines ? (line + 1).ToString().PadLeft(Viewport.Width - 1) + " " : new string(' ', Viewport.Width);

    protected override bool OnDrawingContent()
    {
        var p = Theme.Current;
        var bg = _editor.GetNormalColor().Background;
        for (int y = 0; y < Viewport.Height; y++)
        {
            int line = _editor.TopRow + y;
            SetAttribute(new TAttr(line == _editor.CurrentRow ? p.Accent : p.Dim, bg));
            Move(0, y);
            AddStr(Label(line));
        }
        return true;
    }
}
