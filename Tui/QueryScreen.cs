using System.Data;
using Sqlr.Config;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

public static class QueryScreen
{
    private static int  _lastRowCount;
    private static long _lastElapsedMs;

    private static bool             _suppressCompletion;
    private static int              _popupSelectedIndex;
    private static List<Completion> _popupItems = [];
    private static DataTable?       _currentTable;

    public static void Run(SqlrConnection conn, SqlRunner runner, DatabaseSchema schema)
    {
        _lastRowCount  = 0;
        _lastElapsedMs = 0;
        _currentTable  = null;

        var completer = new SqlCompleter(schema);

        var top = new Toplevel { ColorScheme = Theme.Base };

        // ── Title strip ────────────────────────────────────────────────────
        var titleLabel = new Label
        {
            Text        = $"  ◈ sqlr   {conn.Name}  ·  {conn.Server} / {conn.Database}  ·  {schema.Tables.Count} tables",
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            ColorScheme = Theme.TitleBar
        };

        // ── Results pane ───────────────────────────────────────────────────
        var resultsFrame = new FrameView
        {
            Title       = " results   Tab→editor ",
            X           = 0, Y = 1,
            Width       = Dim.Fill(),
            Height      = Dim.Percent(70),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(resultsFrame);

        var tableView = new TableView
        {
            X             = 0, Y = 0,
            Width         = Dim.Fill(),
            Height        = Dim.Fill(),
            ColorScheme   = Theme.Base,
            FullRowSelect = false,
            MultiSelect   = false
        };
        ConfigureTableStyle(tableView);
        resultsFrame.Add(tableView);


        // ── SQL input pane ─────────────────────────────────────────────────
        var inputFrame = new FrameView
        {
            Title       = " sql   F5=Execute   F2=Clear   F12=Peek   Tab=Results   Ctrl+Q=Quit ",
            X           = 0,
            Y           = Pos.Bottom(resultsFrame),
            Width       = Dim.Fill(),
            Height      = Dim.Fill()! - 1,
            ColorScheme = Theme.Input
        };
        Theme.SetRounded(inputFrame);

        var sqlInput = new TextView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Input,
            AllowsTab   = false   // Tab switches panes
        };
        inputFrame.Add(sqlInput);
        SqlSyntaxHighlighter.Attach(sqlInput);

        // ── Status bar ─────────────────────────────────────────────────────
        var statusLabel = new Label
        {
            Text        = $"Ready  |  {schema.DatabaseName}  ·  {schema.Tables.Count} tables  ·  {schema.Columns.Count} columns",
            X           = 0,
            Y           = Pos.AnchorEnd(1),
            Width       = Dim.Fill(),
            ColorScheme = Theme.StatusBar
        };

        // ── Completion popup ───────────────────────────────────────────────
        // Added LAST so it renders on top of everything else
        var popupFrame = new FrameView
        {
            Title       = " ↑↓ navigate   Tab/Enter accept   Esc dismiss ",
            X           = 1,
            Y           = 2,      // repositioned dynamically before show
            Width       = 58,
            Height      = 12,
            Visible     = false,
            ColorScheme = Theme.Popup
        };
        Theme.SetRounded(popupFrame);

        var popupList = new ListView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Popup,
            CanFocus    = false   // focus stays in sqlInput at all times
        };
        popupFrame.Add(popupList);

        // Order matters for z-index — popup must be added last
        top.Add(titleLabel, resultsFrame, inputFrame, statusLabel, popupFrame);

        // ── Right-click: copy cell value to clipboard ─────────────────────
        tableView.MouseClick += (_, e) =>
        {
            if (!e.Flags.HasFlag(MouseFlags.Button3Clicked)) return;
            if (_currentTable is null) return;
            var row = tableView.SelectedRow;
            var col = tableView.SelectedColumn;
            if (row < 0 || row >= _currentTable.Rows.Count) return;
            if (col < 0 || col >= _currentTable.Columns.Count) return;
            var value = _currentTable.Rows[row][col]?.ToString() ?? "";
            Clipboard.TrySetClipboardData(value);
            var preview = value.Length > 50 ? value[..50] + "…" : value;
            statusLabel.Text = $"Copied: {preview}";
            statusLabel.SetNeedsDraw();
        };

