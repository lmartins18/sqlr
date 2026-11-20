using Point = System.Drawing.Point;
using Sqlr.Config;
using Sqlr.Db;
using Sqlr.Tui;
using Terminal.Gui;
using Xunit;

namespace Sqlr.Tests;

[CollectionDefinition("UI", DisableParallelization = true)]
public class UiCollection;

/// <summary>
/// Drives the real QueryScreen through Terminal.Gui's FakeDriver with synthetic key presses.
/// </summary>
[Collection("UI")]
public sealed class QueryScreenUiTests : IDisposable
{
    private readonly QueryScreen _screen;
    private readonly SqlRunner   _runner;
    private RunState             _rs;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _dispatched = new();

    public QueryScreenUiTests()
    {
        Application.Init(new FakeDriver()); // 80×25

        _runner = new SqlRunner("Server=127.0.0.1,1;Connect Timeout=1");
        var schema = new SchemaService(_ => "", FakeSchema.Build());
        _screen = new QueryScreen(
            new SqlrConnection { Name = "ui-test", Server = "srv", Database = "SqlrTest" },
            _runner, schema, new SqlrSettings(), QueryHistory.Load(0));

        // Background work posts back through Dispatch; queue it so Pump() can run it on this thread.
        _screen.Dispatch = a => _dispatched.Enqueue(a);

        _rs = Application.Begin(_screen.Top);
        _screen.Top.Running = true;
        Pump();
    }

    public void Dispose()
    {
        Application.End(_rs);
        _screen.Top.Dispose();
        _runner.Dispose();
        Application.Shutdown();
    }

    private void Pump()
    {
        while (_dispatched.TryDequeue(out var a)) a();
        for (int i = 0; i < 3; i++) Application.RunIteration(ref _rs, firstIteration: false);
        Application.LayoutAndDraw(true);
    }

    private void Press(Key k)
    {
        Application.RaiseKeyDownEvent(k);
        Pump();
    }

    private void Type(string s)
    {
        foreach (var ch in s)
            Press(ch == ' ' ? Key.Space : ch == '\n' ? Key.Enter : new Key(ch));
    }

    private TextView Editor => _screen.Editor;

    [Fact]
    public void Typing_after_FROM_opens_popup_with_tables()
    {
        Type("select * from cust");
        Assert.True(_screen.Popup.IsOpen);
        Assert.Equal("Customers", _screen.Popup.Selected!.Text);
    }

    [Fact]
    public void Popup_arrow_keys_do_not_move_the_editor_cursor()
    {
        Type("select * from \nfrom o");
        Assert.True(_screen.Popup.IsOpen);
        var before = Editor.CursorPosition;
        var first  = _screen.Popup.Selected;

        Press(Key.CursorDown);
        Assert.Equal(before, Editor.CursorPosition);
        Assert.NotEqual(first, _screen.Popup.Selected);

        Press(Key.CursorUp);
        Assert.Equal(before, Editor.CursorPosition);
        Assert.Equal(first, _screen.Popup.Selected);
    }

    [Fact]
    public void Enter_accepts_a_picked_completion_without_inserting_a_newline()
    {
        Type("select * from cust");
        Press(Key.CursorDown);
        Press(Key.CursorUp);
        Press(Key.Enter);
        Assert.Equal("select * from sales.Customers", Editor.Text);
        Assert.False(_screen.Popup.IsOpen);
        Assert.Equal(new Point("select * from sales.Customers".Length, 0), Editor.CursorPosition);
    }

    [Fact]
    public void Enter_without_picking_is_a_newline_so_aliases_survive()
    {
        Type("select * from sales.Orders o");
        Assert.True(_screen.Popup.IsOpen);          // "o" matches ON / ORDER BY / OUTER APPLY…
        Press(Key.Enter);
        Type("where o.");
        Assert.Equal("select * from sales.Orders o\nwhere o.", Editor.Text.Replace("\r\n", "\n"));
        Assert.True(_screen.Popup.IsOpen);
        Assert.Contains(_screen.Popup.Items, c => c.Text == "Total");
    }

