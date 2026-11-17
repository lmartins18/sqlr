using System.Collections.ObjectModel;
using System.Drawing;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// The IntelliSense list. It never takes focus — the editor keeps it and forwards
/// navigation keys here while the popup is open.
/// </summary>
internal sealed class CompletionPopup
{
    private const int MaxVisible = 10;

    private readonly FrameView _frame;
    private readonly ListView  _list;
    private List<Completion>   _items = [];
    private int                _selected;

    public CompletionPopup()
    {
        _frame = new FrameView
        {
            Title       = " Tab accept · Esc ",
            X           = 1,
            Y           = 2,
            Width       = 58,
            Height      = 12,
            Visible     = false,
            CanFocus    = false,
            ColorScheme = Theme.Popup
        };
        Theme.SetRounded(_frame);

        _list = new ListView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Popup,
            CanFocus    = false   // focus stays in the editor at all times
        };
        _frame.Add(_list);
    }

    public View View => _frame;
    public bool IsOpen => _frame.Visible;
    public IReadOnlyList<Completion> Items => _items;
    public Completion? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>
    /// Shows <paramref name="items"/> with the list's left edge under <paramref name="anchor"/>
    /// (screen position of the start of the word being completed).
    /// </summary>
    public void Show(IReadOnlyList<Completion> items, Point anchor, Size screen)
    {
        if (items.Count == 0) { Hide(); return; }

        _items    = [.. items];
        _selected = 0;

        int textW   = Math.Min(40, _items.Max(c => c.Text.Length));
        int detailW = Math.Min(30, _items.Max(c => (c.Detail ?? c.KindLabel).Length));
        var rows    = _items.Select(c => $"{Fit(c.Text, textW)}  {Fit(c.Detail ?? c.KindLabel, detailW)}");
        _list.SetSource(new ObservableCollection<string>(rows));
        _list.SelectedItem = 0;

        int width  = Math.Clamp(textW + detailW + 4, 30, Math.Max(30, screen.Width));
        int height = Math.Min(_items.Count, MaxVisible) + 2;

        // Below the cursor line when it fits, otherwise above it (the editor sits at the bottom).
        int y = anchor.Y + 1;
        if (y + height > screen.Height) y = anchor.Y - height;
        if (y < 1) y = Math.Max(0, Math.Min(anchor.Y + 1, screen.Height - height));
        int x = Math.Clamp(anchor.X - 1, 0, Math.Max(0, screen.Width - width));

        _frame.X       = x;
        _frame.Y       = y;
        _frame.Width   = width;
        _frame.Height  = height;
        _frame.Visible = true;
        _frame.SetNeedsLayout();
        _frame.SetNeedsDraw();
    }

    public void Move(int delta)
    {
        if (_items.Count == 0) return;
        // Wrap around at the ends, like most editors
        _selected = ((_selected + delta) % _items.Count + _items.Count) % _items.Count;
        _list.SelectedItem = _selected;
        _list.EnsureSelectedItemVisible();
        _list.SetNeedsDraw();
    }

    public void Page(int direction)
    {
        if (_items.Count == 0) return;
        _selected = Math.Clamp(_selected + direction * (MaxVisible - 1), 0, _items.Count - 1);
        _list.SelectedItem = _selected;
        _list.EnsureSelectedItemVisible();
        _list.SetNeedsDraw();
    }

    public void Hide()
    {
        if (!_frame.Visible) return;
        _frame.Visible = false;
        _frame.SuperView?.SetNeedsDraw();
    }

    private static string Fit(string s, int w) =>
        s.Length > w ? s[..Math.Max(0, w - 1)] + "…" : s.PadRight(w);
}
