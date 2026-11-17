using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Sqlr.Config;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

internal sealed record PickItem<T>(string Display, T Value);

/// <summary>Modal dialogs used by the query screen. All close with Esc.</summary>
internal static class Dialogs
{
    // ── Single-line prompt ─────────────────────────────────────────────────

    public static string? Prompt(string title, string label, string initial = "", string? hint = null)
    {
        string? result = null;
        int width = Math.Clamp(Math.Max(label.Length, initial.Length) + 10, 50, 100);

        var dlg = new Dialog
        {
            Title       = $" {title} ",
            Width       = width,
            Height      = hint is null ? 7 : 8,
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var lbl   = new Label { Text = label, X = 1, Y = 0 };
        var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1), Text = initial, ColorScheme = Theme.Input };
        dlg.Add(lbl, field);
        if (hint is not null)
            dlg.Add(new Label { Text = hint, X = 1, Y = 2, ColorScheme = Theme.StatusBar });

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepting += (_, e) => { result = field.Text ?? ""; e.Cancel = true; Application.RequestStop(); };
        var cancel = new Button { Text = "Cancel" };
        cancel.Accepting += (_, e) => { e.Cancel = true; Application.RequestStop(); };
        dlg.AddButton(ok);
        dlg.AddButton(cancel);

        field.SetFocus();
        field.CursorPosition = initial.Length;
        Application.Run(dlg);
        dlg.Dispose();
        return result;
    }

    // ── Read-only text viewer ──────────────────────────────────────────────

    public static void ShowText(string title, string text, bool sqlHighlight = false)
    {
        var dlg = new Dialog
        {
            Title       = $" {title}   c=copy  Esc=close ",
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var tv = new EditorView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = !sqlHighlight, Text = text, ColorScheme = Theme.Input
        };
        if (sqlHighlight) SqlSyntaxHighlighter.Attach(tv);
        tv.PreviewKey = k =>
        {
            if (k.KeyCode != KeyCode.C) return false;
            var copied = tv.SelectedLength > 0 ? tv.SelectedText : text;
            ClipboardHelper.Copy(copied);
            dlg.Title = $" {title}   ✓ copied {copied.Length:N0} chars ";
            return true;
        };
        dlg.Add(tv);
        tv.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    // ── Cell / record viewer ───────────────────────────────────────────────

    /// <summary>
    /// Shows the full value of a cell (pretty-printing JSON/XML) or, after Tab, the whole row
    /// as name: value pairs. n/p step through rows.
    /// </summary>
    public static void ShowCell(ResultTableSource src, int row, int col)
    {
        if (src.Rows == 0) return;
        bool rowMode = false;

        var dlg = new Dialog
        {
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var tv = new EditorView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true, ColorScheme = Theme.Input
        };
        dlg.Add(tv);

        string Content()
        {
            var values = src.RowAt(row);
            if (!rowMode)
                return values[col] is { } v ? Pretty(v) : "NULL";

            int w  = src.Data.Columns.Max(c => c.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                var v      = values[i] ?? "NULL";
                var indent = new string(' ', w + 3);
                sb.Append(src.Data.Columns[i].PadRight(w)).Append(" : ")
                  .Append(v.Replace("\r\n", "\n").Replace("\n", "\n" + indent))
                  .Append('\n');
            }
            return sb.ToString();
        }

        void Refresh()
        {
            var colName = src.Data.Columns[col];
            var type    = src.Data.ColumnTypes[col];
            dlg.Title = rowMode
                ? $" row {row + 1}/{src.Rows}   Tab=cell  n/p=next/prev  c=copy  Esc=close "
                : $" {colName} ({type}) · row {row + 1}/{src.Rows}   Tab=whole row  n/p=next/prev  c=copy  Esc=close ";
            tv.Text = Content();
            tv.CursorPosition = new System.Drawing.Point(0, 0);
            dlg.SetNeedsDraw();
        }

        tv.PreviewKey = k =>
        {
            switch (k.KeyCode)
            {
                case KeyCode.Tab:
                    rowMode = !rowMode; Refresh(); return true;
                case KeyCode.N:
                    if (row < src.Rows - 1) { row++; Refresh(); } return true;
                case KeyCode.P:
                    if (row > 0) { row--; Refresh(); } return true;
                case KeyCode.C:
                    var text = tv.SelectedLength > 0 ? tv.SelectedText : rowMode ? Content() : src.RowAt(row)[col] ?? "";
                    ClipboardHelper.Copy(text);
                    dlg.Title = $" ✓ copied {text.Length:N0} chars   Esc=close ";
                    return true;
                default:
                    return false;
            }
        };

        Refresh();
        tv.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>Indents JSON and XML values; everything else is returned unchanged.</summary>
    public static string Pretty(string value)
    {
        var t = value.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch (JsonException) { }
        }
        else if (t.StartsWith('<'))
        {
            try { return XElement.Parse(value).ToString(); }
            catch (System.Xml.XmlException) { }
        }
        return value;
    }

    // ── Filterable picker (history, object explorer) ───────────────────────

    /// <summary>
    /// A filter box over a list. Typing filters; ↑↓/PgUp/PgDn move; Enter or one of
    /// <paramref name="actionKeys"/> picks. Returns the item and the key used, or null on Esc.
    /// </summary>
    public static (T Item, KeyCode Key)? Pick<T>(
        string title, string hint, Func<string, IReadOnlyList<PickItem<T>>> query, params KeyCode[] actionKeys)
    {
        (T, KeyCode)? result = null;
        IReadOnlyList<PickItem<T>> items = [];

        var dlg = new Dialog
        {
            Title       = $" {title} ",
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var filter = new TextField { X = 2, Y = 0, Width = Dim.Fill(1), ColorScheme = Theme.Input };
        var count  = new Label { X = 1, Y = 1, Width = Dim.Fill(1), ColorScheme = Theme.StatusBar }.NoHotKey();
        var list   = new ListView
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1),
            ColorScheme = Theme.Picker, CanFocus = false
        };
        var help = new Label { Text = hint, X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), ColorScheme = Theme.StatusBar }.NoHotKey();
        dlg.Add(new Label { Text = "›", X = 0, Y = 0, ColorScheme = Theme.StatusBar }, filter, count, list, help);

        void Refill()
        {
            items = query(filter.Text ?? "");
            list.SetSource(new ObservableCollection<string>(items.Select(i => i.Display)));
            list.SelectedItem = items.Count > 0 ? 0 : -1;
            count.Text = $"{items.Count:N0} match{(items.Count == 1 ? "" : "es")}";
            list.SetNeedsDraw();
        }

        void Move(int delta)
        {
            if (items.Count == 0) return;
            list.SelectedItem = Math.Clamp(list.SelectedItem + delta, 0, items.Count - 1);
            list.EnsureSelectedItemVisible();
            list.SetNeedsDraw();
        }

        filter.TextChanged += (_, _) => Refill();
        filter.KeyDown += (_, k) =>
        {
            switch (k.KeyCode)
            {
                case KeyCode.CursorDown: Move(1);   k.Handled = true; return;
                case KeyCode.CursorUp:   Move(-1);  k.Handled = true; return;
                case KeyCode.PageDown:   Move(10);  k.Handled = true; return;
                case KeyCode.PageUp:     Move(-10); k.Handled = true; return;
            }
            if (k.KeyCode == KeyCode.Enter || actionKeys.Contains(k.KeyCode))
            {
                k.Handled = true;
                if (list.SelectedItem >= 0 && list.SelectedItem < items.Count)
                {
                    result = (items[list.SelectedItem].Value, k.KeyCode);
                    Application.RequestStop();
                }
            }
        };

        Refill();
        filter.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
        return result;
    }

    // ── Theme picker (F9) ──────────────────────────────────────────────────

    /// <summary>
    /// Lists the themes, each row drawn in its own colours. Moving previews the theme on the
    /// whole screen; Enter keeps and saves it, Esc goes back to the previous one.
    /// </summary>
    public static void PickTheme()
    {
        var before  = Theme.Current;
        var themes  = Theme.All;
        var screen  = Application.Top; // the screen behind the dialog, recoloured along with it
        Palette? chosen = null;
        int nameW   = themes.Max(t => t.Name.Length);

        var dlg = new Dialog
        {
            Title       = " theme   ↑↓ preview · Enter keep · Esc cancel ",
            Width       = Math.Max(52, nameW + themes.Max(t => t.Description.Length) + 12),
            Height      = themes.Count + 3,
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var list = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Theme.Picker
        };
        list.SetSource(new ObservableCollection<string>(
            themes.Select(t => $" {(t == before ? "●" : " ")} {t.Name.PadRight(nameW)}   {t.Description}")));
        list.RowRender += (_, e) =>
        {
            if (e.Row < 0 || e.Row >= themes.Count) return;
            var t = themes[e.Row];
            e.RowAttribute = e.Row == list.SelectedItem ? new Terminal.Gui.Attribute(t.HiFg, t.Hi) : new Terminal.Gui.Attribute(t.Accent, t.Bg);
        };
        list.SelectedItemChanged += (_, e) =>
        {
            if (e.Item >= 0 && e.Item < themes.Count && themes[e.Item] != Theme.Current)
                Theme.Apply(themes[e.Item], screen, dlg);
        };
        list.OpenSelectedItem += (_, e) =>
        {
            if (e.Item < 0 || e.Item >= themes.Count) return;
            chosen = themes[e.Item];
            Application.RequestStop(dlg);
        };

        dlg.Add(list);
        list.SelectedItem = Math.Max(0, themes.ToList().IndexOf(before));
        list.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();

        if (chosen is null)
        {
            Theme.Apply(before, screen);
            return;
        }
        Theme.Apply(chosen, screen);
        try { Theme.Save(chosen); }
        catch (Exception ex) { SqlrPaths.LogError(ex); }
    }

    // ── Object structure (F12) ─────────────────────────────────────────────

    /// <summary>
    /// The columns / parameters grid and, for views and routines, the definition (Tab switches).
    /// "/" or Ctrl+F searches like nvim: on the grid it filters the rows; on the definition it
    /// highlights every match and jumps to it as you type (n / N: next / previous).
    /// </summary>
    /// <param name="line">Open on the definition at this 1-based line (grep hits).</param>
    /// <param name="search">Start with this search: highlighted in the definition, or filtering the grid.</param>
    public static void ShowStructure(ObjectDescription d, int? line = null, string? search = null)
    {
        bool    hasDefinition = !string.IsNullOrEmpty(d.Definition);
        bool    showDef       = false;
        bool    searching     = false;
        string? term          = null; // definition search (highlighted)
        int     origin        = 0;    // cursor offset when the search started (incremental search)

        var dlg = new Dialog
        {
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);
        dlg.NoHotKey();

        var detailLines = new List<string>(d.Details);
        if (d.AlsoIn.Count > 0)
            detailLines.Add($"Also in schema: {string.Join(", ", d.AlsoIn)} (qualify the name to peek those)");
        int detailH = Math.Min(detailLines.Count, 8);

        var src  = new ResultTableSource(d.Grid);
        var grid = new GridView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(detailH + (detailH > 0 ? 1 : 0) + 1),
            ColorScheme = Theme.Base, FullRowSelect = true, MultiSelect = false, Table = src
        };
        ResultsPane.ConfigureTableStyle(grid);

        var details = new TextView
        {
            X = 0, Y = Pos.Bottom(grid) + (detailH > 0 ? 1 : 0), Width = Dim.Fill(), Height = detailH,
            ReadOnly = true, Text = string.Join("\n", detailLines), ColorScheme = Theme.StatusBar,
            Visible = detailH > 0, CanFocus = false
        };

        var definition = new EditorView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1),
            ReadOnly = true, Text = d.Definition ?? "", ColorScheme = Theme.Input, Visible = false
        };
        SqlSyntaxHighlighter.Attach(definition, () => term);

        // Bottom line: hints, or the "/" search field while searching (nvim's command line).
        var status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), ColorScheme = Theme.StatusBar }.NoHotKey();
        var slash  = new Label { Text = "/", X = 0, Y = Pos.AnchorEnd(1), ColorScheme = Theme.StatusBar, Visible = false };
        var field  = new TextField { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), ColorScheme = Theme.Input, Visible = false };

        dlg.Add(grid, details, definition, status, slash, field);

        List<int> Matches()
        {
            var text = definition.Text ?? "";
            var list = new List<int>();
            if (string.IsNullOrEmpty(term)) return list;
            var cmp = term.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int i = text.IndexOf(term, cmp); i >= 0; i = text.IndexOf(term, i + term.Length, cmp))
                list.Add(i);
            return list;
        }

        // Moves to the next (dir 1) / previous (dir -1) match after `from`, wrapping around.
        void Jump(int dir, int from, bool inclusive)
        {
            var m = Matches();
            if (m.Count == 0) return;
            int target = dir > 0
                ? m.FirstOrDefault(o => inclusive ? o >= from : o > from, m[0])
                : m.LastOrDefault(o => o < from, m[^1]);
            var (r, c) = EditorText.ToRowCol(definition.Text ?? "", target);
            EditorText.MoveCursor(definition, r, c);
        }

        void UpdateStatus()
        {
            string text;
            if (searching)
                text = "";
            else if (showDef)
            {
                var m = Matches();
                int at = m.IndexOf(m.FirstOrDefault(o => o >= EditorText.LinearCursor(definition), -1)) + 1;
                text = term is null
                    ? $" / search   {(hasDefinition ? "Tab columns   " : "")}c copy   Esc close"
                    : $" \"{term}\"  {(m.Count == 0 ? "no matches" : $"{at}/{m.Count}")}   n/N next/prev   / new search   Esc clear";
            }
            else
            {
                text = src.Filter is null
                    ? $" / filter   {(hasDefinition ? "Tab definition   " : "")}c copy   Esc close"
                    : $" filter \"{src.Filter}\" → {src.Rows} of {d.Grid.Rows.Count} rows   / change   Esc clear";
            }
            status.Text    = text;
            status.Visible = !searching;
            slash.Visible  = field.Visible = searching;
            dlg.SetNeedsDraw();
        }

        void Refresh()
        {
            dlg.Title          = $" {d.Kind}: {d.FullName} · {Fmt.Plural(d.Grid.Rows.Count, d.Grid.Columns[0] == "Parameter" ? "parameter" : "column")} ";
            grid.Visible       = !showDef;
            details.Visible    = !showDef && detailH > 0;
            definition.Visible = showDef;
            (showDef ? (View)definition : grid).SetFocus();
            UpdateStatus();
        }

        void ApplyGridFilter(string? f)
        {
            src.ApplyFilter(f);
            grid.Update();
            grid.SelectedRow = 0;
            grid.SetNeedsDraw();
        }

        void StartSearch()
        {
            searching  = true;
            origin     = EditorText.LinearCursor(definition);
            field.Text = showDef ? term ?? "" : src.Filter ?? "";
            UpdateStatus();
            field.SetFocus();
            field.CursorPosition = field.Text.Length;
        }

        void EndSearch(bool keep)
        {
            searching = false;
            if (!keep)
            {
                if (showDef) { term = null; definition.SetNeedsDraw(); }
                else ApplyGridFilter(null);
            }
            Refresh();
        }

        field.TextChanged += (_, _) =>
        {
            if (!searching) return;
            var text = field.Text ?? "";
            if (showDef)
            {
                term = text.Length == 0 ? null : text;
                Jump(1, origin, inclusive: true);
                definition.SetNeedsDraw();
            }
            else ApplyGridFilter(text);
        };
        field.KeyDown += (_, k) =>
        {
            if (k.KeyCode == KeyCode.Enter) { EndSearch(keep: true);  k.Handled = true; }
            else if (k.KeyCode == KeyCode.Esc) { EndSearch(keep: false); k.Handled = true; }
        };

        bool OnKey(Key k)
        {
            if (k.KeyCode == KeyCode.Tab && hasDefinition)
            {
                showDef = !showDef;
                Refresh();
                return true;
            }
            if (k.AsRune.Value == '/' || k.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
            {
                StartSearch();
                return true;
            }
            if (showDef && term is not null && (k.KeyCode == KeyCode.N || k.KeyCode == (KeyCode.N | KeyCode.ShiftMask)))
            {
                bool back = k.IsShift;
                Jump(back ? -1 : 1, EditorText.LinearCursor(definition), inclusive: false);
                UpdateStatus();
                return true;
            }
            if (k.KeyCode == KeyCode.Esc && (showDef ? term is not null : src.Filter is not null))
            {
                EndSearch(keep: false); // first Esc clears the search, the next one closes
                return true;
            }
            if (k.KeyCode == KeyCode.C)
            {
                var text = showDef
                    ? (definition.SelectedLength > 0 ? definition.SelectedText : d.Definition ?? "")
                    : ResultExport.Write(ExportFormat.Tsv, d.Grid, src.VisibleRows);
                ClipboardHelper.Copy(text);
                status.Text = $" ✓ copied {(showDef ? "definition" : $"{Fmt.Plural(src.Rows, "row")}")}";
                return true;
            }
            return false;
        }
        grid.PreviewKey       = OnKey;
        definition.PreviewKey = OnKey;
        definition.UnwrappedCursorPosition += (_, _) => { if (!searching && term is not null) UpdateStatus(); };

        // Procedures / functions: the definition is the interesting part — open on it.
        showDef = hasDefinition && (line is not null || d.Grid.Rows.Count == 0);
        if (!string.IsNullOrEmpty(search))
        {
            if (showDef) term = search;
            else ApplyGridFilter(search);
        }
        Refresh();

        // Position the cursor once the dialog has a size, so the TextView can scroll to it.
        dlg.Ready += (_, _) =>
        {
            if (!showDef || line is not int ln) return;
            var text = definition.Text ?? "";
            int start = EditorText.ToLinear(text, Math.Max(0, ln - 1), 0);
            Jump(1, start, inclusive: true);
            if (term is null || EditorText.ToRowCol(text, EditorText.LinearCursor(definition)).Row != ln - 1)
                EditorText.MoveCursor(definition, ln - 1, 0);
            UpdateStatus();
        };

        Application.Run(dlg);
        dlg.Dispose();
    }

    // ── Help (F1) ──────────────────────────────────────────────────────────

    public const string HelpText =
        """
        EDITOR
          F5 / Ctrl+R          Run the script — or just the selection, if any
          F6 / Ctrl+E          Run the block under the cursor (lines between blank lines)
          Esc                  Cancel the running query · close the completion popup
          Ctrl+Space           Show completions
          Tab                  Accept the highlighted completion
          ↑↓ then Enter        Pick another completion (Enter alone is always a newline)
          Ctrl+/  ·  Alt+/     Toggle -- comment on the line / selected lines
          Ctrl+Backspace       Delete word left   (also Ctrl+W, Alt+Backspace)
          Ctrl+Delete          Delete word right  (also Alt+D)
          Ctrl+Z / Ctrl+Y      Undo / redo
          Ctrl+S / Ctrl+O      Save / open a .sql file
          Tab                  Jump to the results

        RESULTS
          Arrows, PgUp/PgDn    Move around (Home/End: first/last column)
          Enter / dbl-click    View the full cell value (Tab inside: whole row, n/p: next/prev row)
          c / r / a            Copy cell / row / all rows (tab-separated, paste into Excel)
          / (slash)            Filter rows (Esc clears the filter)
          s                    Sort by the current column (asc → desc → off)
          e                    Export to a file (.csv .tsv .json .md .sql by extension)
          F7 / Shift+F7        Next / previous result set · messages
          Esc / Tab            Back to the editor
          right-click          Copy the cell

        OBJECT EXPLORER (F4)   databases › schemas › objects, opens on dbo; the selection previews on the right
          ←/h  →/l/Enter       Up a level / down a level (on an object: its F12 view)
          Ctrl+F or /          Find: filter the list as you type (Enter keeps it, Esc clears)
          Ctrl+G               Grep: plain text in every definition + column name (Enter opens the line)
          t / s                Filter by type / sort (name, type, modified — each ↑ and ↓)
          i / F5               Insert the name into the editor / SELECT TOP 100
          p · Ctrl+D / Ctrl+U  Hide / show the preview on the right · scroll it

        F12 VIEW
          / or Ctrl+F          Filter the columns, or search the definition (n / N next / previous)
          Tab · c · Esc        Columns ⇄ definition · copy · clear search, then close

        ANYWHERE
          F1  this help        F2  clear results     F3  query history
          F4  object explorer  F8  resize panes      F9  colour theme (live preview)
          F10 switch connection
          F12 peek the table / view / procedure under the cursor (works on aliases)
          Ctrl+Q  quit (your editor text is kept as a draft for next time)

        FILES  (~/.sqlr)
          connections.json   saved connections (passwords DPAPI-encrypted on Windows)
          history.jsonl      query history          drafts/   editor text per connection
          settings.json      { "maxRows": 100000, "queryTimeoutSeconds": 0, "maxColumnWidth": 60 }
          theme              the theme picked with F9
        """;
}
