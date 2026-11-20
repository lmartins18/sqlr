using System.Collections.Concurrent;
using Sqlr.Db;
using Sqlr.Tui;
using Terminal.Gui;
using Xunit;

namespace Sqlr.Tests;

/// <summary>Drives the F4 explorer and the F12 view through the FakeDriver against an in-memory catalog.</summary>
[Collection("UI")]
public sealed class ObjectExplorerTests : IDisposable
{
    private const string GetBillsDef = "CREATE PROCEDURE dbo.usp_GetBills\nAS\nSELECT b.AccountId, b.Amount\nFROM dbo.Bills b;\n";

    private static readonly Dictionary<string, List<CatalogObject>> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sales"] =
        [
            new("dbo", "Accounts",     "table",    new DateTime(2026, 1, 5)),
            new("dbo", "Bills",        "table",    new DateTime(2026, 3, 1)),
            new("dbo", "vAccounts",    "view",     new DateTime(2026, 2, 1)),
            new("dbo", "usp_GetBills", "proc",     new DateTime(2026, 11, 1)),
            new("dbo", "fn_Total",     "function", new DateTime(2025, 6, 1)),
            new("edi", "Files",        "table",    new DateTime(2026, 4, 1)),
            new("edi", "usp_Load",     "proc",     new DateTime(2026, 5, 1)),
        ],
        ["Archive"] = [new("old", "OldBills", "table", new DateTime(2020, 1, 1))],
    };

    private static readonly (string Schema, string Table, string Column, string Type)[] Columns =
    [
        ("dbo", "Accounts", "AccountId", "int"), ("dbo", "Accounts", "Name", "nvarchar"),
        ("dbo", "Bills", "AccountId", "int"),    ("dbo", "Bills", "Amount", "decimal"),
    ];

    private readonly ConcurrentQueue<Action> _dispatched = new();
    private readonly ConcurrentQueue<string> _described = new(); // db.schema.name, as the preview / F12 asked
    private readonly ObjectExplorer _x;
    private RunState _rs;

    public ObjectExplorerTests()
    {
        Application.Init(new FakeDriver());
        _x  = new ObjectExplorer(Source(), a => _dispatched.Enqueue(a), grepDelay: TimeSpan.Zero, previewDelay: TimeSpan.Zero);
        _rs = Application.Begin(_x.Dialog);
        PumpUntil(() => _x.Lines.Count > 0);
    }

    public void Dispose()
    {
        Application.End(_rs);
        _x.Dialog.Dispose();
        Application.Shutdown();
    }

    private ExplorerSource Source() => new()
    {
        Database    = "Sales",
        Databases   = ["master", "Sales", "Archive"],
        LoadObjects = (db, _) => Task.FromResult(Catalog.TryGetValue(db, out var o) ? o : []),
        LoadSearchIndex = (db, _) =>
        {
            _indexLoads.Enqueue(db);
            return db == "master"
                ? Task.FromException<SearchIndex>(new InvalidOperationException("no access"))
                : Task.FromResult(IndexOf(db));
        },
        Describe    = (db, schema, name, _) =>
        {
            _described.Enqueue($"{db}.{schema}.{name}");
            return Task.FromResult<ObjectDescription?>(Describe(schema, name));
        }
    };

    private readonly ConcurrentQueue<string> _indexLoads = new();

    private static readonly (string Schema, string Table, string Column, string Type)[] ArchiveColumns =
        [("old", "OldBills", "AccountId", "int")];

    private static SearchIndex IndexOf(string db) => new(
        Catalog[db].Select(o => new SearchObject(o.Schema, o.Name, o.Kind)).ToList(),
        (db == "Sales" ? Columns : ArchiveColumns).Select(c => new SearchColumn(c.Schema, c.Table, c.Column, c.Type)).ToList(),
        db == "Sales" ? [new SearchModule("dbo", "usp_GetBills", "proc", GetBillsDef)] : []);

    private static ObjectDescription Describe(string schema, string name) => name == "usp_GetBills"
        ? new ObjectDescription
        {
            Schema = schema, Name = name, Kind = "Procedure", Definition = GetBillsDef,
            Grid   = new ResultSet { Columns = ["Parameter", "Type", "Direction", "Default"], ColumnTypes = ["sysname", "sysname", "varchar", "varchar"] }
        }
        : AccountsDescription(name);

    private static ObjectDescription AccountsDescription(string name = "Accounts")
    {
        var grid = new ResultSet
        {
            Columns     = ["Column", "Type", "Null", "Key", "Default", "References"],
            ColumnTypes = ["sysname", "sysname", "varchar", "varchar", "nvarchar", "nvarchar"]
        };
        grid.Rows.Add(["AccountId", "int", "NO", "PK IDENTITY", "", ""]);
        grid.Rows.Add(["Name", "nvarchar(50)", "YES", "", "", ""]);
        grid.Rows.Add(["Opened", "date", "NO", "", "(getdate())", ""]);
        var d = new ObjectDescription { Schema = "dbo", Name = name, Kind = "Table", Grid = grid };
        d.Details.Add("≈ 1,234 rows");
        d.Details.Add("PK     PK_Accounts  clustered  (AccountId)");
        return d;
    }

    // ── Driving ────────────────────────────────────────────────────────────

    private void Pump()
    {
        while (_dispatched.TryDequeue(out var a)) a();
        Application.RunIteration(ref _rs, firstIteration: false);
    }

    private void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Assert.True(done(), "timed out waiting for the explorer");
    }

    private void Press(Key k)
    {
        Application.RaiseKeyDownEvent(k);
        Pump();
    }

    private void Type(string s)
    {
        foreach (var ch in s) Press(new Key(ch));
    }

    /// <summary>Runs <paramref name="open"/> (which shows a modal dialog) and plays <paramref name="steps"/> inside it, one per iteration.</summary>
    private void InModal(Action open, params Action[] steps)
    {
        int i = 0;
        void Step(object? sender, IterationEventArgs e)
        {
            if (i < steps.Length && Application.Top is { } top && top != _x.Dialog) steps[i++]();
        }
        Application.Iteration += Step;
        try
        {
            open();
            PumpUntil(() => i >= steps.Length);
        }
        finally { Application.Iteration -= Step; }
    }

    private static View Modal => Application.Top!;
    private IEnumerable<string> Names => _x.Lines.Select(l => l.Trim().Split("  ")[0]);

    // ── Explorer ───────────────────────────────────────────────────────────

    [Fact]
    public void Opens_on_the_session_database_dbo_objects()
    {
        Assert.Equal(ObjectExplorer.Level.Objects, _x.CurrentLevel);
        Assert.Contains("Sales › dbo", _x.Info);
        Assert.Equal(["Accounts", "Bills", "fn_Total", "usp_GetBills", "vAccounts"], Names);
    }

    [Fact]
    public void Left_goes_up_to_schemas_and_databases_right_goes_back_down()
    {
        Press(Key.CursorLeft);
        Assert.Equal(ObjectExplorer.Level.Schemas, _x.CurrentLevel);
        Assert.Equal("dbo", _x.Selected);
        Assert.Equal(["(all schemas)", "dbo", "edi"], Names);

        Press(new Key('h'));
        Assert.Equal(ObjectExplorer.Level.Databases, _x.CurrentLevel);
        Assert.Equal("Sales", _x.Selected);

        Press(Key.CursorDown);                       // Archive
        Press(Key.CursorRight);
        PumpUntil(() => _x.Lines.Count > 1);
        Assert.Equal(ObjectExplorer.Level.Schemas, _x.CurrentLevel);
        Assert.Equal(["(all schemas)", "old"], Names);

        Press(new Key('l'));                         // (all schemas)
        Assert.Equal(ObjectExplorer.Level.Objects, _x.CurrentLevel);
        Assert.Equal(["old.OldBills"], Names);
        Assert.Contains("Archive › (all schemas)", _x.Info);
    }

    [Fact]
    public void Ctrl_F_filters_the_list_and_Esc_clears_it()
    {
        Press(Key.F.WithCtrl);
        Assert.Equal(ObjectExplorer.Mode.Find, _x.CurrentMode);
        Type("bill");
        Assert.Equal(["Bills", "usp_GetBills"], Names);

        Press(Key.Enter);                            // keep the filter, back to the list
        Assert.Equal(ObjectExplorer.Mode.Browse, _x.CurrentMode);
        Assert.Equal(["Bills", "usp_GetBills"], Names);

        Press(Key.Esc);                              // first Esc clears the filter…
        Assert.Equal(5, _x.Lines.Count);
        Assert.Null(_x.Action);
    }

    [Fact]
    public void Type_filter_and_sort_cycle()
    {
        Press(new Key('t'));
        Assert.Equal(["Accounts", "Bills"], Names);
        Assert.Contains("type: tables", _x.Info);
        Press(new Key('t'));
        Assert.Equal(["vAccounts"], Names);
        Press(new Key('t')); Press(new Key('t')); Press(new Key('t')); // function, proc, all
        Assert.Equal(5, _x.Lines.Count);

        Press(new Key('s'));                         // name ↓
        Assert.Equal("vAccounts", Names.First());
        Press(new Key('s'));                         // type ↑: tables, view, function, proc
        Assert.Equal(["Accounts", "Bills", "vAccounts", "fn_Total", "usp_GetBills"], Names);
        Press(new Key('s'));                         // type ↓
        Assert.Equal("usp_GetBills", Names.First());
        Press(new Key('s'));                         // modified ↓
        Assert.Equal("usp_GetBills", Names.First());
        Assert.Contains("sort: modified ↓", _x.Info);
    }

    [Fact]
    public void Grep_finds_columns_and_definition_lines()
    {
        Press(Key.G.WithCtrl);
        Assert.Equal(ObjectExplorer.Mode.Grep, _x.CurrentMode);
        Type("accountid");
        PumpUntil(() => !_x.Info.Contains("searching")); // "accounti"'s hits can show while the last search runs
        Assert.Equal(3, _x.Lines.Count);
        Assert.Contains(_x.Lines, l => l.Contains("dbo.Accounts") && l.Contains("col │ AccountId  int"));
        Assert.Contains(_x.Lines, l => l.Contains("dbo.usp_GetBills") && l.Contains("3 │ SELECT b.AccountId, b.Amount"));
        Assert.Contains("3 matches in 3 objects", _x.Info);

        Press(Key.Esc);                              // back to the object list
        Assert.Equal(ObjectExplorer.Mode.Browse, _x.CurrentMode);
        Assert.Equal(5, _x.Lines.Count);
    }

    [Fact]
    public void Grep_hit_opens_the_definition_on_its_line_with_the_match_highlighted()
    {
        Press(Key.G.WithCtrl);
        Type("Amount");
        PumpUntil(() => _x.Lines.Any(l => l.Contains("usp_GetBills")));
        while (_x.Selected is not GrepHit { IsColumn: false }) Press(Key.CursorDown);

        int row = -1;
        string? status = null;
        InModal(() => Press(Key.Enter),
            () => { },
            () =>
            {
                row    = Modal.Subviews.OfType<EditorView>().Single(v => v.Visible).CurrentRow;
                status = Modal.Subviews.OfType<Label>().First(l => l.Visible).Text;
            },
            () => Application.RaiseKeyDownEvent(Key.Esc),   // clears the search…
            () => Application.RaiseKeyDownEvent(Key.Esc));  // …then closes

        Assert.Equal(2, row);
        Assert.Contains("\"Amount\"  1/1", status);
    }

    [Fact]
    public void Grep_finds_objects_by_name_with_or_without_the_schema()
    {
        Press(Key.G.WithCtrl);
        Type("dbo.bills");
        PumpUntil(() => !_x.Info.Contains("searching"));

        Assert.Equal("dbo.Bills", Names.First());
        Assert.Matches(@"table\s+name │", _x.Lines[0]);
        Assert.Contains(_x.Lines, l => l.Contains("dbo.usp_GetBills") && l.Contains("4 │ FROM dbo.Bills b;"));

        PumpUntil(() => _x.PreviewTitle == " table dbo.Bills ");
        Assert.Equal(0, _x.PreviewTop);                      // a name hit previews from the top

        Press(Key.Tab);
        Press(new Key('i'));
        Assert.Equal(("dbo", "Bills"), (_x.Action?.Object.Schema, _x.Action?.Object.Name));
    }

    [Fact]
    public void Name_matches_take_the_name_or_a_qualified_name_with_smart_case()
    {
        GrepHit? Match(string pattern) => ObjectCatalog.MatchName("EDI", "NimoPricingGroups", "table", pattern, "CMaster");

        Assert.NotNull(Match("NimoPricingGroups"));
        Assert.NotNull(Match("EDI.NimoPricingGroups"));
        Assert.NotNull(Match("CMaster.EDI.NimoPricingGroups"));
        Assert.NotNull(Match("[CMaster].[EDI].[NimoPricingGroups]"));   // pasted from SSMS
        Assert.NotNull(Match("cmaster.edi.nimo"));
        Assert.Null(Match("nimoPricing"));                              // a capital: exact case
        Assert.Null(Match("EDI"));                                      // no dot: the name alone, not every EDI object
        Assert.Null(Match("Billing.EDI.NimoPricingGroups"));
        Assert.True(Match("Nimo")!.IsName);
    }

    [Fact]
    public void A_database_is_read_once_while_you_type()
    {
        Press(Key.G.WithCtrl);
        Type("acc");
        PumpUntil(() => !_x.Info.Contains("searching"));
        Type("ountid");
        PumpUntil(() => !_x.Info.Contains("searching"));

        Assert.Contains("3 matches in 3 objects", _x.Info);
        Assert.Equal(["Sales"], _indexLoads);
    }

    [Fact]
    public void Grep_from_the_database_list_takes_a_database_qualified_name()
    {
        Press(Key.CursorLeft); Press(Key.CursorLeft);
        Press(Key.G.WithCtrl);
        Type("Archive.old.OldBills");
        PumpUntil(() => !_x.Info.Contains("searching"));

        Assert.Equal(["Archive.old.OldBills"], Names);
    }

    [Fact]
    public void Grep_on_the_database_list_searches_every_database_listed()
    {
        Press(Key.CursorLeft); Press(Key.CursorLeft); // databases
        Press(Key.G.WithCtrl);
        Type("accountid");
        PumpUntil(() => !_x.Info.Contains("searching"));

        Assert.Equal(["Sales.dbo.Accounts", "Sales.dbo.Bills", "Sales.dbo.usp_GetBills", "Archive.old.OldBills"], Names);
        Assert.Contains("grep in 3 databases · 4 matches in 4 objects · ⚠ master: no access", _x.Info);

        for (int n = 0; n < 10 && _x.Selected is not GrepHit { Database: "Archive" }; n++) Press(Key.CursorDown);
        PumpUntil(() => _described.Contains("Archive.old.OldBills")); // the preview reads it from its own database
    }

    [Fact]
    public void Grep_on_a_filtered_database_list_searches_only_those()
    {
        Press(Key.CursorLeft); Press(Key.CursorLeft);
        Press(Key.F.WithCtrl);
        Type("arch");
        Press(Key.Enter);
        Press(Key.G.WithCtrl);
        Type("accountid");
        PumpUntil(() => !_x.Info.Contains("searching"));

        Assert.Equal(["old.OldBills"], Names);
        Assert.Contains("grep in Archive · 1 match in 1 object", _x.Info);
    }

    [Fact]
    public void I_on_a_grep_hit_inserts_the_object_it_is_in()
    {
        Press(Key.CursorLeft); Press(Key.CursorLeft);
        Press(Key.G.WithCtrl);
        Type("accountid");                            // its i goes into the search box
        PumpUntil(() => !_x.Info.Contains("searching"));
        Assert.Null(_x.Action);

        Press(Key.Tab);                               // into the results
        for (int n = 0; n < 10 && _x.Selected is not GrepHit { Database: "Archive" }; n++) Press(Key.CursorDown);
        Press(new Key('i'));

        Assert.Equal(("old", "OldBills"), (_x.Action?.Object.Schema, _x.Action?.Object.Name));
        Assert.Equal("Archive", _x.Action?.Database);
        Assert.Equal(ExplorerVerb.Insert, _x.Action?.Verb);
    }

    [Fact]
    public void Insert_returns_the_object_to_the_editor()
    {
        Press(Key.CursorDown);                        // Bills
        Press(new Key('i'));
        Assert.Equal("Bills", _x.Action?.Object.Name);
        Assert.Equal(ExplorerVerb.Insert, _x.Action?.Verb);
    }

    [Fact]
    public void R_selects_the_top_1000_of_tables_and_views_only()
    {
        Press(new Key('t')); Press(new Key('t')); Press(new Key('t')); Press(new Key('t')); // procs
        Assert.Equal(["usp_GetBills"], Names);
        Press(new Key('r'));
        Assert.Null(_x.Action);

        Press(new Key('t'));                          // all
        Press(Key.Home); Press(Key.CursorDown);       // Bills
        Press(new Key('r'));
        Assert.Equal("Bills", _x.Action?.Object.Name);
        Assert.Equal(ExplorerVerb.Top1000, _x.Action?.Verb);
    }

    // ── Preview pane ───────────────────────────────────────────────────────

    [Fact]
    public void Preview_follows_the_selection()
    {
        PumpUntil(() => _x.PreviewText.Contains("AccountId"));
        Assert.Equal(" table dbo.Accounts ", _x.PreviewTitle);
        var lines = _x.PreviewText.Split('\n');
        Assert.Equal("-- table dbo.Accounts · ≈ 1,234 rows", lines[0]);
        Assert.Contains("AccountId  int           NOT NULL  -- PK IDENTITY", lines);
        Assert.Contains("Opened     date          NOT NULL  -- default (getdate())", lines);
        Assert.Contains("-- PK     PK_Accounts  clustered  (AccountId)", lines);

        Press(Key.CursorDown); Press(Key.CursorDown); Press(Key.CursorDown); // usp_GetBills
        PumpUntil(() => _x.PreviewText.Contains("CREATE PROCEDURE"));
        Assert.Equal(" procedure dbo.usp_GetBills ", _x.PreviewTitle);

        Press(Key.CursorLeft);                                                // schemas: a summary
        Assert.Contains("-- schema dbo · 5 objects", _x.PreviewText);
        Assert.Contains("-- procs (1)", _x.PreviewText);
    }

    [Fact]
    public void Preview_of_a_grep_hit_scrolls_to_its_line()
    {
        Press(Key.G.WithCtrl);
        Type("Amount");
        PumpUntil(() => _x.Lines.Any(l => l.Contains("usp_GetBills")));
        while (_x.Selected is not GrepHit { IsColumn: false }) Press(Key.CursorDown);
        PumpUntil(() => _x.PreviewText.Contains("CREATE PROCEDURE"));

        var lines = _x.PreviewText.Split('\n');
        Assert.Equal("SELECT b.AccountId, b.Amount", lines[_x.PreviewTop + 3]); // three lines of context above
    }

    [Fact]
    public void P_toggles_the_preview()
    {
        Assert.True(_x.PreviewShown);
        Press(new Key('p'));
        Assert.False(_x.PreviewShown);
        Press(new Key('p'));
        Assert.True(_x.PreviewShown);
    }

    // ── F12 view ───────────────────────────────────────────────────────────

    [Fact]
    public void F12_view_slash_filters_the_columns()
    {
        int filtered = -1, afterEsc = -1;
        InModal(() => Dialogs.ShowStructure(AccountsDescription()),
            () => Application.RaiseKeyDownEvent(new Key('/')),
            () => { foreach (var ch in "name") Application.RaiseKeyDownEvent(new Key(ch)); },
            () => filtered = Modal.Subviews.OfType<GridView>().Single().Table.Rows,
            () => Application.RaiseKeyDownEvent(Key.Esc),   // clears the filter
            () => afterEsc = Modal.Subviews.OfType<GridView>().Single().Table.Rows,
            () => Application.RaiseKeyDownEvent(Key.Esc));  // closes

        Assert.Equal(1, filtered);
        Assert.Equal(3, afterEsc);
    }

    [Fact]
    public void F12_view_searches_the_definition_with_n_and_N()
    {
        var rows = new List<int>();
        int Row() => Modal.Subviews.OfType<EditorView>().Single(v => v.Visible).CurrentRow;
        InModal(() => Dialogs.ShowStructure(Describe("dbo", "usp_GetBills")),
            () => Application.RaiseKeyDownEvent(Key.F.WithCtrl),
            () => { foreach (var ch in "bills") Application.RaiseKeyDownEvent(new Key(ch)); },
            () => rows.Add(Row()),                          // incremental: first match (line 1)
            () => Application.RaiseKeyDownEvent(Key.Enter),
            () => Application.RaiseKeyDownEvent(new Key('n')),
            () => rows.Add(Row()),                          // next match: line 4
            () => Application.RaiseKeyDownEvent(new Key('N')),
            () => rows.Add(Row()),                          // previous: back to line 1
            () => Application.RaiseKeyDownEvent(Key.Esc),
            () => Application.RaiseKeyDownEvent(Key.Esc));

        Assert.Equal([0, 3, 0], rows);
    }

    // ── Grep matching ──────────────────────────────────────────────────────

    [Fact]
    public void Grep_lines_are_numbered_and_smart_case()
    {
        var hits = ObjectCatalog.MatchLines("dbo", "p", "proc", "a\r\nSELECT AccountId\r\nb accountid", "accountid").ToList();
        Assert.Equal([2, 3], hits.Select(h => h.Line));
        Assert.Equal("SELECT AccountId", hits[0].Text);

        Assert.Single(ObjectCatalog.MatchLines("dbo", "p", "proc", "a\r\nSELECT AccountId\r\nb accountid", "AccountId"));
    }
}
