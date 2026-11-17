using System.Drawing;
using Sqlr.Config;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

public enum ScreenExit { Quit, SwitchConnection }

/// <summary>
/// The main editor + results screen for one connection.
/// </summary>
public sealed class QueryScreen
{
    private static readonly int[]    SplitSizes = [70, 45, 20];
    private static readonly string[] Spinner    = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private enum StatusKind { Normal, Ok, Error, Busy }

    private readonly SqlrConnection _conn;
    private readonly SqlRunner      _runner;
    private readonly SchemaService  _schema;
    private readonly SqlCompleter   _completer;
    private readonly QueryHistory   _history;

    private readonly Toplevel        _top;
    private readonly Label           _title;
    private readonly ResultsPane     _results;
    private readonly FrameView       _inputFrame;
    private readonly EditorView      _editor;
    private readonly Label           _status;
    private readonly CompletionPopup _popup;

    private CancellationTokenSource? _cts;
    private Task?      _runTask;
    private bool       _running;
    private DateTime   _runStarted;
    private int        _spinnerFrame;
    private string     _statusText = "";
    private DateTime   _noticeUntil;   // while running, a fresh message briefly replaces the spinner
    private string?    _currentFile;
    private string     _database;
    private int        _splitIndex;
    private ScreenExit _exit = ScreenExit.Quit;
    private bool       _deletePending;
    private bool       _popupChosen;   // user picked an item (↑↓) or asked for the list (Ctrl+Space)

    public QueryScreen(SqlrConnection conn, SqlRunner runner, SchemaService schema, SqlrSettings settings,
                       QueryHistory history, string? initialText = null, string? filePath = null)
    {
        _conn        = conn;
        _runner      = runner;
        _schema      = schema;
        _history     = history;
        _completer   = new SqlCompleter(() => _schema.Current);
        _database    = runner.CurrentDatabase ?? conn.DatabaseLabel;
        _currentFile = filePath;

        _top = new Toplevel { ColorScheme = Theme.Base };

        // ── Title strip ────────────────────────────────────────────────────
        _title = new Label
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            ColorScheme = Theme.TitleBar
        };

        // ── Results pane ───────────────────────────────────────────────────
        _results = new ResultsPane(settings.MaxColumnWidth);

        // ── SQL input pane ─────────────────────────────────────────────────
        _inputFrame = new FrameView
        {
            Title       = " sql   F5 run · F6 block · F3 history · F4 objects · F12 peek · F1 help ",
            X           = 0,
            Y           = Pos.Bottom(_results.Frame),
            Width       = Dim.Fill(),
            Height      = Dim.Fill()! - 1,
            ColorScheme = Theme.Input
        };
        Theme.SetRounded(_inputFrame);