    [Fact]
    public void Ctrl_space_then_Enter_accepts()
    {
        Type("select * from cust");
        Press(Key.Esc);
        Press(Key.Space.WithCtrl);
        Press(Key.Enter);
        Assert.Equal("select * from sales.Customers", Editor.Text);
    }

    [Fact]
    public void Tab_accepts_completion_and_stays_in_the_editor()
    {
        Type("select * from cust");
        Press(Key.Tab);
        Assert.Equal("select * from sales.Customers", Editor.Text);
        Assert.True(Editor.HasFocus);
    }

    [Fact]
    public void Enter_on_an_already_complete_word_is_a_newline()
    {
        Editor.Text = "select * from sales.Customers c where c.Emai";
        EditorText.MoveCursor(Editor, 0, Editor.Text.Length);
        Type("l");
        // "Email" is complete: the popup closes itself or Enter falls through to a newline
        Press(Key.Enter);
        Assert.Equal("select * from sales.Customers c where c.Email\n", Editor.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Esc_closes_the_popup_without_quitting()
    {
        Type("select * from cust");
        Press(Key.Esc);
        Assert.False(_screen.Popup.IsOpen);
        Assert.True(_screen.Top.Running);
    }

    [Fact]
    public void Esc_never_quits_the_screen()
    {
        Press(Key.Esc);
        Assert.True(_screen.Top.Running);

        Press(Key.Tab); // to results
        Press(Key.Esc);
        Assert.True(_screen.Top.Running);
        Assert.True(Editor.HasFocus); // Esc from results returns to the editor
    }

    [Fact]
    public void Ctrl_Q_quits()
    {
        Press(Key.Q.WithCtrl);
        Assert.False(_screen.Top.Running);
    }

    [Fact]
    public void Dot_after_alias_completes_its_columns_and_keeps_scroll()
    {
        Editor.Text = "select o\nfrom sales.Orders o";
        EditorText.MoveCursor(Editor, 0, 8);
        Type(".");
        Assert.True(_screen.Popup.IsOpen);
        Assert.Equal(["CustomerId", "OrderId", "Total"], _screen.Popup.Items.Select(c => c.Text).Order().ToList());

        Type("to");
        Press(Key.Tab);
        Assert.Equal("select o.Total\nfrom sales.Orders o", Editor.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Bracketed_identifier_is_replaced_including_its_bracket()
    {
        Type("select * from [Order D");
        Press(Key.Tab);
        Assert.Equal("select * from [Order Details]", Editor.Text);

        // "[Order|]" (closing bracket already typed) doesn't end up doubled
        Editor.Text = "select * from [Ord]";
        EditorText.MoveCursor(Editor, 0, 18);
        Press(Key.Space.WithCtrl);
        while (_screen.Popup.Selected?.Text != "Order Details") Press(Key.CursorDown);
        Press(Key.Tab);
        Assert.Equal("select * from [Order Details]", Editor.Text);
    }

    [Fact]
    public void Completion_accept_is_undoable()
    {
        Type("select * from cust");
        Press(Key.Tab);
        Assert.Equal("select * from sales.Customers", Editor.Text);
        for (int i = 0; i < 40 && Editor.Text != "select * from cust"; i++)
            Press(Key.Z.WithCtrl);
        Assert.Equal("select * from cust", Editor.Text);
    }

    [Fact]
    public void Multi_line_comment_toggle_undoes_cleanly()
    {
        const string original = "select 1\nselect 2\nselect 3\nselect 4";
        Editor.Text = original;
        Editor.ClearHistoryChanges();
        EditorText.MoveCursor(Editor, 0, 0);
        Press(Key.CursorDown.WithShift);
        Press(Key.CursorDown.WithShift);
        Press(Key.End.WithShift);
        Press(new Key((KeyCode)'/' | KeyCode.CtrlMask));
        Assert.Equal("-- select 1\n-- select 2\n-- select 3\nselect 4", Editor.Text.Replace("\r\n", "\n"));

        for (int i = 0; i < 60 && Editor.Text.Replace("\r\n", "\n") != original; i++)
            Press(Key.Z.WithCtrl);
        Assert.Equal(original, Editor.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Emoji_before_the_cursor_does_not_break_completion()
    {
        Editor.Text = "select N'😀', cust from sales.Customers";
        EditorText.MoveCursor(Editor, 0, "select N'😀', cust".EnumerateRunes().Count());
        Press(Key.Space.WithCtrl);
        Press(Key.Tab);
        Assert.StartsWith("select N'😀', CustomerId", Editor.Text);
    }

    private void WaitForRun()
    {
        var deadline = DateTime.Now.AddSeconds(30);
        while (_screen.IsRunning && DateTime.Now < deadline) { Thread.Sleep(50); Pump(); }
        Assert.False(_screen.IsRunning);
    }

    [Fact]
    public void Line_numbers_follow_the_editor_and_widen_past_99_lines()
    {
        Editor.Text = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"select {i}"));
        EditorText.MoveCursor(Editor, 34, 0);                    // line 35, scrolled into view
        Pump();

        var numbers = _screen.Gutter.VisibleNumbers();
        Assert.Equal((Editor.TopRow + 1).ToString(), numbers[0]);
        Assert.Contains("35", numbers);
        Assert.Equal(_screen.Gutter.Frame.Width, Editor.Frame.X);

        Editor.Text = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"select {i}"));
        Pump();
        Assert.Equal(4, _screen.Gutter.Frame.Width);              // "120" plus a space
    }

    /// <summary>What a terminal does on paste: every character as a key, back to back.</summary>
    private void PasteKeys(string text)
    {
        foreach (var ch in text)
            Application.RaiseKeyDownEvent(ch switch { '\n' => Key.Enter, '\t' => Key.Tab, ' ' => Key.Space, _ => new Key(ch) });
    }

    [Fact]
    public void A_terminal_paste_goes_in_as_plain_text_in_one_undo_step()
    {
        const string script = "CREATE TABLE dbo.T (\n\tId INT,\n\tName NVARCHAR(50)\n);\nSELECT * FROM Archive..Bills\n";
        Editor.SetFocus();
        PasteKeys(script);
        Pump();

        Assert.Equal(script, Editor.Text.Replace("\r\n", "\n")); // tabs stay text, ".." isn't expanded
        Assert.True(Editor.HasFocus);
        Assert.False(_screen.Popup.IsOpen);

        Press(Key.Z.WithCtrl);
        Assert.DoesNotContain("\n", Editor.Text);                 // the block goes in one step
    }

    [Fact]
    public void A_paste_arriving_in_chunks_keeps_a_tab_at_the_start_of_a_chunk()
    {
        Editor.SetFocus();
        PasteKeys("SELECT 1\n");
        Pump();
        PasteKeys("\tFROM t\n");
        Pump();

        Assert.Equal("SELECT 1\n\tFROM t\n", Editor.Text.Replace("\r\n", "\n"));
        Assert.True(Editor.HasFocus);
    }

    [Fact]
    public void Keys_queued_while_typing_still_complete_and_Tab_still_accepts()
    {
        Editor.SetFocus();
        PasteKeys("select * from cust");
        Application.RaiseKeyDownEvent(Key.Tab);
        Pump();

        Assert.Equal("select * from sales.Customers", Editor.Text);
        Assert.True(Editor.HasFocus);
    }

    [Fact]
    public void A_long_paste_is_quick()
    {
        var script = string.Concat(Enumerable.Range(0, 200).Select(i => $"    UPDATE a SET a.Col{i} = b.Value FROM dbo.Accounts a JOIN dbo.Bills b ON b.Id = a.Id;\n"));
        Editor.SetFocus();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PasteKeys(script);
        Pump();

        Assert.Equal(script, Editor.Text.Replace("\r\n", "\n"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}"); // was ~15 s typed key by key
        Assert.True(EditorView.CanPasteInOneStep, "Terminal.Gui's InsertAllText is gone: pastes fall back to InsertText");
    }

    [Fact]
    public void Undo_and_redo_drop_a_selection_whose_anchor_they_remove()
    {
        Editor.SetFocus();
        Type("select 1\nselect 2");
        Press(Key.Home.WithShift);                    // anchored at the end of the text…
        Press(Key.Z.WithCtrl);                        // …which the undo takes away

        Assert.False(Editor.IsSelecting);
        Assert.StartsWith("select 1\nselect", Editor.Text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("select 2", Editor.Text);

        Press(Key.End);
        Press(Key.Home.WithShift);
        Press(Key.Y.WithCtrl);
        Assert.False(Editor.IsSelecting);
    }

    [Fact]
    public void An_error_on_every_redraw_ends_the_screen_instead_of_spinning()
    {
        var ex = new InvalidOperationException("boom");
        var t0 = DateTime.UtcNow;
        for (int i = 0; i < 200; i++)
            Assert.True(_screen.OnRunError(ex, t0.AddMilliseconds(100 * i))); // now and then: carry on
        Assert.Contains("Internal error: boom", _screen.StatusText);

        var t1 = t0.AddMinutes(1);
        int n = 0;
        while (n < 1000 && _screen.OnRunError(ex, t1.AddMilliseconds(n))) n++;
        Assert.Equal(QueryScreen.MaxErrorsPerSecond - 1, n);
    }

    [Fact]
    public void Explorer_top_1000_goes_below_the_editor_text_and_runs()
    {
        Editor.Text = "select 1\n\n";
        var customers = new CatalogObject("sales", "Customers", "table", DateTime.Now);
        _screen.ApplyExplorerAction(new ExplorerAction(customers, "SqlrTest", ExplorerVerb.Top1000));

        Assert.Equal("select 1\n\nSELECT TOP (1000) * FROM sales.Customers", Editor.Text.Replace("\r\n", "\n"));
        Assert.Equal(2, Editor.CurrentRow);
        Assert.True(_screen.IsRunning);
        WaitForRun();
    }

    [Fact]
    public void Explorer_top_1000_into_an_empty_editor_names_other_databases()
    {
        var bills = new CatalogObject("dbo", "Bills", "view", DateTime.Now);
        _screen.ApplyExplorerAction(new ExplorerAction(bills, "Archive", ExplorerVerb.Top1000));

        Assert.Equal("SELECT TOP (1000) * FROM Archive.dbo.Bills", Editor.Text);
        Assert.Equal(0, Editor.CurrentRow);
        Assert.True(_screen.IsRunning);
        WaitForRun();
    }

    [Fact]
    public void A_second_run_never_starts_while_one_is_running()
    {
        Editor.Text = "select 1";
        _screen.ExecuteForTest("select 1");
        Assert.True(_screen.IsRunning);
        _screen.ExecuteForTest("select 2");               // e.g. F3 → F5 or F4 → F5 while busy
        Assert.True(_screen.IsRunning);
        Assert.Contains("already running", _screen.StatusText);

        var deadline = DateTime.Now.AddSeconds(30);
        while (_screen.IsRunning && DateTime.Now < deadline) { Thread.Sleep(50); Pump(); }
        Assert.False(_screen.IsRunning);
    }

    [Fact]
    public void Enter_on_an_error_message_jumps_to_its_line_even_when_wrapped()
    {
        var r = new ExecutionResult();
        r.Messages.Add(new SqlMessage(MessageKind.Info, new string('x', 400) + " long print output that wraps"));
        r.Messages.Add(new SqlMessage(MessageKind.Error, "Msg 208, Level 16, State 1, Line 5\nInvalid object name 'nope'.", 5));
        r.Messages.Add(new SqlMessage(MessageKind.Error, "Msg 207, Level 16, State 1, Line 9\nInvalid column name 'x'.", 9));
        _screen.Results.Load(r);
        Pump();

        var lines = _screen.Results.Messages.Text.Replace("\r\n", "\n").Split('\n');
        int RowOf(string s) => Array.FindIndex(lines, l => l.Contains(s));

        foreach (var (needle, expected) in new (string, int?)[] { ("Msg 208", 5), ("Invalid object", 5), ("Msg 207", 9), ("long print", null) })
        {
            _screen.Results.Messages.CursorPosition = new Point(0, RowOf(needle));
            Assert.Equal(expected, _screen.Results.LineAtMessageCursor());
        }
    }

    [Fact]
    public void Ctrl_backspace_deletes_the_previous_word()
    {
        Editor.Text = "select foo bar";
        EditorText.MoveCursor(Editor, 0, 14);
        Press(Key.Backspace.WithCtrl);
        Assert.Equal("select foo ", Editor.Text);
        Press(Key.W.WithCtrl);
        Assert.Equal("select ", Editor.Text);
    }

    [Fact]
    public void Ctrl_slash_toggles_line_comments()
    {
        Editor.Text = "select 1\nselect 2";
        EditorText.MoveCursor(Editor, 1, 3);
        Press(new Key((KeyCode)'/' | KeyCode.CtrlMask));
        Assert.Equal("select 1\n-- select 2", Editor.Text.Replace("\r\n", "\n"));
        Press(new Key((KeyCode)'/' | KeyCode.CtrlMask));
        Assert.Equal("select 1\nselect 2", Editor.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Double_dot_expands_to_dbo_only_outside_strings()
    {
        Type("select * from SqlrOther..");
        Assert.Equal("select * from SqlrOther.dbo.", Editor.Text);

        Editor.Text = "";
        Type("print 'wait..");
        Assert.Equal("print 'wait..", Editor.Text);
    }

    [Fact]
    public void No_popup_inside_strings()
    {
        Type("select 'from cust");
        Assert.False(_screen.Popup.IsOpen);
    }

    [Fact]
    public void Tab_moves_focus_between_editor_and_results()
    {
        Assert.True(Editor.HasFocus);
        Press(Key.Tab);
        Assert.False(Editor.HasFocus);
        Assert.True(_screen.Results.Messages.HasFocus); // no results yet → messages pane
        Press(Key.Tab);
        Assert.True(Editor.HasFocus);
    }

    [Fact]
    public void Keywords_are_highlighted()
    {
        Type("select 1");
        var origin = Editor.ViewportToScreen(new Point(0, 0));
        var cell   = Application.Driver!.Contents![origin.Y, origin.X];
        Assert.Equal(Color.Yellow, cell.Attribute!.Value.Foreground);
        var digit  = Application.Driver.Contents[origin.Y, origin.X + 7];
        Assert.Equal(Color.Magenta, digit.Attribute!.Value.Foreground);
    }

    [Fact]
    public void Switching_theme_recolors_the_open_screen()
    {
        var tokyo = Theme.Find("Tokyo-Night")!;
        try
        {
            Theme.Apply(tokyo, _screen.Top);
            Type("select 1");
            var origin = Editor.ViewportToScreen(new Point(0, 0));
            var cell   = Application.Driver!.Contents![origin.Y, origin.X];
            Assert.Equal(tokyo.Keyword, cell.Attribute!.Value.Foreground);
            Assert.Equal(tokyo.Bg, cell.Attribute!.Value.Background);
            Assert.Equal(tokyo.TitleBg, Application.Driver.Contents[0, 0].Attribute!.Value.Background);
            Assert.Same(Theme.Base, _screen.Top.ColorScheme);
        }
        finally
        {
            Theme.Apply(Theme.Default, _screen.Top);
        }
    }

    [Fact]
    public void F9_previews_themes_live_and_Esc_goes_back()
    {
        var seen = DriveThemePicker(Key.CursorDown, Key.CursorDown, Key.Esc);
        Assert.Equal([Theme.All[1].Name, Theme.All[2].Name], seen);
        Assert.Equal(Theme.Default, Theme.Current);
        Assert.False(File.Exists(SqlrPaths.ThemeFile));
    }

    [Fact]
    public void F9_Enter_keeps_and_saves_the_theme()
    {
        try
        {
            DriveThemePicker(Key.CursorDown, Key.Enter);
            Assert.Equal(Theme.All[1], Theme.Current);
            Assert.Equal(Theme.All[1].Name, File.ReadAllText(SqlrPaths.ThemeFile).Trim());

            Theme.Apply(Theme.Default);
            Theme.LoadSaved();
            Assert.Equal(Theme.All[1], Theme.Current);
        }
        finally
        {
            File.Delete(SqlrPaths.ThemeFile);
            Theme.Apply(Theme.Default, _screen.Top);
        }
    }

    [Fact]
    public void Theme_names_are_unique()
    {
        Assert.Equal(Theme.All.Count, Theme.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Same(Theme.Default, Theme.All[0]);
    }

    /// <summary>Opens the F9 picker and presses <paramref name="keys"/> inside it; returns the theme active after each key but the last.</summary>
    private List<string> DriveThemePicker(params Key[] keys)
    {
        var seen = new List<string>();
        int i    = 0;
        void OnIteration(object? sender, IterationEventArgs e)
        {
            if (i >= keys.Length) return;
            if (i > 0) seen.Add(Theme.Current.Name);
            Application.RaiseKeyDownEvent(keys[i++]);
        }

        Application.Iteration += OnIteration;
        try { Press(Key.F9); }
        finally { Application.Iteration -= OnIteration; }
        return seen;
    }

    [Fact]
    public void Results_grid_loads_sorts_filters_and_copies()
    {
        var rs = new ResultSet { Columns = ["id", "name"], ColumnTypes = ["int", "nvarchar"] };
        rs.Rows.Add(["10", "b"]);
        rs.Rows.Add(["9", "a"]);
        rs.Rows.Add(["100", null]);
        var result = new ExecutionResult();
        result.ResultSets.Add(rs);
        result.ResultSets.Add(new ResultSet { Columns = ["x"], ColumnTypes = ["int"] });
        _screen.Results.Load(result);
        _screen.Results.Table.SetFocus();
        Pump();

        var src = _screen.Results.Source!;
        Assert.Equal(3, src.Rows);
        Assert.Contains("result 1", _screen.Results.Frame.Title);

        Press(new Key('s'));                                  // numeric sort, not string sort
        Assert.Equal(["9", "10", "100"], Enumerable.Range(0, 3).Select(i => src.RowAt(i)[0]).ToList());
        Press(new Key('s'));
        Assert.Equal("100", src.RowAt(0)[0]);
        Press(new Key('s'));
        Assert.Equal("10", src.RowAt(0)[0]);                  // back to original order

        src.ApplyFilter("A");
        Assert.Equal(1, src.Rows);
        Assert.Equal("9", src.RowAt(0)[0]);
        Press(Key.Esc);                                       // first Esc clears the filter…
        Assert.Equal(3, src.Rows);
        Assert.True(_screen.Results.Table.HasFocus);

        Press(Key.F7);
        Assert.Contains("▶ result 2", _screen.Results.Frame.Title);
        Press(Key.F7);
        Assert.True(_screen.Results.ShowingMessages);
        Press(Key.F7.WithShift);
        Assert.False(_screen.Results.ShowingMessages);
    }

    [Fact]
    public void Running_against_a_dead_server_reports_an_error_instead_of_crashing()
    {
        Editor.Text = "select 1";
        Press(Key.F5);
        var deadline = DateTime.Now.AddSeconds(30);
        while (_screen.IsRunning && DateTime.Now < deadline) { Thread.Sleep(50); Pump(); }

        Assert.False(_screen.IsRunning);
        Assert.True(_screen.Results.ShowingMessages);
        Assert.Contains("Could not connect", _screen.Results.Messages.Text);
        Assert.True(_screen.Top.Running);
    }
}
