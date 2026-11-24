using System.Diagnostics;
using System.Reflection;
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

    /// <summary>Raised after a terminal paste was inserted in one go.</summary>
    public event Action? Pasted;

    public EditorView()
    {
        // Undo/redo with a selection still on can leave its anchor past the end of the restored text,
        // and Terminal.Gui then throws on every redraw. Drop the selection first.
        AddCommand(Command.Undo, () => { IsSelecting = false; Undo(); return true; });
        AddCommand(Command.Redo, () => { IsSelecting = false; Redo(); return true; });
    }

    protected override bool OnKeyDown(Key key)
    {
        if (!_replaying && Collect(key)) return true;
        Flush();
        bool handled = (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);
        KeyDone(key);
        return handled;
    }

    protected override bool OnKeyDownNotHandled(Key key)
    {
        bool inserted = base.OnKeyDownNotHandled(key);
        if (inserted && !key.IsCtrl && !key.IsAlt && key.AsRune.Value > 31 && key.AsRune.Value <= char.MaxValue)
            CharTyped?.Invoke((char)key.AsRune.Value);
        KeyDone(key);
        return inserted;
    }

    // ── Terminal paste ─────────────────────────────────────────────────────
    // Terminals paste by sending the text as keystrokes, and Terminal.Gui 2.0 has no bracketed
    // paste. Typed one by one (completion, redraw) a long script takes minutes, and its tabs would
    // move the focus. So typed keys wait for the next main-loop pass: a paste's keys all arrive in
    // that pass and go in together (first one included, so one undo takes it all back); a key on
    // its own is handled exactly as if typed.

    private static readonly TimeSpan PasteGap = TimeSpan.FromMilliseconds(250); // a paste carrying on after a redraw
    private const int PasteMinKeys = 32;

    // TextView's own paste path: fast, and one undo step. It's private; without it InsertText still works.
    private static readonly MethodInfo? InsertAllText =
        typeof(TextView).GetMethod("InsertAllText", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(bool)]);

    private readonly List<Key> _burst = [];
    private long _lastKeyDone;
    private bool _pasting;
    private bool _flushQueued;
    private bool _replaying;

    internal static bool CanPasteInOneStep => InsertAllText is not null;

    /// <summary>Runs the insert on the next main-loop pass, after the rest of the burst has arrived.</summary>
    internal Action<Action> Post { get; set; } = Application.Invoke;

    private static char? TypedChar(Key key) => key.KeyCode switch
    {
        KeyCode.Enter => '\n',
        KeyCode.Tab => '\t',
        _ when !key.IsCtrl && !key.IsAlt && key.AsRune.Value is > 31 and <= char.MaxValue and not 127 => (char)key.AsRune.Value,
        _ => null
    };

    private void KeyDone(Key key) => _lastKeyDone = TypedChar(key) is null ? 0 : Stopwatch.GetTimestamp();

    private bool Collect(Key key)
    {
        var ch = TypedChar(key);
        if (ch is null || ReadOnly)
        {
            _pasting = false;
            return false;
        }
        if (_burst.Count == 0 && _pasting && (_lastKeyDone == 0 || Stopwatch.GetElapsedTime(_lastKeyDone) >= PasteGap))
            _pasting = false;
        // A Tab right after typed letters accepts a completion; only inside a pasted block is it text.
        if (ch == '\t' && !_pasting && !_burst.Exists(k => k.KeyCode == KeyCode.Enter)) return false;

        _burst.Add(key);
        _lastKeyDone = Stopwatch.GetTimestamp();
        if (!_flushQueued)
        {
            _flushQueued = true;
            Post(Flush);
        }
        return true;
    }

    private void Flush()
    {
        _flushQueued = false;
        if (_burst.Count == 0) return;
        var keys = _burst.ToList();
        _burst.Clear();

        var text = new string(keys.Select(k => TypedChar(k)!.Value).ToArray());
        if (_pasting || keys.Count >= PasteMinKeys || keys.Count > 1 && text.Contains('\n'))
        {
            if (InsertAllText is not null) InsertAllText.Invoke(this, [text, false]);
            else InsertText(text);
            _pasting = true;
            _lastKeyDone = Stopwatch.GetTimestamp();
            Pasted?.Invoke();
            return;
        }

        // A few keys that queued up while typing fast: handle them exactly as if typed.
        _replaying = true;
        try
        {
            foreach (var k in keys) NewKeyDownEvent(new Key(k.KeyCode));
        }
        finally
        {
            _replaying = false;
        }
    }
}

/// <summary>A TableView whose keys can be intercepted before type-to-search.</summary>
internal sealed class GridView : TableView
{
    public Func<Key, bool>? PreviewKey { get; set; }

    protected override bool OnKeyDown(Key key) =>
        (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);
}

internal static class ViewText
{
    /// <summary>
    /// Shows '_' literally in the view's text and title. Terminal.Gui treats it as a hotkey marker
    /// by default, which turns "usp_GetBills" into "uspGetBills".
    /// </summary>
    public static T NoHotKey<T>(this T view) where T : View
    {
        view.HotKeySpecifier = (System.Text.Rune)0xFFFF;
        return view;
    }
}

/// <summary>A ListView whose keys can be intercepted before type-to-search.</summary>
internal sealed class KeyListView : ListView
{
    public Func<Key, bool>? PreviewKey { get; set; }

    protected override bool OnKeyDown(Key key) =>
        (PreviewKey?.Invoke(key) ?? false) || key.Handled || base.OnKeyDown(key);
}