        _editor = new EditorView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Input,
            AllowsTab   = false,   // Tab switches panes / accepts completions
            Text        = initialText ?? ""
        };
        _inputFrame.Add(_editor);
        SqlSyntaxHighlighter.Attach(_editor);

        // ── Status bar ─────────────────────────────────────────────────────
        _status = new Label
        {
            X           = 0,
            Y           = Pos.AnchorEnd(1),
            Width       = Dim.Fill(),
            ColorScheme = Theme.StatusBar
        };

        // ── Completion popup ── added LAST so it renders on top of everything else
        _popup = new CompletionPopup();
        _top.Add(_title, _results.Frame, _inputFrame, _status, _popup.View);

        WireEvents();

        if (!string.IsNullOrEmpty(initialText))
            EditorText.MoveCursor(_editor, _editor.Lines - 1, int.MaxValue / 2);
        _editor.SetFocus();

        UpdateTitle();
        SetStatus(_schema.IsLoaded ? "Ready." : "Ready — loading schema for completions…");
    }

    /// <summary>Marshals work from background threads onto the UI thread (replaceable in tests).</summary>
    internal Action<Action> Dispatch { get; set; } = Application.Invoke;

    // Exposed for UI tests
    internal Toplevel        Top     => _top;
    internal TextView        Editor  => _editor;
    internal ResultsPane     Results => _results;
    internal CompletionPopup Popup   => _popup;
    internal string          StatusText => _status.Text ?? "";

    public static ScreenExit Run(SqlrConnection conn, SqlRunner runner, SchemaService schema, SqlrSettings settings,
                                 QueryHistory history, string? initialText = null, string? filePath = null) =>
        new QueryScreen(conn, runner, schema, settings, history, initialText, filePath).Run();

    public ScreenExit Run()
    {
        Application.Run(_top, ex =>
        {
            // Never let a bug in a handler kill the session (and the user's unsaved query).
            SqlrPaths.LogError(ex);
            SetStatus($"Internal error: {ex.Message} (details in {SqlrPaths.ErrorLog})", StatusKind.Error);
            return true;
        });
        Shutdown();
        _top.Dispose();
        return _exit;
    }

    internal void Shutdown()
    {
        _cts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancelled */ }
        Drafts.Save(_conn.Name, _editor.Text ?? "");
    }

    // ── Wiring ─────────────────────────────────────────────────────────────

    private void WireEvents()
    {
        _editor.PreviewKey           = OnEditorKey;
        _editor.CharTyped           += AfterTyped;
        _results.Table.PreviewKey    = OnTableKey;
        _results.Messages.PreviewKey = OnMessagesKey;
        _top.KeyDown                += (_, k) => { if (!k.Handled) HandleGlobalKey(k); };

        _editor.UnwrappedCursorPosition += (_, _) => RefreshStatus();
        _editor.ContentsChanged         += (_, _) =>
        {
            UpdateTitle();
            // Backspace/Delete are applied by the TextView itself; refresh the list afterwards.
            if (_deletePending)
            {
                _deletePending = false;
                RefreshOpenPopup();
            }
        };
        _editor.HasFocusChanged         += (_, _) => RefreshStatus();
        _results.Table.SelectedCellChanged += (_, _) => RefreshStatus();
        _results.Table.HasFocusChanged     += (_, _) => RefreshStatus();
        _results.Changed += RefreshStatus;
        _top.FrameChanged += (_, _) => { _popup.Hide(); RefreshStatus(); };

        _results.Table.CellActivated += (_, e) =>
        {
            if (_results.Source is { } src) Dialogs.ShowCell(src, e.Row, e.Col);
        };

        // ── Right-click: copy cell value to clipboard ─────────────────────
        _results.Table.MouseClick += (_, e) =>
        {
            if (!e.Flags.HasFlag(MouseFlags.Button3Clicked) || _results.Source is null) return;
            if (_results.Table.ScreenToCell(e.Position) is { } cell)
            {
                _results.Table.SelectedColumn = cell.X;
                _results.Table.SelectedRow    = cell.Y;
            }
            CopyCell();
            e.Handled = true;
        };

        _schema.Loaded += s => Dispatch(() =>
        {
            UpdateTitle();
            if (s.LoadError is not null)
                SetStatus($"Schema partially loaded: {s.LoadError}", StatusKind.Error);
            else if (!_running)
                SetStatus($"Schema loaded: {s.UserTables.Count(t => !t.IsView):N0} tables, {s.UserTables.Count(t => t.IsView):N0} views, {Fmt.Plural(s.Routines.Count, "routine")}.");
        });
    }

    // ── Keys ───────────────────────────────────────────────────────────────

    private static bool Is(Key k, KeyCode code) => k.KeyCode == code;

    /// <summary>Keys that work from any pane. Returns true (and marks the key handled) when used.</summary>
    private bool HandleGlobalKey(Key k)
    {
        bool handled = true;
        var  kc      = k.KeyCode;

        if      (kc == KeyCode.F1)                                   Dialogs.ShowText("keys", Dialogs.HelpText);
        else if (kc == KeyCode.F2)                                   ClearResults();
        else if (kc == KeyCode.F3)                                   ShowHistory();
        else if (kc == KeyCode.F4)                                   ShowObjectExplorer();
        else if (kc == KeyCode.F5 || Is(k, KeyCode.R | KeyCode.CtrlMask))  RunQuery(blockOnly: false);
        else if (kc == KeyCode.F6 || Is(k, KeyCode.E | KeyCode.CtrlMask))  RunQuery(blockOnly: true);
        else if (kc == KeyCode.F7)                                   _results.Cycle(+1);
        else if (kc == (KeyCode.F7 | KeyCode.ShiftMask))             _results.Cycle(-1);
        else if (kc == KeyCode.F8)                                   CycleSplit();
        else if (kc == KeyCode.F10)                                  SwitchConnection();
        else if (kc == KeyCode.F12)                                  _ = PeekAsync();
        else if (Is(k, KeyCode.Q | KeyCode.CtrlMask))                Quit();
        else if (Is(k, KeyCode.S | KeyCode.CtrlMask))                SaveFile();
        else if (Is(k, KeyCode.O | KeyCode.CtrlMask))                OpenFile();
        else if (kc == KeyCode.Esc)
        {
            // Esc is Terminal.Gui's default quit key — always swallow it here so it never exits.
            if (_running) CancelQuery();
            else if (!_editor.HasFocus) _editor.SetFocus();
        }
        else handled = false;

        if (handled)
        {
            _popup.Hide();
            k.Handled = true;
        }
        return handled;
    }

    private bool OnEditorKey(Key k)
    {
        var kc = k.KeyCode;

        // Some terminals/input paths emit a stray "\r " outside the draw cycle (seen after Ctrl+W),
        // blanking the frame's left border on the cursor row. Repainting the small editor frame
        // on every key keeps the screen consistent whatever the cause.
        _inputFrame.SetNeedsDraw();

        // ── Popup is open: it owns navigation keys ─────────────────────────
        if (_popup.IsOpen)
        {
            switch (kc)
            {
                case KeyCode.CursorDown: _popup.Move(+1); _popupChosen = true; return true;
                case KeyCode.CursorUp:   _popup.Move(-1); _popupChosen = true; return true;
                case KeyCode.PageDown:   _popup.Page(+1); _popupChosen = true; return true;
                case KeyCode.PageUp:     _popup.Page(-1); _popupChosen = true; return true;
                case KeyCode.Esc:        _popup.Hide();   return true;
                case KeyCode.Tab:
                    AcceptCompletion();
                    return true;
                case KeyCode.Enter:
                    // Enter only commits a suggestion you deliberately picked; otherwise it's a
                    // newline (typing "FROM t o⏎" must not turn the alias into "ON").
                    if (!_popupChosen)
                    {
                        _popup.Hide();
                        return false; // let the TextView insert the newline
                    }
                    AcceptCompletion();
                    return true;
                case KeyCode.CursorLeft:
                case KeyCode.CursorRight:
                case KeyCode.Home:
                case KeyCode.End:
                    _popup.Hide();
                    break;
            }
        }

        if (HandleGlobalKey(k)) return true;

        // ── Editor-only keys ───────────────────────────────────────────────
        if (kc == KeyCode.Tab)
        {
            FocusResults();
            return true;
        }

        if (IsCompletionTrigger(k))
        {
            ShowCompletions(force: true);
            _popupChosen = _popup.IsOpen;
            return true;
        }

        // Ctrl+Backspace / Ctrl+W / Alt+Backspace → delete word left
        if (Is(k, KeyCode.Backspace | KeyCode.CtrlMask) || Is(k, KeyCode.W | KeyCode.CtrlMask) ||
            Is(k, KeyCode.Backspace | KeyCode.AltMask)  || Is(k, (KeyCode)8 | KeyCode.CtrlMask))
        {
            EditorText.DeleteWordLeft(_editor);
            RefreshOpenPopup();
            return true;
        }

        // Ctrl+Delete / Alt+D → delete word right
        if (Is(k, KeyCode.Delete | KeyCode.CtrlMask) || Is(k, KeyCode.D | KeyCode.AltMask))
        {
            EditorText.DeleteWordRight(_editor);
            RefreshOpenPopup();
            return true;
        }

        // Ctrl+/ (terminals also send it as Ctrl+_ or Ctrl+7) or Alt+/ → toggle comment
        if (Is(k, (KeyCode)'/' | KeyCode.CtrlMask) || Is(k, (KeyCode)'_' | KeyCode.CtrlMask) ||
            Is(k, KeyCode.D7 | KeyCode.CtrlMask)  || Is(k, (KeyCode)'/' | KeyCode.AltMask) ||
            Is(k, (KeyCode)31))
        {
            _popup.Hide();
            EditorText.ToggleLineComment(_editor);
            return true;
        }

        // ── Completion: refreshed after the TextView has applied the key ──
        // (printable characters arrive via CharTyped, deletions via ContentsChanged)
        if (kc is KeyCode.Backspace or KeyCode.Delete)
            _deletePending = _popup.IsOpen;
        else if (kc is KeyCode.CursorUp or KeyCode.CursorDown or KeyCode.CursorLeft or KeyCode.CursorRight
                    or KeyCode.PageUp or KeyCode.PageDown or KeyCode.Home or KeyCode.End or KeyCode.Enter
                 || k.IsCtrl || k.IsAlt)
            _popup.Hide();

        return false;
    }

    private static bool IsCompletionTrigger(Key k) =>
        Is(k, KeyCode.Space | KeyCode.CtrlMask) || Is(k, (KeyCode)'@' | KeyCode.CtrlMask) || Is(k, KeyCode.Null);

    private bool OnTableKey(Key k)
    {
        var src = _results.Source;

        // Esc in a filtered grid clears the filter first (a second Esc returns to the editor).
        if (k.KeyCode == KeyCode.Esc && !_running && src?.Filter is not null)
        {
            src.ApplyFilter(null);
            _results.Table.Update();
            _results.UpdateTitle();
            _results.Table.SetNeedsDraw();
            SetStatus("Filter cleared.");
            return true;
        }

        if (HandleGlobalKey(k)) return true;

        if (k.KeyCode == KeyCode.Tab)
        {
            _editor.SetFocus();
            return true;
        }

        if (k.IsAlt || src is null) return false;

        if (Is(k, KeyCode.C | KeyCode.CtrlMask)) { CopyCell(); return true; }
        if (k.IsCtrl) return false;

        switch (k.KeyCode)
        {
            case KeyCode.C: CopyCell(); return true;
            case KeyCode.R: CopyRow();  return true;
            case KeyCode.A: CopyAll();  return true;
            case KeyCode.S: Sort();     return true;
            case KeyCode.E: ExportResults(); return true;
        }
        if (k.AsRune.Value == '/') { FilterResults(); return true; }

        // Swallow other printable keys so type-to-search doesn't jump around unexpectedly.
        return k.AsRune.Value > 31 && !k.IsShift;
    }

    private bool OnMessagesKey(Key k)
    {
        if (HandleGlobalKey(k)) return true;
        if (k.KeyCode == KeyCode.Tab)
        {
            _editor.SetFocus();
            return true;
        }
        if (k.KeyCode == KeyCode.Enter && _results.LineAtMessageCursor() is int line)
        {
            GoToLine(line);
            return true;
        }
        return false;
    }

    // ── Completion ─────────────────────────────────────────────────────────

    private void AfterTyped(char ch)
    {
        _popupChosen = false;
        if (ch == '.')
        {
            EditorText.ExpandDoubleDot(_editor);
            ShowCompletions(force: false);
        }
        else if (char.IsLetterOrDigit(ch) || ch is '_' or '#' or '@' or '$' or '[')
        {
            ShowCompletions(force: false);
        }
        else
        {
            _popup.Hide();
        }
    }

    private void RefreshOpenPopup()
    {
        if (_popup.IsOpen) ShowCompletions(force: false);
    }

    private void ShowCompletions(bool force)
    {
        var before      = EditorText.TextBeforeCursor(_editor);
        var (start, word) = SqlCompleter.CurrentToken(SqlScript.Mask(before));
        bool dotted     = start < before.Length && before[start] == '[' || start > 0 && before[start - 1] == '.';

        if (!force && word.Length == 0 && !dotted)
        {
            _popup.Hide();
            return;
        }

        var items = _completer.GetCompletions(before, _editor.Text ?? "");

        // Don't nag once the word already matches the only suggestion exactly.
        if (items.Count == 0 || (!force && items.Count == 1 && items[0].InsertText.Equals(word, StringComparison.OrdinalIgnoreCase)))
        {
            _popup.Hide();
            if (force) SetStatus("No completions here.");
            return;
        }

        var anchor = _editor.ViewportToScreen(new Point(
            _editor.CurrentColumn - _editor.LeftColumn - (before.Length - start),
            _editor.CurrentRow - _editor.TopRow));
        _popup.Show(items, anchor, new Size(Application.Screen.Width, Application.Screen.Height - 1));
    }

    private void AcceptCompletion()
    {
        var chosen = _popup.Selected;
        _popup.Hide();
        if (chosen is null) return;

        var text   = _editor.Text ?? "";
        int cursor = EditorText.LinearCursor(_editor);
        // "[Ord|" → the opening bracket is replaced too (the insert text brings its own quoting)
        var (start, _) = SqlCompleter.CurrentToken(SqlScript.Mask(text[..cursor]));
        int end    = cursor;

        // "[Ord|]" → swallow the closing bracket after the cursor when we insert one
        if (chosen.InsertText.EndsWith(']') && end < text.Length && text[end] == ']') end++;

        EditorText.ReplaceRange(_editor, start, end, chosen.InsertText);
    }

    // ── Execution ──────────────────────────────────────────────────────────

    private void RunQuery(bool blockOnly)
    {
        if (_running)
        {
            SetStatus("A query is already running — Esc cancels it.", StatusKind.Busy);
            return;
        }

        var text = _editor.Text ?? "";
        string sql;
        int    offset;

        if (EditorText.Selection(_editor) is { } sel)
        {
            (sql, offset) = sel;
        }
        else if (blockOnly)
        {
            if (SqlScript.BlockAt(text, _editor.CurrentRow) is not { } block)
            {
                SetStatus("No statement under the cursor (blocks are separated by blank lines).", StatusKind.Error);
                return;
            }
            (sql, offset) = block;
        }
        else
        {
            (sql, offset) = (text, 0);
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            SetStatus("Nothing to run.");
            return;
        }

        Drafts.Save(_conn.Name, text);
        Execute(sql, offset);
    }

    private void Execute(string sql, int lineOffset)
    {
        // Every entry point (F5, F6, history, explorer) funnels through here — one run at a time.
        if (_running)
        {
            SetStatus("A query is already running — Esc cancels it.", StatusKind.Busy);
            return;
        }

        _cts?.Dispose();
        _cts        = new CancellationTokenSource();
        _running    = true;
        _runStarted = DateTime.Now;
        _popup.Hide();
        SetStatus("", StatusKind.Busy);

        Application.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            _spinnerFrame++;
            RefreshStatus();
            return _running;
        });

        var token = _cts.Token;
        _runTask = Task.Run(async () =>
        {
            ExecutionResult result;
            try
            {
                result = await _runner.ExecuteAsync(sql, lineOffset, token);
            }
            catch (Exception ex)
            {
                result = new ExecutionResult();
                result.Messages.Add(new SqlMessage(MessageKind.Error, ex.Message));
            }
            Dispatch(() => OnExecuted(sql, result));
        });
    }

    internal void ExecuteForTest(string sql) => Execute(sql, 0);
    internal bool IsRunning => _running;

    private void CancelQuery()
    {
        if (!_running) return;
        _cts?.Cancel();
        SetStatus("Cancelling…", StatusKind.Busy);
    }

    private void OnExecuted(string sql, ExecutionResult r)
    {
        _running = false;

        _results.Load(r);

        try
        {
            _history.Add(new HistoryEntry(DateTime.Now, _conn.Name, r.Database, sql,
                !r.HasErrors && !r.Cancelled, (long)r.Elapsed.TotalMilliseconds, r.ResultSets.Count > 0 ? r.TotalRows : r.RowsAffected));
        }
        catch (Exception ex)
        {
            SqlrPaths.LogError(ex); // history is a convenience — never lose results over it
        }

        var elapsed = Fmt.Elapsed(r.Elapsed);
        if (r.Cancelled)
        {
            SetStatus($"⊘ Cancelled after {elapsed}.", StatusKind.Error);
        }
        else if (r.HasErrors)
        {
            var where = r.ErrorLine is int l ? $" · line {l}" : "";
            SetStatus($"✗ {Fmt.Plural(r.ErrorCount, "error")}{where} · {elapsed}" +
                      (r.ResultSets.Count > 0 ? $" · {Fmt.Plural(r.ResultSets.Count, "result set")} (F7)" : ""), StatusKind.Error);
        }
        else if (r.ResultSets.Count > 0)
        {
            var sets   = r.ResultSets.Count > 1 ? $"{r.ResultSets.Count} result sets · " : "";
            var capped = r.ResultSets.Any(s => s.Truncated) ? $" (capped at {_runner.MaxRows:N0} per set)" : "";
            SetStatus($"✓ {sets}{Fmt.Plural(r.TotalRows, "row")}{capped} · {elapsed}", StatusKind.Ok);
        }
        else
        {
            SetStatus(r.RowsAffected > 0
                ? $"✓ {Fmt.Plural(r.RowsAffected, "row")} affected · {elapsed}"
                : $"✓ Done · {elapsed}", StatusKind.Ok);
        }

        // Focus: results when there are any to look at; the failing line when something broke.
        if (r.HasErrors && r.ErrorLine is int errLine)
            GoToLine(errLine);
        else if (r.ResultSets.Count > 0 && !r.HasErrors)
            _results.Table.SetFocus();

        // Keep completions in step with the session: USE changes the database, DDL changes objects.
        var db = r.Database ?? _database;
        if (!db.Equals(_database, StringComparison.OrdinalIgnoreCase))
        {
            _database = db;
            _schema.Reload(db);
            UpdateTitle();
        }
        else if (!r.Cancelled && SqlScript.MayChangeSchema(sql))
        {
            _schema.Reload(db);
            UpdateTitle();
        }
    }

    private void ClearResults()
    {
        _results.Clear();
        SetStatus("Cleared.");
    }

    // ── Results actions ────────────────────────────────────────────────────

    private void FocusResults()
    {
        _popup.Hide();
        if (_results.ShowingMessages) _results.Messages.SetFocus();
        else _results.Table.SetFocus();
    }

    private bool TryGetCell(out ResultTableSource src, out int row, out int col)
    {
        src = _results.Source!;
        row = _results.Table.SelectedRow;
        col = _results.Table.SelectedColumn;
        return src is not null && row >= 0 && row < src.Rows && col >= 0 && col < src.Columns;
    }

    private void CopyCell()
    {
        if (!TryGetCell(out var src, out var row, out var col)) return;
        var value = src.RowAt(row)[col] ?? "NULL";
        ClipboardHelper.Copy(value);
        SetStatus($"Copied: {Fmt.OneLine(value, 60)}", StatusKind.Ok);
    }

    private void CopyRow()
    {
        if (!TryGetCell(out var src, out var row, out _)) return;
        ClipboardHelper.Copy(ResultExport.RowToTsv(src.RowAt(row)));
        SetStatus($"Copied row {row + 1} ({Fmt.Plural(src.Columns, "column")}, tab-separated).", StatusKind.Ok);
    }

    private void CopyAll()
    {
        if (_results.Source is not { } src) return;
        ClipboardHelper.Copy(ResultExport.Write(ExportFormat.Tsv, src.Data, src.VisibleRows));
        SetStatus($"Copied {Fmt.Plural(src.Rows, "row")} with headers (tab-separated — pastes into Excel).", StatusKind.Ok);
    }

    private void Sort()
    {
        if (!TryGetCell(out var src, out _, out var col)) return;
        src.CycleSort(col);
        _results.Table.Update();
        _results.Table.SelectedRow = 0;
        _results.Table.SetNeedsDraw();
        SetStatus(src.SortColumn < 0
            ? "Original order."
            : $"Sorted by {src.Data.Columns[col]} {(src.SortDescending ? "descending" : "ascending")}.");
    }

    private void FilterResults()
    {
        if (_results.Source is not { } src) return;
        var f = Dialogs.Prompt("filter rows", "Show rows where any cell contains (empty = all rows):", src.Filter ?? "");
        if (f is null) return;
        src.ApplyFilter(f);
        _results.Table.Update();
        _results.Table.SelectedRow = 0;
        _results.UpdateTitle();
        _results.Table.SetNeedsDraw();
        SetStatus(src.Filter is null
            ? "Filter cleared."
            : $"{Fmt.Plural(src.Rows, "row")} match{(src.Rows == 1 ? "es" : "")} \"{src.Filter}\" (/ to change, Esc to clear).");
    }

    private void ExportResults()
    {
        if (_results.Source is not { } src) return;
        var suggested = Path.Combine(Directory.GetCurrentDirectory(), $"{_conn.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var path = Dialogs.Prompt("export results", $"Save {Fmt.Plural(src.Rows, "row")} to:", suggested,
                                  "Format follows the extension: .csv .tsv .json .md .sql");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            path = ExpandPath(path);
            var format = ResultExport.FormatFromPath(path);
            File.WriteAllText(path, ResultExport.Write(format, src.Data.Columns, src.Data.ColumnTypes, src.VisibleRows));
            SetStatus($"Exported {Fmt.Plural(src.Rows, "row")} as {format} → {path}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", StatusKind.Error);
        }
    }

    // ── Peek (F12) ─────────────────────────────────────────────────────────

    private async Task PeekAsync()
    {
        var ident = EditorText.IdentifierUnderCursor(_editor);
        if (string.IsNullOrWhiteSpace(ident))
        {
            SetStatus("Put the cursor on a table, view or procedure name, then F12.", StatusKind.Error);
            return;
        }

        var refs  = SqlCompleter.ExtractTableRefs(SqlScript.Mask(_editor.Text ?? ""));
        var parts = SqlIdent.SplitParts(ident);

        // Try the full name, then without the last part (cursor on "o.Column" → peek o's table).
        for (int len = parts.Count; len >= 1; len--)
        {
            var p      = parts.Take(len).ToList();
            var name   = p[^1];
            var schema = p.Count >= 2 && p[^2].Length > 0 ? p[^2] : null;
            var db     = p.Count >= 3 && p[^3].Length > 0 ? p[^3] : null;

            if (p.Count == 1 && refs.FirstOrDefault(r => r.Alias?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) is { } aliased)
                (db, schema, name) = (aliased.Database, aliased.Schema, aliased.Name);

            SetStatus($"Looking up {(schema is null ? "" : schema + ".")}{name}…", StatusKind.Busy);
            try
            {
                var desc = await ObjectDescriber.DescribeAsync(_conn.BuildConnectionString(db ?? _database), schema, name);
                if (desc is null) continue;
                Dispatch(() =>
                {
                    SetStatus($"{desc.Kind}: {desc.FullName} · {Fmt.Plural(desc.Grid.Rows.Count, desc.Grid.Columns[0] == "Parameter" ? "parameter" : "column")}");
                    Dialogs.ShowStructure(desc);
                });
                return;
            }
            catch (Exception ex)
            {
                Dispatch(() => SetStatus($"Peek failed: {ex.Message}", StatusKind.Error));
                return;
            }
        }

        Dispatch(() => SetStatus($"'{ident}' was not found in {_database}.", StatusKind.Error));
    }

    // ── Object explorer (F4) ───────────────────────────────────────────────

    private sealed record DbObject(string Schema, string Name, string Kind, string Quoted);

    private void ShowObjectExplorer()
    {
        var s = _schema.Current;
        if (!_schema.IsLoaded && s.Tables.Count == 0)
        {
            SetStatus("Schema is still loading — try again in a moment.", StatusKind.Busy);
            return;
        }

        var all = s.UserTables.Select(t => new DbObject(t.Schema, t.Name, t.IsView ? "view" : "table", t.QuotedFullName))
            .Concat(s.Routines.Select(r => new DbObject(r.Schema, r.Name, r.IsProcedure ? "proc" : "function", r.QuotedFullName)))
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int nameW = Math.Min(60, all.Select(o => o.Schema.Length + o.Name.Length + 1).DefaultIfEmpty(10).Max());

        var picked = Dialogs.Pick(
            $"objects in {_database} · {all.Count:N0}",
            "Enter insert name · F5 SELECT TOP 100 · F12 structure · Esc close",
            filter => all
                .Where(o => MatchesAll($"{o.Schema}.{o.Name} {o.Kind}", filter))
                .Select(o => new PickItem<DbObject>($" {($"{o.Schema}.{o.Name}").PadRight(nameW)}  {o.Kind}", o))
                .ToList(),
            KeyCode.F5, KeyCode.F12);

        if (picked is not { } p) return;
        var obj = p.Item;

        switch (p.Key)
        {
            case KeyCode.F5 when obj.Kind is "table" or "view":
                Execute($"SELECT TOP (100) * FROM {obj.Quoted};", 0);
                break;
            case KeyCode.F12:
                _ = PeekObjectAsync(obj.Schema, obj.Name);
                break;
            default:
                _editor.SetFocus();
                _editor.InsertText(obj.Kind == "proc" && EditorText.TextBeforeCursor(_editor).TrimEnd().Length == 0
                    ? $"EXEC {obj.Quoted} "
                    : obj.Quoted);
                break;
        }
    }

    private async Task PeekObjectAsync(string schema, string name)
    {
        try
        {
            var desc = await ObjectDescriber.DescribeAsync(_conn.BuildConnectionString(_database), schema, name);
            Dispatch(() =>
            {
                if (desc is null) SetStatus($"{schema}.{name} no longer exists.", StatusKind.Error);
                else Dialogs.ShowStructure(desc);
            });
        }
        catch (Exception ex)
        {
            Dispatch(() => SetStatus($"Peek failed: {ex.Message}", StatusKind.Error));
        }
    }

    // ── History (F3) ───────────────────────────────────────────────────────

    private void ShowHistory()
    {
        var entries = _history.Entries.Reverse().ToList();
        if (entries.Count == 0)
        {
            SetStatus("No history yet — run something first.");
            return;
        }

        var picked = Dialogs.Pick(
            $"history · {entries.Count:N0}",
            "Enter load into editor · F5 load & run · Esc close",
            filter => entries
                .Where(e => MatchesAll($"{e.Connection} {e.Database} {e.Sql}", filter))
                .Select(e => new PickItem<HistoryEntry>(
                    $" {e.At:MM-dd HH:mm}  {(e.Ok ? "✓" : "✗")} {Fmt.Elapsed(TimeSpan.FromMilliseconds(e.ElapsedMs)),8}  {Trunc(e.Connection, 12),-12}  {Fmt.OneLine(e.Sql, 200)}",
                    e))
                .ToList(),
            KeyCode.F5);

        if (picked is not { } p) return;

        if (!ConfirmReplaceEditor(p.Item.Sql)) return;
        if (p.Key == KeyCode.F5) Execute(p.Item.Sql, 0);
    }

    /// <summary>
    /// Puts <paramref name="sql"/> in the editor. If the editor holds text that isn't in the
    /// history (so would otherwise be lost — replacing text can't be undone), asks first.
    /// </summary>
    private bool ConfirmReplaceEditor(string sql)
    {
        var current = _editor.Text ?? "";
        bool unsaved = !string.IsNullOrWhiteSpace(current)
                       && current.Trim() != sql.Trim()
                       && !_history.Entries.Any(e => e.Sql.Trim() == current.Trim());

        if (unsaved)
        {
            var choice = MessageBox.Query("history",
                "The editor has text that isn't saved or in history.\nReplace it, or append the query below it?",
                "Append", "Replace", "Cancel");
            if (choice == 0)
            {
                LoadEditorText(current.TrimEnd() + "\n\n" + sql);
                EditorText.MoveCursor(_editor, _editor.Lines - 1, int.MaxValue / 2);
                return true;
            }
            if (choice != 1) return false;
        }

        LoadEditorText(sql);
        return true;
    }

    private void LoadEditorText(string text)
    {
        _editor.Text = text;
        EditorText.MoveCursor(_editor, 0, 0);
        _editor.SetFocus();
        UpdateTitle();
    }

    // ── Files ──────────────────────────────────────────────────────────────

    private void SaveFile()
    {
        var path = _currentFile;
        if (path is null)
        {
            path = Dialogs.Prompt("save", "Save the editor to:", Path.Combine(Directory.GetCurrentDirectory(), $"{_conn.Name}.sql"));
            if (string.IsNullOrWhiteSpace(path)) return;
            path = ExpandPath(path);
        }

        try
        {
            File.WriteAllText(path, _editor.Text ?? "");
            _currentFile = path;
            _editor.ClearHistoryChanges();
            UpdateTitle();
            SetStatus($"Saved {path}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", StatusKind.Error);
        }
    }

    private void OpenFile()
    {
        var path = Dialogs.Prompt("open", "Open a .sql file (replaces the editor text — Ctrl+Z won't undo this):",
                                  Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(path)) return;
        path = ExpandPath(path);

        try
        {
            var text = File.ReadAllText(path);
            _currentFile = path;
            LoadEditorText(text);
            SetStatus($"Opened {path}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}", StatusKind.Error);
        }
    }

    private static string ExpandPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith('~'))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(path);
    }

    // ── Layout / lifecycle ─────────────────────────────────────────────────

    private void CycleSplit()
    {
        _splitIndex = (_splitIndex + 1) % SplitSizes.Length;
        _results.Frame.Height = Dim.Percent(SplitSizes[_splitIndex]);
        _top.SetNeedsLayout();
        _top.SetNeedsDraw();
    }

    private void GoToLine(int oneBasedLine)
    {
        _editor.SetFocus();
        EditorText.MoveCursor(_editor, Math.Clamp(oneBasedLine - 1, 0, Math.Max(0, _editor.Lines - 1)), 0);
    }

    private void SwitchConnection()
    {
        CancelQuery();
        _exit = ScreenExit.SwitchConnection;
        Application.RequestStop(_top);
    }

    private void Quit()
    {
        CancelQuery();
        _exit = ScreenExit.Quit;
        Application.RequestStop(_top);
    }

    // ── Title / status ─────────────────────────────────────────────────────

    private void UpdateTitle()
    {
        var s          = _schema.Current;
        var schemaInfo = _schema.IsLoading ? "loading schema…" : $"{s.UserTables.Count():N0} tables";
        var file       = _currentFile is null ? "" : $"  ·  {Path.GetFileName(_currentFile)}{(_editor.IsDirty ? " *" : "")}";
        var auth       = _conn.IsSqlAuth ? _conn.Username : "windows auth";
        _title.Text = $"  ◈ sqlr   {_conn.Name}  ·  {_conn.Server} / {_database}  ·  {auth}  ·  {schemaInfo}{file}";
    }

    private void SetStatus(string text, StatusKind kind = StatusKind.Normal)
    {
        _statusText  = text;
        _noticeUntil = text.Length > 0 ? DateTime.Now.AddSeconds(2.5) : DateTime.MinValue;
        _status.ColorScheme = kind switch
        {
            StatusKind.Ok    => Theme.StatusOk,
            StatusKind.Error => Theme.StatusError,
            StatusKind.Busy  => Theme.StatusBusy,
            _                => Theme.StatusBar
        };
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string left = _statusText;
        if (_running && DateTime.Now >= _noticeUntil)
        {
            var el = DateTime.Now - _runStarted;
            left = $"{Spinner[_spinnerFrame % Spinner.Length]} running {Fmt.Elapsed(el)} · {Fmt.Plural(_runner.RowsFetched, "row")} · Esc to cancel";
        }

        string right;
        if (_results.Table.HasFocus && _results.Source is { } src && src.Rows > 0)
        {
            int r = Math.Clamp(_results.Table.SelectedRow, 0, src.Rows - 1);
            int c = Math.Clamp(_results.Table.SelectedColumn, 0, src.Columns - 1);
            right = $"row {r + 1:N0}/{src.Rows:N0} · col {c + 1}/{src.Columns} {Trunc(src.Data.Columns[c], 20)} ({src.Data.ColumnTypes[c]})";
        }
        else
        {
            right = $"Ln {_editor.CurrentRow + 1}, Col {_editor.CurrentColumn + 1}";
        }
        right += $"  │  {_database} ";

        int width = Math.Max(20, Application.Screen.Width);
        left = " " + left;
        int room = width - right.Length - 1;
        if (left.Length > room) left = room > 1 ? left[..(room - 1)] + "…" : "";
        _status.Text = left.PadRight(Math.Max(0, room)) + " " + right;
        _status.SetNeedsDraw();
    }

    private static bool MatchesAll(string haystack, string filter) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string Trunc(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
}
