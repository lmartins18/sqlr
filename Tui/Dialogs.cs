using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

        var filter = new TextField { X = 2, Y = 0, Width = Dim.Fill(1), ColorScheme = Theme.Input };
        var count  = new Label { X = 1, Y = 1, Width = Dim.Fill(1), ColorScheme = Theme.StatusBar };
        var list   = new ListView
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1),
            ColorScheme = Theme.Picker, CanFocus = false
        };
        var help = new Label { Text = hint, X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), ColorScheme = Theme.StatusBar };
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

    // ── Object structure (F12) ─────────────────────────────────────────────

    public static void ShowStructure(ObjectDescription d)
    {
        bool hasDefinition = !string.IsNullOrEmpty(d.Definition);
        bool showDef       = false;

        var dlg = new Dialog
        {
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);

        var detailLines = new List<string>(d.Details);
        if (d.AlsoIn.Count > 0)
            detailLines.Add($"Also in schema: {string.Join(", ", d.AlsoIn)} (qualify the name to peek those)");
        int detailH = Math.Min(detailLines.Count, 8);

        var grid = new GridView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(detailH + (detailH > 0 ? 1 : 0)),
            ColorScheme = Theme.Base, FullRowSelect = true, MultiSelect = false,
            Table = new ResultTableSource(d.Grid)
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
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, Text = d.Definition ?? "", ColorScheme = Theme.Input, Visible = false
        };
        SqlSyntaxHighlighter.Attach(definition);

        dlg.Add(grid, details, definition);

        void Refresh()
        {
            var tabHint = hasDefinition ? (showDef ? "   Tab=columns" : "   Tab=definition") : "";
            dlg.Title = $" {d.Kind}: {d.FullName} · {Fmt.Plural(d.Grid.Rows.Count, d.Grid.Columns[0] == "Parameter" ? "parameter" : "column")}{tabHint}   c=copy  Esc=close ";
            grid.Visible       = !showDef;
            details.Visible    = !showDef && detailH > 0;
            definition.Visible = showDef;
            (showDef ? (View)definition : grid).SetFocus();
            dlg.SetNeedsDraw();
        }

        bool OnKey(Key k)
        {
            if (k.KeyCode == KeyCode.Tab && hasDefinition)
            {
                showDef = !showDef;
                Refresh();
                return true;
            }
            if (k.KeyCode == KeyCode.C)
            {
                var text = showDef
                    ? (definition.SelectedLength > 0 ? definition.SelectedText : d.Definition ?? "")
                    : ResultExport.Write(ExportFormat.Tsv, d.Grid);
                ClipboardHelper.Copy(text);
                dlg.Title = $" ✓ copied {(showDef ? "definition" : "columns")}   Esc=close ";
                return true;
            }
            return false;
        }
        grid.PreviewKey       = OnKey;
        definition.PreviewKey = OnKey;

        // Procedures / functions: the definition is the interesting part — open on it.
        showDef = hasDefinition && d.Grid.Rows.Count == 0;
        Refresh();
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

        ANYWHERE
          F1  this help        F2  clear results     F3  query history
          F4  object explorer  F8  resize panes      F10 switch connection
          F12 peek the table / view / procedure under the cursor (works on aliases)
          Ctrl+Q  quit (your editor text is kept as a draft for next time)

        FILES  (~/.sqlr)
          connections.json   saved connections (passwords DPAPI-encrypted on Windows)
          history.jsonl      query history          drafts/   editor text per connection
          settings.json      { "maxRows": 100000, "queryTimeoutSeconds": 0, "maxColumnWidth": 60 }
        """;
}
