using System.Text.RegularExpressions;
using Sqlr.Db;
using Terminal.Gui;
using TAttr = Terminal.Gui.Attribute;

namespace Sqlr.Tui;

/// <summary>
/// The upper pane: one tab per result set plus a Messages tab (rows affected, PRINT output,
/// errors). F7 / Shift+F7 cycle tabs.
/// </summary>
internal sealed class ResultsPane
{
    private const string Welcome =
        """

          ◈ sqlr

          F5  run script (or selection)      F6  run block under cursor
          F3  history     F4  objects        F12 peek table / proc
          F1  all keys    F9  theme          Esc cancel · Ctrl+Q quit

          Results:  Enter view cell · c/r/a copy · / filter · s sort · e export
        """;

    private readonly int _maxColumnWidth;
    private ExecutionResult? _result;
    private List<Theme.Msg> _messageLineColors = []; // resolved at draw time so a theme switch recolours them
    private List<int?>  _messageLineTargets = []; // editor line each displayed row points at
    private int _active; // 0..n-1 = result sets, n = messages

    public ResultsPane(int maxColumnWidth)
    {
        _maxColumnWidth = maxColumnWidth;

        Frame = new FrameView
        {
            Title       = " results ",
            X           = 0, Y = 1,
            Width       = Dim.Fill(),
            Height      = Dim.Percent(70),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(Frame);
        Frame.NoHotKey();

        Table = new GridView
        {
            X             = 0, Y = 0,
            Width         = Dim.Fill(),
            Height        = Dim.Fill(),
            ColorScheme   = Theme.Base,
            FullRowSelect = false,
            MultiSelect   = false,
            Visible       = false,
            NullSymbol    = "∅",
            MaxCellWidth  = maxColumnWidth
        };
        ConfigureTableStyle(Table);

        Messages = new EditorView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ReadOnly    = true,
            WordWrap    = false,   // wrapped by BuildMessages, so rows map 1:1 to message lines
            ColorScheme = Theme.Input,
            Text        = Welcome
        };
        Messages.DrawNormalColor += (_, e) =>
        {
            var row = e.UnwrappedPosition.Item1;
            if (row < 0 || row >= _messageLineColors.Count || e.Col < 0 || e.Col >= e.Line.Count) return;
            var cell = e.Line[e.Col];
            cell.Attribute = Theme.Message(_messageLineColors[row]);
            e.Line[e.Col]  = cell;
        };

        Frame.Add(Messages, Table);
    }

    public FrameView Frame    { get; }
    public GridView   Table    { get; }
    public EditorView Messages { get; }

    public ResultTableSource? Source => Table.Table as ResultTableSource;
    public bool ShowingMessages => !Table.Visible;
    public bool HasResult => _result is not null;
    public ExecutionResult? Result => _result;

    /// <summary>Raised when the active tab, filter or sort changes (so the status line can refresh).</summary>
    public event Action? Changed;

    public void Load(ExecutionResult result)
    {
        _result = result;
        BuildMessages(result);

        // Errors: go straight to Messages so they can't be missed. Otherwise the first result set.
        _active = result.HasErrors || result.ResultSets.Count == 0 ? result.ResultSets.Count : 0;
        ShowActive();
    }

    public void Clear()
    {
        _result             = null;
        _messageLineColors  = [];
        _messageLineTargets = [];
        Messages.Text      = Welcome;
        Table.Table        = null;
        _active            = 0;
        Table.Visible      = false;
        Messages.Visible   = true;
        UpdateTitle();
        Changed?.Invoke();
    }

    public void Cycle(int direction)
    {
        if (_result is null) return;
        int tabs = _result.ResultSets.Count + 1;
        _active  = ((_active + direction) % tabs + tabs) % tabs;
        ShowActive();
    }

    private void ShowActive()
    {
        var sets = _result?.ResultSets ?? [];
        if (_active < sets.Count)
        {
            var src = new ResultTableSource(sets[_active]);
            Table.Table = src;
            ApplyColumnStyles(src);
            Table.SelectedRow    = 0;
            Table.SelectedColumn = 0;
            Table.RowOffset      = 0;
            Table.ColumnOffset   = 0;
            Table.Visible        = true;
            Messages.Visible     = false;
        }
        else
        {
            Table.Visible    = false;
            Messages.Visible = true;
            Messages.CursorPosition = new System.Drawing.Point(0, 0);
        }
        UpdateTitle();
        Frame.SetNeedsDraw();
        Changed?.Invoke();
    }

