using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using Sqlr.Config;
using Sqlr.Db;
using Sqlr.Tui;
using Terminal.Gui;
using Xunit;

namespace Sqlr.Tests.Demo;

/// <summary>Runs only when SQLR_RECORD_DEMOS=1, on Windows with the sqlr-demo LocalDB instance.</summary>
public sealed class DemoFactAttribute : FactAttribute
{
    public DemoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQLR_RECORD_DEMOS") != "1" || !OperatingSystem.IsWindows())
            Skip = "Records the README clips: set SQLR_RECORD_DEMOS=1 (Windows, LocalDB instance sqlr-demo).";
    }
}

/// <summary>
/// The README clips (docs/demo/*.gif): the real query screen against fictional LocalDB data,
/// driven key by key. Re-record with
///   sqllocaldb create sqlr-demo -s
///   $env:SQLR_RECORD_DEMOS = 1; dotnet test tests/Sqlr.Tests --filter FullyQualifiedName~DemoClips
/// </summary>
[Collection("UI")]
[SupportedOSPlatform("windows")]
public sealed class DemoClips
{
    private const string Server = @"(localdb)\sqlr-demo";
    private const int MaxClipMs = 10_000;
    private static readonly Lazy<bool> DatabaseReady = new(CreateDemoDatabases);

    [DemoFact]
    public void Write_and_run() => Record("write-and-run", "", c => c
        .Hold(700)
        .Type("select c.Name, o.Total from sales.Cu")
        .Hold(500)
        .Press(Key.Tab, 300)
        .Type(" c join sales.Or")
        .Press(Key.Enter, 300)
        .Type(" o on o.Cu")
        .Hold(350)
        .Press(Key.Enter, 300)
        .Type(" = c.CustomerId\norder by o.Total desc", 40)
        .Run(2600));

    [DemoFact]
    public void Results() => Record("results", "select * from sales.vActiveCustomers", c => c
        .Run(900)
        .Press(Key.End, 500)
        .Press(new Key('s'), 700)
        .Press(new Key('s'), 1100)
        .Press(new Key('/'), 400, until: () => ScreenText().Contains("filter rows"))
        .Type("boston", 70)
        .Press(Key.Enter, 1300)
        .Press(Key.Enter, 500, until: () => ScreenText().Contains("Tab=whole row"))
        .Press(Key.Tab, 1700, until: () => ScreenText().Contains("City       : Boston"))
        .Press(Key.Esc, 600));

    [DemoFact]
    public void Errors() => Record("errors", "select c.Name, o.OrderDate, o.Totl\nfrom sales.Customers c\njoin sales.Orders o on o.CustomerId = c.CustomerId", c => c
        .Hold(800)
        .Run(2600)
        .Press(Key.End, 250)
        .Press(Key.Backspace, 200)
        .Type("al", 180)
        .Hold(500)
        .Run(2400));

    [DemoFact]
    public void Explorer() => Record("explorer", "", c => c
        .Press(Key.F4, 1100, until: () => ScreenText().Contains("objects"))
        .Press(Key.CursorLeft, 700)
        .Press(Key.CursorDown, 400)
        .Press(Key.CursorRight, 900)
        .Wait(PreviewShows("table sales.Customers"), 0, silent: true)
        .Press(Key.CursorDown, 1000, until: PreviewShows("table sales.OrderLines"))
        .Press(Key.CursorDown, 1000, until: PreviewShows("table sales.Orders"))
        .Press(new Key('r'), 2600, until: () => !_screen!.IsRunning && ScreenText().Contains("TOP (1000)")));

    [DemoFact]
    public void Grep() => Record("grep", "", c => c
        .Press(Key.F4, 500, until: () => ScreenText().Contains("objects"))
        .Press(Key.CursorLeft, 150)
        .Press(Key.CursorLeft, 500)
        .Press(Key.F.WithCtrl, 150)
        .Type("sqlr", 70)
        .Press(Key.Enter, 400)
        .Press(Key.G.WithCtrl, 250)
        .Type("CustomerId", 55)
        .Wait(() => ScreenText().Contains("match") && PreviewLoaded(), 1600)
        .Press(Key.CursorDown, 450, until: PreviewLoaded)
        .Press(Key.CursorDown, 450, until: PreviewLoaded)
        .Press(Key.CursorDown, 800, until: PreviewLoaded)
        .Press(Key.Enter, 1600, until: () => ScreenText().Contains("CREATE"))
        .Press(Key.Esc, 150)
        .Press(Key.Esc, 200)
        .Press(Key.Esc, 200)
        .Press(Key.Esc, 400));