        // ── Cell-position tracker ──────────────────────────────────────────
        tableView.SelectedCellChanged += (_, e) =>
        {
            if (tableView.Table is null) return;
            statusLabel.Text =
                $"{_lastRowCount} rows  {_lastElapsedMs}ms  |  " +
                $"Row {e.NewRow + 1}/{tableView.Table.Rows}  Col {e.NewCol + 1}/{tableView.Table.Columns}";
            statusLabel.SetNeedsDraw();
        };

        // ── SQL input key handling ─────────────────────────────────────────
        sqlInput.KeyDown += (_, key) =>
        {
            // ── Popup is open: intercept navigation ────────────────────────
            if (popupFrame.Visible)
            {
                switch (key.KeyCode)
                {
                    case KeyCode.CursorDown:
                        MovePopupSelection(+1, popupList);
                        return;
                    case KeyCode.CursorUp:
                        MovePopupSelection(-1, popupList);
                        return;
                    case KeyCode.Tab:
                    case KeyCode.Enter:
                        AcceptCompletion(sqlInput, popupFrame);
                        return;
                    case KeyCode.Esc:
                        HidePopup(popupFrame);
                        return;
                    case KeyCode.F5:
                    case KeyCode.F2:
                    case KeyCode.F12:
                        HidePopup(popupFrame);
                        break;  // fall through to handle below
                }
            }

            // ── Functional keys ────────────────────────────────────────────
            switch (key.KeyCode)
            {
                case KeyCode.Tab:
                    HidePopup(popupFrame);
                    tableView.SetFocus();
                    return;

                case KeyCode.F5:
                {
                    var sql = sqlInput.Text?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(sql))
                    {
                        HidePopup(popupFrame);
                        _ = ExecuteQueryAsync(sql, tableView, statusLabel, runner);
                    }
                    return;
                }

                case KeyCode.F2:
                    tableView.Table = null;
                    _lastRowCount   = 0;
                    _lastElapsedMs  = 0;
                    statusLabel.Text = "Cleared.";
                    statusLabel.SetNeedsDraw();
                    HidePopup(popupFrame);
                    return;

                case KeyCode.F12:
                {
                    var word = GetWordUnderCursor(sqlInput);
                    if (string.IsNullOrWhiteSpace(word))
                    {
                        statusLabel.Text = "No identifier at cursor.";
                        statusLabel.SetNeedsDraw();
                    }
                    else
                    {
                        _ = ShowTableStructureAsync(word, statusLabel, runner, schema);
                    }
                    return;
                }
            }

            if (key.KeyCode == (KeyCode.Q | KeyCode.CtrlMask))
            {
                Application.RequestStop();
                return;
            }

            // ── Ctrl+Backspace → delete word left ──────────────────────────
            // Check both the WithCtrl equality and the raw flag OR — terminals vary
            if (key == Key.Backspace.WithCtrl ||
                key.KeyCode == (KeyCode.Backspace | KeyCode.CtrlMask) ||
                (key.IsCtrl && key.KeyCode == KeyCode.Backspace))
            {
                key.Handled = true;
                DeleteWordLeft(sqlInput);
                Application.Invoke(() =>
                    RefreshPopup(sqlInput, popupFrame, popupList, completer, resultsFrame));
                return;
            }

            // ── Ctrl+Delete → delete word right ───────────────────────────
            if (key == Key.Delete.WithCtrl ||
                key.KeyCode == (KeyCode.Delete | KeyCode.CtrlMask) ||
                (key.IsCtrl && key.KeyCode == KeyCode.Delete))
            {
                key.Handled = true;
                DeleteWordRight(sqlInput);
                Application.Invoke(() =>
                    RefreshPopup(sqlInput, popupFrame, popupList, completer, resultsFrame));
                return;
            }