    public void UpdateTitle()
    {
        if (_result is null) { Frame.Title = " results "; return; }

        var parts = new List<string>();
        var sets  = _result.ResultSets;
        for (int i = 0; i < sets.Count; i++)
        {
            if (sets.Count > 4 && i != _active) continue; // too many to list: show only the active one
            var rows  = sets[i].Rows.Count;
            var label = sets.Count > 4 ? $"result {i + 1}/{sets.Count}" : sets.Count > 1 ? $"result {i + 1}" : "result";
            var text  = $"{label} · {rows:N0} row{(rows == 1 ? "" : "s")}{(sets[i].Truncated ? "+" : "")}";
            if (i == _active && Source?.Filter is { } f) text += $" · filter \"{f}\" → {Source.Rows:N0}";
            parts.Add(i == _active ? $"▶ {text}" : text);
        }

        var msgCount = _result.Messages.Count;
        var msgLabel = _result.HasErrors ? $"messages ({_result.ErrorCount} error{(_result.ErrorCount == 1 ? "" : "s")})" : $"messages ({msgCount})";
        parts.Add(_active >= sets.Count ? $"▶ {msgLabel}" : msgLabel);

        Frame.Title = " " + string.Join("  │  ", parts) + (sets.Count > 0 ? "   F7 ⇄ " : " ");
    }

    private void BuildMessages(ExecutionResult r)
    {
        var lines   = new List<string>();
        var colors  = new List<Theme.Msg>();
        var targets = new List<int?>();
        int width   = Math.Max(20, (Messages.Viewport.Width > 0 ? Messages.Viewport.Width : Application.Screen.Width - 4) - 1);

        void Add(string text, Theme.Msg color, int? target = null)
        {
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                do
                {
                    // Hard-wrap long lines ourselves; continuation rows keep the same colour/target.
                    int take = l.Length;
                    if (take > width)
                    {
                        int space = l.LastIndexOf(' ', width - 1);   // break at a word when it's not too far back
                        take = space > width / 2 ? space + 1 : width;
                    }
                    lines.Add(l[..take].TrimEnd());
                    colors.Add(color);
                    targets.Add(target);
                    l = l[take..];
                }
                while (l.Length > 0);
            }
        }

        Add($"[{DateTime.Now:HH:mm:ss}]  {(r.Cancelled ? "cancelled" : r.HasErrors ? "finished with errors" : "completed")} in {Fmt.Elapsed(r.Elapsed)}", Theme.Msg.Head);
        Add("", Theme.Msg.Info);
        foreach (var m in r.Messages)
        {
            Add(m.Text, m.Kind switch
            {
                MessageKind.Error        => Theme.Msg.Error,
                MessageKind.RowsAffected => Theme.Msg.Rows,
                _                        => Theme.Msg.Info
            }, m.Line);
        }
        foreach (var rs in r.ResultSets.Where(rs => rs.Truncated))
            Add($"Note: a result set was capped at {rs.Rows.Count:N0} rows; {rs.SkippedRows:N0} more were skipped (raise maxRows in ~/.sqlr/settings.json).", Theme.Msg.Error);

        if (r.ErrorLine is int line)
        {
            Add("", Theme.Msg.Info);
            Add($"Enter on an error line jumps to it in the editor (first error: line {line}).", Theme.Msg.Rows);
        }

        _messageLineColors  = colors;
        _messageLineTargets = targets;
        Messages.Text       = string.Join("\n", lines);
    }

    /// <summary>The editor line the message under the cursor refers to, if any.</summary>
    public int? LineAtMessageCursor()
    {
        int row = Messages.CurrentRow;
        return row >= 0 && row < _messageLineTargets.Count ? _messageLineTargets[row] : null;
    }

    // ── TableView style ────────────────────────────────────────────────────

    public static void ConfigureTableStyle(TableView tv)
    {
        tv.Style.ShowHeaders                    = true;
        tv.Style.AlwaysShowHeaders              = true;
        tv.Style.ShowHorizontalHeaderOverline   = true;
        tv.Style.ShowHorizontalHeaderUnderline  = true;
        tv.Style.ShowHorizontalScrollIndicators = true;
        tv.Style.ExpandLastColumn               = false;
        tv.Style.SmoothHorizontalScrolling      = true;
        tv.Style.RowColorGetter = args =>
            args.RowIndex % 2 == 0 ? Theme.OddRow : Theme.EvenRow;
    }

    private void ApplyColumnStyles(ResultTableSource src)
    {
        Table.Style.ColumnStyles.Clear();
        for (int i = 0; i < src.Columns; i++)
        {
            var style = Table.Style.GetOrCreateColumnStyle(i);
            style.MaxWidth = _maxColumnWidth;
            if (src.Data.IsNumeric(i))
                style.Alignment = Alignment.End;
            style.RepresentationGetter = v => v is string s ? ValueFormatter.ForGrid(s) : v?.ToString() ?? "";
            style.ColorGetter = args =>
                args.CellValue is null or DBNull ? Theme.NullCell(args.RowIndex % 2 == 0) : null;
        }
    }
}

internal static class Fmt
{
    public static string Elapsed(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalSeconds < 60      ? $"{t.TotalSeconds:0.00} s"
        : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";

    public static string Plural(long n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    public static string OneLine(string sql, int maxLen)
    {
        var flat = Regex.Replace(sql, @"\s+", " ").Trim();
        return flat.Length > maxLen ? flat[..maxLen] + "…" : flat;
    }
}