    [DemoFact]
    public void Peek() => Record("peek", "select *\nfrom sales.vActiveCustomers v\nwhere v.Spent > 1000", c => c
        .Do(() => EditorText.MoveCursor(_screen!.Editor, 1, 12))
        .Hold(700)
        .Press(Key.F12, 1800, until: () => ScreenText().Contains("Spent"))
        .Press(Key.Tab, 2000)
        .Press(Key.Esc, 500)
        .Do(() => { _screen!.Editor.Text = "exec sales.usp_GetCustomerOrders 3"; EditorText.MoveCursor(_screen.Editor, 0, 12); })
        .Hold(500)
        .Press(Key.F12, 1800, until: () => ScreenText().Contains("@CustomerId"))
        .Press(Key.Esc, 400));

    [DemoFact]
    public void Themes() => Record("themes", "select c.Name, c.City, o.Status, o.Total\nfrom sales.Customers c\njoin sales.Orders o on o.CustomerId = c.CustomerId\nwhere o.Status = 'open'", c => c
        .Run(0, silent: true)
        .Hold(600)
        .Press(Key.F9, 600, until: () => ScreenText().Contains("↑↓ preview"))
        .Press(Key.CursorDown, 700).Press(Key.CursorDown, 700).Press(Key.CursorDown, 700)
        .Press(Key.CursorDown, 700).Press(Key.CursorDown, 700).Press(Key.CursorDown, 700)
        .Press(Key.Esc, 900));

    [DemoFact]
    public void History() => Record("history", "", c => c
        .Do(() => _screen!.ExecuteForTest("select count(*) from sales.Orders where Status = 'open'"))
        .Wait(() => !_screen!.IsRunning, 0, silent: true)
        .Do(() => _screen!.ExecuteForTest("select top 5 * from dbo.Products order by Price desc"))
        .Wait(() => !_screen!.IsRunning, 0, silent: true)
        .Do(() => _screen!.ExecuteForTest("select Name, City from sales.Customers where State = 'RI'"))
        .Wait(() => !_screen!.IsRunning, 0, silent: true)
        .Do(() => _screen!.Editor.Text = "")
        .Hold(500)
        .Press(Key.F3, 1100, until: () => ScreenText().Contains("history ·"))
        .Type("product", 80)
        .Hold(900)
        .Press(Key.F5, 2200, until: () => !_screen!.IsRunning && ScreenText().Contains("Grinder burrs")));

    [DemoFact]
    public void Paste() => Record("paste", "", c => c
        .Do(() => Application.RaiseKeyDownEvent(Key.F8)) // a taller editor, so more of the script shows
        .Do(() => Application.RaiseKeyDownEvent(Key.F8))
        .Hold(600)
        .Do(() =>
        {
            foreach (var ch in PastedScript.Replace("\r", ""))
                Application.RaiseKeyDownEvent(ch switch { '\n' => Key.Enter, '\t' => Key.Tab, ' ' => Key.Space, _ => new Key(ch) });
        })
        .Wait(() => (_screen!.Editor.Text ?? "").Contains("COMMIT"), 2200)
        .Press(Key.Z.WithCtrl, 1300));

    private const string PastedScript = """
        -- Move cancelled orders out of the way
        BEGIN TRAN;

        CREATE TABLE #cancelled (
            OrderId INT PRIMARY KEY,
            CustomerId INT NOT NULL,
            Total DECIMAL(12, 2) NOT NULL
        );

        INSERT INTO #cancelled (OrderId, CustomerId, Total)
        SELECT o.OrderId, o.CustomerId, o.Total
        FROM sales.Orders o
        WHERE o.Status = 'cancelled';

        DELETE l
        FROM sales.OrderLines l
        JOIN #cancelled c ON c.OrderId = l.OrderId;

        DELETE o
        FROM sales.Orders o
        JOIN #cancelled c ON c.OrderId = o.OrderId;

        SELECT COUNT(*) AS Moved, SUM(Total) AS Value FROM #cancelled;

        COMMIT;
        """;

    // ── Recording ──────────────────────────────────────────────────────────

    private QueryScreen? _screen;

    private static Func<bool> PreviewShows(string text) => () => ScreenText().Contains(text) && PreviewLoaded();

    private static bool PreviewLoaded() => !ScreenText().Contains("loading…");

    private void Record(string name, string initialText, Func<Clip, Clip> script)
    {
        Assert.True(DatabaseReady.Value);
        var conn = new SqlrConnection { Name = "demo", Server = Server, Database = "SqlrDemo" };
        using var runner = new SqlRunner(conn.ConnectionString);
        // As sqlr does before the screen opens; before Init, whose sync context would never run the continuation.
        Assert.Null(runner.ConnectAsync().GetAwaiter().GetResult());
        var schema = new SchemaService(db => conn.BuildConnectionString(db));
        schema.Reload("SqlrDemo");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (schema.IsLoading && DateTime.UtcNow < deadline) Thread.Sleep(50);

        Theme.Apply(Theme.Find("horizon")!);
        Application.Init(new FakeDriver());
        ((FakeDriver)Application.Driver!).SetBufferSize(112, 30);

        using var rec = new GifRecorder(name);
        try
        {
            _screen = new QueryScreen(conn, runner, schema, new SqlrSettings(), QueryHistory.Load(200), initialText);
            var clip = script(new Clip(this, rec));
            clip.Play(_screen);
        }
        finally
        {
            Application.Shutdown();
        }
        Assert.True(rec.TotalMs <= MaxClipMs, $"{name} runs {rec.TotalMs} ms");
        rec.Save(Path.Combine(RepoRoot(), "docs", "demo", name + ".gif"));
    }

