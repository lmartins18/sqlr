using Terminal.Gui;

namespace Sqlr.Tui;

// Terminal.Gui raises a view's KeyDown event only after the view's own OnKeyDown, and
// TableView/ListView consume letter keys there (type-to-search). These subclasses give
// the app first say over every key via PreviewKey.

/// <summary>A TextView whose keys can be intercepted before its built-in handling.</summary>
internal sealed class EditorView : TextView
{
    /// <summary>Runs before the TextView handles the key. Return true to consume it.</summary>
    public Func<Key, bool>? PreviewKey { get; set; }

    /// <summary>Raised after a printable character has been inserted (cursor already advanced).</summary>
    public event Action<char>? CharTyped;

    protected override bool OnKeyDown(Key key) =>
        (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);

    protected override bool OnKeyDownNotHandled(Key key)
    {
        bool inserted = base.OnKeyDownNotHandled(key);
        if (inserted && !key.IsCtrl && !key.IsAlt && key.AsRune.Value > 31 && key.AsRune.Value <= char.MaxValue)
            CharTyped?.Invoke((char)key.AsRune.Value);
        return inserted;
    }
}

/// <summary>A TableView whose keys can be intercepted before type-to-search.</summary>
internal sealed class GridView : TableView
{
    public Func<Key, bool>? PreviewKey { get; set; }

    protected override bool OnKeyDown(Key key) =>
        (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);
}

/// <summary>A ListView whose keys can be intercepted before type-to-search.</summary>
internal sealed class KeyListView : ListView
{
    public Func<Key, bool>? PreviewKey { get; set; }

    protected override bool OnKeyDown(Key key) =>
        (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);
}