            // ── Completion trigger ─────────────────────────────────────────
            // Defer until AFTER the key has been processed by the TextView
            // (so Text and CurrentRow/CurrentColumn are already updated)
            if (ShouldTriggerCompletion(key))
            {
                Application.Invoke(() =>
                {
                    if (!_suppressCompletion)
                        RefreshPopup(sqlInput, popupFrame, popupList, completer, resultsFrame);
                });
            }
            else if (ShouldHideCompletion(key))
            {
                Application.Invoke(() => HidePopup(popupFrame));
            }
        };

        // ── Results pane: Tab returns to SQL editor ────────────────────────
        tableView.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Tab)
                sqlInput.SetFocus();
        };

        // ── Ctrl+Q anywhere ────────────────────────────────────────────────
        top.KeyDown += (_, key) =>
        {
            if (key.KeyCode == (KeyCode.Q | KeyCode.CtrlMask))
                Application.RequestStop();
        };

        sqlInput.SetFocus();
        Application.Run(top);
        top.Dispose();
    }

    // ── Table structure peek (F12) ─────────────────────────────────────────

    private static string GetWordUnderCursor(TextView tv)
    {
        var text  = tv.Text ?? "";
        var lines = text.Split('\n');
        var row   = tv.CurrentRow;
        if (row >= lines.Length) return "";

        var line = lines[row];
        var col  = Math.Min(tv.CurrentColumn, line.Length);

        static bool IsIdentChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '.' || c == '[' || c == ']';

        // If cursor is past last char or not on an ident char, check one position back
        if (col >= line.Length || !IsIdentChar(line[col]))
        {
            if (col > 0 && IsIdentChar(line[col - 1]))
                col--;
            else
                return "";
        }

        int start = col;
        while (start > 0 && IsIdentChar(line[start - 1]))
            start--;

        int end = col + 1;
        while (end < line.Length && IsIdentChar(line[end]))
            end++;

        return line[start..end].Trim('.');
    }

    private static async Task ShowTableStructureAsync(
        string word, Label statusLabel, SqlRunner runner, DatabaseSchema schema)
    {
        // Parse multi-part name: db..table  schema.table  table
        var parts      = word.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rawTable   = parts[^1].Trim('[', ']');
        var rawSchema  = parts.Length >= 2 ? parts[^2].Trim('[', ']') : "";

        // If the "schema" segment is actually a database name, discard it
        if (!string.IsNullOrEmpty(rawSchema) &&
            !schema.Schemas.Contains(rawSchema, StringComparer.OrdinalIgnoreCase))
            rawSchema = "";

        // Sanitize: strip non-word chars so we can interpolate safely
        var tableName  = System.Text.RegularExpressions.Regex.Replace(rawTable,  @"[^\w#]", "");
        var schemaName = System.Text.RegularExpressions.Regex.Replace(rawSchema, @"[^\w#]", "");

        if (string.IsNullOrEmpty(tableName)) return;

        Application.Invoke(() =>
        {
            statusLabel.Text = $"Looking up: {tableName}…";
            statusLabel.SetNeedsDraw();
            Application.LayoutAndDraw();
        });

        var schemaFilter = string.IsNullOrEmpty(schemaName)
            ? ""
            : $"AND c.TABLE_SCHEMA = '{schemaName}'";

        var sql = $"""
            SELECT
                c.COLUMN_NAME AS [Column],
                CASE
                    WHEN c.CHARACTER_MAXIMUM_LENGTH = -1
                        THEN c.DATA_TYPE + '(max)'
                    WHEN c.CHARACTER_MAXIMUM_LENGTH IS NOT NULL
                        THEN c.DATA_TYPE + '(' + CAST(c.CHARACTER_MAXIMUM_LENGTH AS varchar) + ')'
                    WHEN c.DATA_TYPE IN ('decimal','numeric') AND c.NUMERIC_PRECISION IS NOT NULL
                        THEN c.DATA_TYPE + '(' + CAST(c.NUMERIC_PRECISION AS varchar)
                             + ',' + CAST(ISNULL(c.NUMERIC_SCALE,0) AS varchar) + ')'
                    ELSE c.DATA_TYPE
                END AS [Type],
                c.IS_NULLABLE AS [Nullable],
                ISNULL(c.COLUMN_DEFAULT, '') AS [Default],
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 'PK' ELSE '' END AS [Key]
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    ON  tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                    AND tc.TABLE_SCHEMA    = ku.TABLE_SCHEMA
                    AND tc.TABLE_NAME      = ku.TABLE_NAME
                    AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA
                AND pk.TABLE_NAME   = c.TABLE_NAME
                AND pk.COLUMN_NAME  = c.COLUMN_NAME
            WHERE c.TABLE_NAME = '{tableName}'
            {schemaFilter}
            ORDER BY c.ORDINAL_POSITION
            """;

        var result = await runner.ExecuteAsync(sql);

        Application.Invoke(() =>
        {
            if (!result.IsSuccess)
            {
                statusLabel.Text = $"Peek error: {result.Error}";
                statusLabel.SetNeedsDraw();
                return;
            }

            if (result.Data is null || result.Data.Rows.Count == 0)
            {
                statusLabel.Text = $"'{tableName}' not found in schema.";
                statusLabel.SetNeedsDraw();
                return;
            }

            var tableInfo = schema.Tables
                .FirstOrDefault(t => t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                                  && (string.IsNullOrEmpty(schemaName)
                                      || t.Schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase)));

            var kind     = tableInfo?.IsView == true ? "View" : "Table";
            var fullName = tableInfo?.FullName ?? tableName;

            ShowStructureDialog(fullName, kind, result.Data);

            statusLabel.Text = $"{kind}: {fullName}  ·  {result.Data.Rows.Count} columns";
            statusLabel.SetNeedsDraw();
        });
    }

    private static void ShowStructureDialog(string fullName, string kind, DataTable data)
    {
        var dialog = new Dialog
        {
            Title       = $" {kind}: {fullName} ",
            Width       = Dim.Percent(85),
            Height      = Dim.Percent(75),
            ColorScheme = Theme.Base,
        };

        var tv = new TableView
        {
            X             = 0, Y = 0,
            Width         = Dim.Fill(),
            Height        = Dim.Fill(),
            ColorScheme   = Theme.Base,
            FullRowSelect = true,
            MultiSelect   = false,
        };
        ConfigureTableStyle(tv);
        tv.Table = new DataTableSource(data);
        dialog.Add(tv);

        var closeBtn = new Button { Text = "Close" };
        closeBtn.Accepting += (_, _) => Application.RequestStop();
        dialog.AddButton(closeBtn);

        tv.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
    }

    // ── Key classification helpers ─────────────────────────────────────────

    private static bool ShouldTriggerCompletion(Key key)
    {
        var kc = key.KeyCode;

        // Explicit trigger
        if (kc == (KeyCode.Space | KeyCode.CtrlMask)) return true;

        // Block control / function keys
        if (kc == KeyCode.Esc || kc == KeyCode.Tab) return false;
        if (kc >= KeyCode.F1  && kc <= KeyCode.F12)  return false;
        if (kc == KeyCode.CursorUp || kc == KeyCode.CursorDown ||
            kc == KeyCode.CursorLeft || kc == KeyCode.CursorRight) return false;
        if (kc == KeyCode.PageUp || kc == KeyCode.PageDown) return false;
        if (kc == KeyCode.Home   || kc == KeyCode.End)      return false;
        if (kc == (KeyCode.Q | KeyCode.CtrlMask)) return false;

        // Printable chars, backspace, space, dot, underscore — all trigger
        return true;
    }

    private static bool ShouldHideCompletion(Key key)
    {
        var kc = key.KeyCode;
        return kc == KeyCode.CursorUp || kc == KeyCode.CursorDown ||
               kc == KeyCode.CursorLeft || kc == KeyCode.CursorRight ||
               kc == KeyCode.PageUp || kc == KeyCode.PageDown ||
               kc == KeyCode.Home || kc == KeyCode.End;
    }

    // ── Completion popup logic ─────────────────────────────────────────────

    private static void RefreshPopup(
        TextView sqlInput,
        FrameView popup,
        ListView popupList,
        SqlCompleter completer,
        FrameView resultsFrame)
    {
        var before = GetTextBeforeCursor(sqlInput);
        var word   = SqlCompleter.ExtractCurrentWord(before);

        // Don't show for empty input or pure whitespace after clearing
        if (string.IsNullOrWhiteSpace(before))
        {
            HidePopup(popup);
            return;
        }

        var completions = completer.GetCompletions(before);
        if (completions.Count == 0)
        {
            HidePopup(popup);
            return;
        }

        _popupItems         = [.. completions];
        _popupSelectedIndex = 0;

        var displayItems = new System.Collections.ObjectModel.ObservableCollection<string>(
            _popupItems.Select(c => c.Display));

        popupList.SetSource(displayItems);
        popupList.SelectedItem = 0;

        // Position just above the input frame
        int visibleCount = Math.Min(_popupItems.Count, 10);
        int popupH       = visibleCount + 2; // +2 for border

        popup.Y      = resultsFrame.Frame.Y + resultsFrame.Frame.Height - popupH;
        popup.Height = popupH;
        popup.Visible = true;
        popup.SetNeedsDraw();
        Application.LayoutAndDraw();
    }

    private static void MovePopupSelection(int delta, ListView popupList)
    {
        if (_popupItems.Count == 0) return;
        _popupSelectedIndex    = Math.Clamp(_popupSelectedIndex + delta, 0, _popupItems.Count - 1);
        popupList.SelectedItem = _popupSelectedIndex;
        popupList.SetNeedsDraw();
        Application.LayoutAndDraw();
    }

    private static void AcceptCompletion(TextView sqlInput, FrameView popup)
    {
        if (_popupItems.Count == 0 || _popupSelectedIndex >= _popupItems.Count)
        {
            popup.Visible = false;
            return;
        }

        var chosen = _popupItems[_popupSelectedIndex].Text;
        var before = GetTextBeforeCursor(sqlInput);
        var word   = SqlCompleter.ExtractCurrentWord(before);

        _suppressCompletion = true;
        try
        {
            var text      = sqlInput.Text ?? "";
            var cursorPos = GetLinearCursorPos(sqlInput);
            var wordStart = cursorPos - word.Length;

            sqlInput.Text = text[..wordStart] + chosen + text[cursorPos..];

            var newLinear  = wordStart + chosen.Length;
            var (row, col) = LinearToRowCol(sqlInput.Text, newLinear);
            sqlInput.CursorPosition = new System.Drawing.Point(col, row);
        }
        finally
        {
            _suppressCompletion = false;
        }

        popup.Visible = false;
        popup.SetNeedsDraw();
        Application.LayoutAndDraw();
    }

    private static void HidePopup(FrameView popup)
    {
        if (!popup.Visible) return;
        popup.Visible = false;
        popup.SetNeedsDraw();
    }

    // ── Text / cursor helpers ──────────────────────────────────────────────

    private static string GetTextBeforeCursor(TextView tv)
    {
        var text = tv.Text ?? "";
        var pos  = GetLinearCursorPos(tv);
        return text[..Math.Min(pos, text.Length)];
    }

    private static int GetLinearCursorPos(TextView tv)
    {
        var text  = tv.Text ?? "";
        var row   = tv.CurrentRow;
        var col   = tv.CurrentColumn;
        var lines = text.Split('\n');

        int pos = 0;
        for (int i = 0; i < Math.Min(row, lines.Length); i++)
            pos += lines[i].Length + 1;

        pos += row < lines.Length ? Math.Min(col, lines[row].Length) : 0;
        return Math.Min(pos, text.Length);
    }

    private static (int row, int col) LinearToRowCol(string text, int linearPos)
    {
        var lines = text.Split('\n');
        int pos = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (pos + lines[i].Length >= linearPos)
                return (i, linearPos - pos);
            pos += lines[i].Length + 1;
        }
        return (lines.Length - 1, lines[^1].Length);
    }

    // ── Word deletion ──────────────────────────────────────────────────────

    /// <summary>Ctrl+Backspace: deletes from start of current word to cursor.</summary>
    private static void DeleteWordLeft(TextView tv)
    {
        var text = tv.Text ?? "";
        var end  = GetLinearCursorPos(tv);
        if (end == 0) return;

        int i = end - 1;
        // Skip trailing whitespace
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        // Skip word characters
        while (i >= 0 && IsWordChar(text[i])) i--;
        int start = i + 1;

        _suppressCompletion = true;
        try
        {
            tv.Text = text[..start] + text[end..];
            var (row, col) = LinearToRowCol(tv.Text, start);
            tv.CursorPosition = new System.Drawing.Point(col, row);
        }
        finally { _suppressCompletion = false; }
    }

    /// <summary>Ctrl+Delete: deletes from cursor to end of current word.</summary>
    private static void DeleteWordRight(TextView tv)
    {
        var text  = tv.Text ?? "";
        var start = GetLinearCursorPos(tv);
        if (start >= text.Length) return;

        int i = start;
        // Skip leading whitespace
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        // Skip word characters
        while (i < text.Length && IsWordChar(text[i])) i++;
        int end = i;

        _suppressCompletion = true;
        try
        {
            tv.Text = text[..start] + text[end..];
            var (row, col) = LinearToRowCol(tv.Text, start);
            tv.CursorPosition = new System.Drawing.Point(col, row);
        }
        finally { _suppressCompletion = false; }
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@';

    // ── Query execution ────────────────────────────────────────────────────

    private static async Task ExecuteQueryAsync(
        string sql,
        TableView tableView,
        Label statusLabel,
        SqlRunner runner)
    {
        Application.Invoke(() =>
        {
            statusLabel.Text = $"Executing: {BuildPreview(sql, 80)}";
            statusLabel.SetNeedsDraw();
            Application.LayoutAndDraw();
        });

        var result = await runner.ExecuteAsync(sql);

        Application.Invoke(() =>
        {
            if (result.IsSuccess)
            {
                _lastRowCount  = result.RowCount;
                _lastElapsedMs = result.ElapsedMs;
                _currentTable  = result.Data;
                tableView.Table          = result.Data is not null ? new DataTableSource(result.Data) : null;
                tableView.SelectedRow    = 0;
                tableView.SelectedColumn = 0;
                statusLabel.Text = $"{result.RowCount} rows  {result.ElapsedMs}ms  |  {BuildPreview(sql, 60)}";
                tableView.SetFocus();
            }
            else
            {
                _lastElapsedMs = result.ElapsedMs;
                MessageBox.ErrorQuery("SQL Error", result.Error!, "OK");
                statusLabel.Text = $"Error ({result.ElapsedMs}ms)";
            }
            statusLabel.SetNeedsDraw();
            Application.LayoutAndDraw();
        });
    }

    private static string BuildPreview(string sql, int maxLen)
    {
        var flat = sql.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length > maxLen ? flat[..maxLen] + "…" : flat;
    }

    // ── TableView style ────────────────────────────────────────────────────

    private static void ConfigureTableStyle(TableView tv)
    {
        tv.Style.ShowHeaders                    = true;
        tv.Style.AlwaysShowHeaders              = true;
        tv.Style.ShowHorizontalHeaderOverline   = true;
        tv.Style.ShowHorizontalHeaderUnderline  = true;
        tv.Style.ShowHorizontalScrollIndicators = true;
        tv.Style.ExpandLastColumn               = false;
        tv.Style.RowColorGetter = args =>
            args.RowIndex % 2 == 0 ? Theme.OddRow : Theme.EvenRow;
    }
}