    private static string ScreenText()
    {
        var cells = Application.Driver?.Contents;
        if (cells is null) return "";
        var sb = new StringBuilder();
        for (int r = 0; r < cells.GetLength(0); r++)
        {
            for (int c = 0; c < cells.GetLength(1); c++) sb.Append(cells[r, c].Rune);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static Point? CursorCell()
    {
        var focused = Application.Top?.MostFocused;
        if (focused is not (TextView or TextField)) return null;
        try
        {
            return focused.PositionCursor() is { } p ? focused.ViewportToScreen(p) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sqlr.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("sqlr.csproj not found above the test output");
    }

    private static bool CreateDemoDatabases()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Demo", "demo-db.sql"));
        var cs = new SqlrConnection { Name = "setup", Server = Server, Database = "master" }.ConnectionString;
        using var runner = new SqlRunner(cs);
        var result = runner.ExecuteAsync(script, 0, CancellationToken.None).GetAwaiter().GetResult();
        if (result.HasErrors)
            throw new InvalidOperationException(string.Join("\n", result.Messages.Where(m => m.Kind == MessageKind.Error).Select(m => m.Text)));
        return true;
    }

    private sealed record Step(Action? Do, Func<bool>? Until, int HoldMs, bool Capture);

    /// <summary>Steps played inside the screen's own main loop, one per iteration, each captured as a frame.</summary>
    private sealed class Clip(DemoClips owner, GifRecorder rec)
    {
        private readonly List<Step> _steps = [];

        public Clip Press(Key key, int hold = 160, Func<bool>? until = null)
        {
            _steps.Add(new(() => Application.RaiseKeyDownEvent(key), until, hold, true));
            return this;
        }

        public Clip Type(string text, int perChar = 55)
        {
            foreach (var ch in text)
                Press(ch switch { ' ' => Terminal.Gui.Key.Space, '\n' => Terminal.Gui.Key.Enter, _ => new Key(ch) }, perChar);
            return this;
        }

        public Clip Hold(int ms)
        {
            _steps.Add(new(null, null, ms, true));
            return this;
        }

        public Clip Do(Action action)
        {
            _steps.Add(new(action, null, 0, false));
            return this;
        }

        public Clip Wait(Func<bool> until, int hold, bool silent = false)
        {
            _steps.Add(new(null, until, hold, !silent));
            return this;
        }

        /// <summary>F5, then the finished run on screen.</summary>
        public Clip Run(int hold, bool silent = false)
        {
            _steps.Add(new(() => Application.RaiseKeyDownEvent(Terminal.Gui.Key.F5), () => !owner._screen!.IsRunning, hold, !silent));
            return this;
        }

        private sealed class State(Step step)
        {
            public Step Step { get; } = step;
            public DateTime Started { get; } = DateTime.UtcNow;
            public bool Begun { get; set; }
            public bool Done { get; set; }
        }

        public void Play(QueryScreen screen)
        {
            int next = 0;
            State? current = null;
            Exception? failure = null;
            bool finished = false;

            void OnIteration(object? sender, IterationEventArgs e)
            {
                // Done or failed: close whatever is open (a dialog, then the screen), one per pass.
                if (failure is not null || finished)
                {
                    Application.RequestStop();
                    return;
                }
                try
                {
                    if (current is null)
                    {
                        if (next == _steps.Count)
                        {
                            finished = true;
                            return;
                        }
                        var state = current = new State(_steps[next++]);
                        // Through the main loop, so a key that opens a dialog doesn't block this handler.
                        if (state.Step.Do is { } action) Application.Invoke(() => { state.Begun = true; action(); state.Done = true; });
                        else state.Begun = state.Done = true;
                        return;
                    }
                    if (!current.Begun) return;
                    Application.LayoutAndDraw(true); // the conditions read the screen, so make it current
                    bool ready = current.Step.Until is { } until ? until() : current.Done;
                    if (!ready)
                    {
                        if (DateTime.UtcNow - current.Started > TimeSpan.FromSeconds(30))
                            throw new TimeoutException($"step {next} never got ready:\n{ScreenText()}");
                        return;
                    }
                    if (current.Step.Capture)
                    {
                        Application.LayoutAndDraw(true);
                        rec.Capture(current.Step.HoldMs, CursorCell());
                    }
                    current = null;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            Application.Iteration += OnIteration;
            try
            {
                screen.Run();
            }
            finally
            {
                Application.Iteration -= OnIteration;
            }
            if (failure is not null) throw new InvalidOperationException("clip failed", failure);
        }
    }
}
