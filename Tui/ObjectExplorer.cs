using System.Collections.ObjectModel;
using Sqlr.Db;
using Terminal.Gui;
using TAttr = Terminal.Gui.Attribute;

namespace Sqlr.Tui;

/// <summary>What the explorer data comes from (real queries in the app, fakes in tests).</summary>
internal sealed class ExplorerSource
{
    /// <summary>The session's database: where the explorer opens.</summary>
    public required string Database { get; init; }
    public required IReadOnlyList<string> Databases { get; init; }
    public required Func<string, CancellationToken, Task<List<CatalogObject>>> LoadObjects { get; init; }
    /// <summary>A database's object names, columns and definitions, for grep.</summary>
    public required Func<string, CancellationToken, Task<SearchIndex>> LoadSearchIndex { get; init; }
    /// <summary>(database, schema, name) → the F12 description.</summary>
    public required Func<string, string, string, CancellationToken, Task<ObjectDescription?>> Describe { get; init; }
    /// <summary>Object lists already loaded, per database; kept between openings.</summary>
    public Dictionary<string, List<CatalogObject>> Cache { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Descriptions already loaded (preview / F12), keyed by <see cref="DescriptionKey"/>; kept between openings.</summary>
    public Dictionary<string, ObjectDescription?> Descriptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DescriptionKey(string db, string schema, string name) => $"{db}\u0001{schema}\u0001{name}";
}

/// <summary>What the query screen should do with the object once the explorer closes.</summary>
internal enum ExplorerVerb
{
    /// <summary>Insert the name at the cursor.</summary>
    Insert,
    /// <summary>Show <c>SELECT TOP (100)</c> in the results, leaving the editor alone.</summary>
    Top100,
    /// <summary>Write <c>SELECT TOP (1000)</c> into the editor and run it.</summary>
    Top1000
}

internal sealed record ExplorerAction(CatalogObject Object, string Database, ExplorerVerb Verb);

/// <summary>
/// F4 object explorer: databases › schemas › objects, opening on the session database's dbo.
/// ←/h goes up a level, →/l/Enter goes down (or peeks an object). Ctrl+F filters the list,
/// Ctrl+G greps definitions and column names, t filters by type, s sorts.
/// </summary>
internal sealed class ObjectExplorer
{
    internal enum Level { Databases, Schemas, Objects }
    internal enum Mode  { Browse, Find, Grep }
    private  enum Sort  { NameAsc, NameDesc, TypeAsc, TypeDesc, NewestFirst, OldestFirst }

    private const string AllSchemas = "";

    private readonly ExplorerSource _src;
    private readonly Label          _info, _prompt, _hint;
    private readonly TextField      _input;
    private readonly KeyListView    _list;
    private readonly FrameView      _previewFrame;
    private readonly EditorView     _preview;
    private CancellationTokenSource? _previewCts;
    private string?                 _previewKey; // what the preview shows now
    private string                  _previewText = "";

    private Level   _level = Level.Objects;
    private Mode    _mode  = Mode.Browse;
    private Sort    _sort  = Sort.NameAsc;
    private string  _db;
    private string  _schema = "dbo";
    private string  _filter = "";
    private int     _kind   = -1; // index into ObjectCatalog.Kinds; -1 = every type
    private string? _error;
    private string? _busy;
    private bool    _settingInput;

    private List<object>    _rows = [];  // string (database / schema) | CatalogObject | GrepHit
    private List<GrepHit>   _hits = [];
    private string          _grepPattern = "";
    private List<string>    _grepDbs = []; // where the grep searches
    private int             _grepDone;     // databases searched so far
    private bool            _grepping;
    private CancellationTokenSource? _loadCts, _grepCts;
    private readonly CancellationTokenSource _closing = new();
    // Loaded on a database's first grep and kept while the explorer is open, so a reopened
    // explorer sees procs altered in the meantime.
    private readonly Dictionary<string, Task<SearchIndex>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private ExplorerAction? _action;

    /// <param name="dispatch">Marshals background results onto the UI thread (tests pass a queue).</param>
    /// <param name="grepDelay">Pause in typing before a grep runs.</param>
    /// <param name="previewDelay">Pause on a row before its preview loads (holding ↓ doesn't query every row).</param>
    public ObjectExplorer(ExplorerSource src, Action<Action>? dispatch = null, TimeSpan? grepDelay = null, TimeSpan? previewDelay = null)
    {
        _src          = src;
        _db           = src.Database;
        _dispatch     = dispatch ?? Application.Invoke;
        _delay        = grepDelay ?? TimeSpan.FromMilliseconds(250);
        _previewDelay = previewDelay ?? TimeSpan.FromMilliseconds(120);

        Dialog = new Dialog
        {
            Width       = Dim.Percent(90),
            Height      = Dim.Percent(85),
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(Dialog);
        Dialog.NoHotKey();

        _info   = new Label { X = 0, Y = 0, Width = Dim.Fill(), ColorScheme = Theme.StatusBar }.NoHotKey();
        _prompt = new Label { X = 0, Y = 1, ColorScheme = Theme.StatusBar }.NoHotKey();
        _input  = new TextField { X = Pos.Right(_prompt) + 1, Y = 1, Width = Dim.Fill(1), ColorScheme = Theme.Input, Visible = false };
        _list   = new KeyListView { X = 0, Y = 2, Width = Dim.Percent(50), Height = Dim.Fill(1), ColorScheme = Theme.Picker };
        _hint   = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), ColorScheme = Theme.StatusBar }.NoHotKey();

        _previewFrame = new FrameView
        {
            Title = " preview ", X = Pos.Right(_list) + 1, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1),
            ColorScheme = Theme.Input
        };
        Theme.SetRounded(_previewFrame);
        _previewFrame.NoHotKey();
        _preview = new EditorView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = false, CanFocus = false, ColorScheme = Theme.Input
        };
        SqlSyntaxHighlighter.Attach(_preview, () => _mode == Mode.Grep && _grepPattern.Trim().Length >= 2 ? _grepPattern : null);
        _previewFrame.Add(_preview);
        Dialog.Add(_info, _prompt, _input, _list, _previewFrame, _hint);

        _list.PreviewKey        = OnListKey;
        _list.OpenSelectedItem += (_, _) => Open();
        _list.RowRender        += (_, e) => ColorRow(e);
        _list.SelectedItemChanged += (_, _) => SchedulePreview();
        _input.TextChanged     += (_, _) => OnInputChanged();
        _input.KeyDown         += (_, k) => { if (OnInputKey(k)) k.Handled = true; };

        EnsureLoaded(_db);
        Refresh();
        _list.SetFocus();
    }

    public Dialog Dialog { get; }

    private readonly Action<Action> _dispatch;
    private readonly TimeSpan       _delay, _previewDelay;
    private void Dispatch(Action a) => _dispatch(a);

    // Exposed for tests
    internal Level CurrentLevel => _level;
    internal Mode  CurrentMode  => _mode;
    internal string Info        => _info.Text ?? "";
    internal IReadOnlyList<string> Lines => _rows.Select(Format).ToList();
    internal object? Selected   => _list.SelectedItem >= 0 && _list.SelectedItem < _rows.Count ? _rows[_list.SelectedItem] : null;
    internal ExplorerAction? Action => _action;
    internal string PreviewText  => _previewText;
    internal string PreviewTitle => _previewFrame.Title ?? "";
    internal int    PreviewTop   => _preview.TopRow;
    internal bool   PreviewShown => _previewFrame.Visible;

    public static ExplorerAction? Run(ExplorerSource src)
    {
        var x = new ObjectExplorer(src);
        Application.Run(x.Dialog);
        x._loadCts?.Cancel();
        x._grepCts?.Cancel();
        x._previewCts?.Cancel();
        x._closing.Cancel();
        x.Dialog.Dispose();
        return x._action;
    }

    /// <summary>A keystroke doesn't cancel a load in progress: the next one needs the same index.</summary>
    private Task<SearchIndex> IndexFor(string db)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(db, out var t) && !t.IsFaulted && !t.IsCanceled) return t;
            return _indexes[db] = Task.Run(() => _src.LoadSearchIndex(db, _closing.Token));
        }
    }

    // ── Data ───────────────────────────────────────────────────────────────

    private List<CatalogObject>? Objects(string db) => _src.Cache.GetValueOrDefault(db);

    private void EnsureLoaded(string db)
    {
        if (Objects(db) is not null) return;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _busy  = $"loading {db}…";
        _error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var objs = await _src.LoadObjects(db, cts.Token);
                Dispatch(() =>
                {
                    _src.Cache[db] = objs;
                    if (cts.IsCancellationRequested || !db.Equals(_db, StringComparison.OrdinalIgnoreCase)) return;
                    _busy = null;
                    // Opening on dbo: if this database keeps nothing there, show every schema instead.
                    if (_level == Level.Objects && _schema == "dbo" && !objs.Any(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)))
                        _schema = AllSchemas;
                    Refresh();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _busy  = null;
                    _error = $"{db}: {ex.Message}";
                    Refresh();
                });
            }
        });
    }

    private List<object> BrowseRows()
    {
        switch (_level)
        {
            case Level.Databases:
                return _src.Databases.Where(d => Matches(d)).Cast<object>().ToList();

            case Level.Schemas:
            {
                var objs = Objects(_db);
                if (objs is null) return [];
                var schemas = objs.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
                                  .Order(StringComparer.OrdinalIgnoreCase).Where(s => Matches(s));
                return (_filter.Length == 0 ? [AllSchemas] : Enumerable.Empty<string>()).Concat(schemas).Cast<object>().ToList();
            }

            default:
            {
                var objs = Objects(_db);
                if (objs is null) return [];
                IEnumerable<CatalogObject> q = objs.Where(o =>
                    (_schema == AllSchemas || o.Schema.Equals(_schema, StringComparison.OrdinalIgnoreCase))
                    && (_kind < 0 || o.Kind == ObjectCatalog.Kinds[_kind])
                    && Matches(_schema == AllSchemas ? o.FullName : o.Name));

                var byName = StringComparer.OrdinalIgnoreCase;
                q = _sort switch
                {
                    Sort.NameDesc    => q.OrderByDescending(o => o.Name, byName).ThenByDescending(o => o.Schema, byName),
                    Sort.TypeAsc     => q.OrderBy(o => ObjectCatalog.KindRank(o.Kind)).ThenBy(o => o.Name, byName),
                    Sort.TypeDesc    => q.OrderByDescending(o => ObjectCatalog.KindRank(o.Kind)).ThenBy(o => o.Name, byName),
                    Sort.NewestFirst => q.OrderByDescending(o => o.Modified),
                    Sort.OldestFirst => q.OrderBy(o => o.Modified),
                    _                => q.OrderBy(o => o.Name, byName).ThenBy(o => o.Schema, byName)
                };
                return q.Cast<object>().ToList();
            }
        }
    }

    private bool Matches(string text) =>
        _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    // ── Rendering ──────────────────────────────────────────────────────────

    private int _nameW = 20;

    private void Refresh(object? select = null)
    {
        var keep = select ?? Selected;
        _rows = _mode == Mode.Grep ? _hits.Cast<object>().ToList() : BrowseRows();

        _nameW = Math.Clamp(_rows.Select(r => NameOf(r).Length).DefaultIfEmpty(10).Max(), 10, 60);
        _list.SetSource(new ObservableCollection<string>(_rows.Select(Format)));
        int idx = keep is null ? -1 : _rows.FindIndex(r => SameRow(r, keep));
        _list.SelectedItem = _rows.Count == 0 ? -1 : Math.Max(0, idx);
        if (_rows.Count > 0) _list.EnsureSelectedItemVisible();
        UpdateChrome();
        SchedulePreview();
    }

    private static bool SameRow(object a, object b) =>
        a is string sa && b is string sb ? sa.Equals(sb, StringComparison.OrdinalIgnoreCase) : a.Equals(b);

    private string NameOf(object row) => row switch
    {
        string s when _level == Level.Schemas && s == AllSchemas => "(all schemas)",
        string s        => s,
        CatalogObject o => _schema == AllSchemas ? o.FullName : o.Name,
        GrepHit h       => _grepDbs.Count > 1 ? $"{h.Database}.{h.FullName}" : h.FullName,
        _               => ""
    };

    private string Format(object row)
    {
        var name = NameOf(row);
        if (name.Length > _nameW) name = name[..(_nameW - 1)] + "…";
        switch (row)
        {
            case string db when _level == Level.Databases:
                return $" {name.PadRight(_nameW)}  {(db.Equals(_src.Database, StringComparison.OrdinalIgnoreCase) ? "● current" : "")}";

            case string schema:
            {
                var objs = (Objects(_db) ?? []).Where(o => schema == AllSchemas || o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)).ToList();
                var counts = string.Join(" · ", ObjectCatalog.Kinds
                    .Select(k => (k, n: objs.Count(o => o.Kind == k)))
                    .Where(x => x.n > 0)
                    .Select(x => $"{x.n:N0} {x.k}{(x.n == 1 ? "" : "s")}"));
                return $" {name.PadRight(_nameW)}  {objs.Count,6:N0}   {counts}";
            }

            case CatalogObject o:
                return $" {name.PadRight(_nameW)}  {o.Kind,-8}  {o.Modified:yyyy-MM-dd HH:mm}";

            case GrepHit h:
                var where = h.IsColumn ? "col" : h.IsName ? "name" : h.Line.ToString();
                return $" {name.PadRight(_nameW)}  {h.Kind,-8} {where,5} │ {h.Text.Replace('\t', ' ').Trim()}";

            default:
                return "";
        }
    }

    private void ColorRow(ListViewRowEventArgs e)
    {
        if (e.Row < 0 || e.Row >= _rows.Count || e.Row == _list.SelectedItem) return;
        var p    = Theme.Current;
        var kind = _rows[e.Row] switch { CatalogObject o => o.Kind, GrepHit h => h.Kind, _ => null };
        var fg   = kind switch
        {
            "view"     => p.Function,
            "function" => p.Number,
            "proc"     => p.Keyword,
            "trigger"  => p.Ddl,
            "column"   => p.Variable,
            _          => p.Fg
        };
        e.RowAttribute = new TAttr(fg, p.Bg);
    }

    private void UpdateChrome()
    {
        string crumb = _level switch
        {
            Level.Databases => "databases",
            Level.Schemas   => $"{_db} ›",
            _               => $"{_db} › {(_schema == AllSchemas ? "(all schemas)" : _schema)}"
        };

        string info;
        if (_mode == Mode.Grep)
        {
            var where   = _grepDbs.Count == 1 ? _grepDbs[0] : Fmt.Plural(_grepDbs.Count, "database");
            var objects = _hits.Select(h => (h.Database, h.FullName)).Distinct().Count();
            var status  = _grepping ? "searching…" + (_grepDbs.Count > 1 ? $" {_grepDone}/{_grepDbs.Count}" : "")
                : _grepPattern.Trim().Length < 2 ? "type at least 2 characters"
                : _error is not null && _hits.Count == 0 ? $"⚠ {_error}"
                : $"{_hits.Count:N0} match{(_hits.Count == 1 ? "" : "es")} in {Fmt.Plural(objects, "object")}"
                  + (_hits.Count >= ObjectCatalog.MaxGrepHits ? $" (first {ObjectCatalog.MaxGrepHits:N0})" : "")
                  + (_error is not null ? $" · ⚠ {_error}" : "");
            info = $" grep in {where} · {status}";
        }
        else
        {
            var noun = _level switch { Level.Databases => "database", Level.Schemas => "schema", _ => "object" };
            int n    = _level == Level.Schemas ? _rows.Count(r => r is string s && s != AllSchemas) : _rows.Count;
            info = $" ◈ {crumb}   {Fmt.Plural(n, noun)}";
            if (_level == Level.Objects)
                info += $" · type: {(_kind < 0 ? "all" : ObjectCatalog.Kinds[_kind] + "s")} · sort: {SortLabel()}";
            if (_busy is not null)  info += $" · {_busy}";
            if (_error is not null) info += $" · ⚠ {_error}";
        }
        _info.Text = info;

        _prompt.Text   = _mode == Mode.Grep ? " grep ›" : " /";
        _prompt.Visible = _mode != Mode.Browse || _filter.Length > 0;
        _input.Visible  = _prompt.Visible;

        _hint.Text = _mode switch
        {
            Mode.Find => " type to filter · ↑↓ move · Enter done · Esc clear",
            Mode.Grep => " text in names, columns & definitions · ↑↓ move · Enter open · Tab then i: insert · Esc back",
            _ => _level switch
            {
                Level.Objects => " ← up · → peek · ^F find · ^G grep · t type · s sort · i insert · r top 1000 · F5 top 100 · p preview · ^D/^U scroll",
                Level.Schemas => " ← up · → open · ^F find · ^G grep · p preview · Esc close",
                _             => " → open · ^F find · ^G grep every listed database · p preview · Esc close"
            }
        };
        Dialog.Title = _mode == Mode.Grep ? " grep " : " objects ";
        Dialog.SetNeedsDraw();
    }

    private string SortLabel() => _sort switch
    {
        Sort.NameDesc    => "name ↓",
        Sort.TypeAsc     => "type ↑",
        Sort.TypeDesc    => "type ↓",
        Sort.NewestFirst => "modified ↓",
        Sort.OldestFirst => "modified ↑",
        _                => "name ↑"
    };

    // ── Navigation ─────────────────────────────────────────────────────────

    private void Up()
    {
        switch (_level)
        {
            case Level.Objects:
                _level = Level.Schemas;
                ResetFilter();
                Refresh(_schema);
                break;
            case Level.Schemas:
                _level = Level.Databases;
                ResetFilter();
                Refresh(_db);
                break;
        }
    }

    private void Open()
    {
        switch (Selected)
        {
            case string db when _level == Level.Databases:
                _db     = db;
                _level  = Level.Schemas;
                _busy   = null;
                _error  = null;
                ResetFilter();
                EnsureLoaded(db);
                Refresh(AllSchemas);
                break;
            case string schema:
                _schema = schema;
                _level  = Level.Objects;
                ResetFilter();
                Refresh(select: null);
                _list.SelectedItem = _rows.Count > 0 ? 0 : -1;
                break;
            case CatalogObject o:
                Peek(_db, o.Schema, o.Name, null, null);
                break;
            case GrepHit h:
                Peek(h.Database, h.Schema, h.Object, h.Line > 0 ? h.Line : null, h.IsColumn ? h.Column : h.IsName ? null : _grepPattern);
                break;
        }
    }

    private void Peek(string db, string schema, string name, int? line, string? search)
    {
        var key = ExplorerSource.DescriptionKey(db, schema, name);
        if (_src.Descriptions.TryGetValue(key, out var cached) && cached is not null)
        {
            Dialogs.ShowStructure(cached, line, search);
            return;
        }

        _busy = $"opening {schema}.{name}…";
        UpdateChrome();
        _ = Task.Run(async () =>
        {
            try
            {
                var desc = await _src.Describe(db, schema, name, CancellationToken.None);
                Dispatch(() =>
                {
                    _src.Descriptions[key] = desc;
                    _busy  = null;
                    _error = desc is null ? $"{schema}.{name} no longer exists" : null;
                    UpdateChrome();
                    if (desc is not null) Dialogs.ShowStructure(desc, line, search);
                });
            }
            catch (Exception ex)
            {
                Dispatch(() => { _busy = null; _error = ex.Message; UpdateChrome(); });
            }
        });
    }

    private void ResetFilter()
    {
        _filter = "";
        _mode   = Mode.Browse;
        SetInput("");
        _list.SetFocus();
    }

    private void SetInput(string text)
    {
        _settingInput = true;
        _input.Text   = text;
        _input.CursorPosition = text.Length;
        _settingInput = false;
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _list.SelectedItem = Math.Clamp(_list.SelectedItem + delta, 0, _rows.Count - 1);
        _list.EnsureSelectedItemVisible();
        _list.SetNeedsDraw();
    }

    // ── Find (Ctrl+F) and grep (Ctrl+G) ────────────────────────────────────

    private void StartFind()
    {
        _mode = Mode.Find;
        SetInput(_filter);
        UpdateChrome();
        _input.SetFocus();
    }

    private void StartGrep()
    {
        _mode  = Mode.Grep;
        _error = null;
        SetInput(_grepPattern); // Ctrl+G again resumes the last grep, re-run if you're now somewhere else
        if (GrepScope().SequenceEqual(_grepDbs, StringComparer.OrdinalIgnoreCase)) Refresh(select: null);
        else QueueGrep(_grepPattern);
        _input.SetFocus();
    }

    /// <summary>From the database list: every database it shows. Anywhere else: the open database.</summary>
    private List<string> GrepScope() => _level == Level.Databases ? _src.Databases.Where(d => Matches(d)).ToList() : [_db];

    private void LeaveGrep()
    {
        _grepCts?.Cancel();
        _grepping = false;
        _error    = null;
        _mode     = Mode.Browse;
        SetInput(_filter);
        Refresh();
        _list.SetFocus();
    }

    private void OnInputChanged()
    {
        if (_settingInput) return;
        var text = _input.Text ?? "";
        if (_mode == Mode.Find)
        {
            _filter = text;
            Refresh();
        }
        else if (_mode == Mode.Grep)
        {
            QueueGrep(text);
        }
    }

    private const int GrepParallelism = 4;

    /// <summary>
    /// Greps every database in scope, a few at a time. Hits show as each database finishes, in
    /// list order; a database that fails is named in the info line and the rest carry on.
    /// </summary>
    private void QueueGrep(string pattern)
    {
        _grepCts?.Cancel();
        _grepPattern = pattern;
        _grepDbs     = GrepScope();
        _error       = null;
        if (pattern.Trim().Length < 2)
        {
            _grepping = false;
            _hits     = [];
            Refresh();
            return;
        }

        var cts    = _grepCts = new CancellationTokenSource();
        var dbs    = _grepDbs;
        var found  = new List<GrepHit>?[dbs.Count];
        var failed = new List<string>();
        _grepDone = 0;
        _grepping = dbs.Count > 0;
        if (!_grepping)
        {
            _hits = [];
            Refresh();
            return;
        }
        UpdateChrome();
        _ = Task.Run(async () =>
        {
            try
            {
                if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cts.Token); // wait for typing to pause
                var options = new ParallelOptions { MaxDegreeOfParallelism = GrepParallelism, CancellationToken = cts.Token };
                await Parallel.ForEachAsync(Enumerable.Range(0, dbs.Count), options, async (i, ct) =>
                {
                    var db = dbs[i];
                    List<GrepHit> hits;
                    string? failure = null;
                    try
                    {
                        var index = await IndexFor(db).WaitAsync(ct);
                        hits = index.Grep(pattern, ObjectCatalog.MaxGrepHits, db).Select(h => h with { Database = db }).ToList();
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        hits    = [];
                        failure = dbs.Count == 1 ? ex.Message : $"{db}: {ex.Message}";
                    }
                    Dispatch(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        found[i] = hits;
                        if (failure is not null) failed.Add(failure);
                        _grepDone++;
                        _hits  = found.Where(f => f is not null).SelectMany(f => f!).Take(ObjectCatalog.MaxGrepHits).ToList();
                        _error = failed.Count == 0 ? null : failed[0] + (failed.Count > 1 ? $" (+{failed.Count - 1} more)" : "");
                        if (_hits.Count >= ObjectCatalog.MaxGrepHits) cts.Cancel(); // enough: stop the databases still running
                        _grepping = _grepDone < dbs.Count && !cts.IsCancellationRequested;
                        Refresh(select: null);
                    });
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    // ── Preview (right pane) ───────────────────────────────────────────────

    private bool    _showPreview = true;
    private string? _pendingKey;   // description being loaded for the preview

    private void TogglePreview()
    {
        _showPreview          = !_showPreview;
        _previewFrame.Visible = _showPreview;
        _list.Width           = _showPreview ? Dim.Percent(50) : Dim.Fill();
        Refresh();
    }

    private void SchedulePreview()
    {
        if (!_showPreview) return;
        switch (Selected)
        {
            case CatalogObject o:
                PreviewObject(_db, o.Schema, o.Name);
                break;
            case GrepHit h:
                PreviewObject(h.Database, h.Schema, h.Object);
                break;
            case string s when _level == Level.Schemas:
                CancelPreviewLoad();
                SetPreview($"schema\u0001{_db}\u0001{s}", s == AllSchemas ? $" {_db} " : $" schema {s} ", SchemaSummary(s), 0);
                break;
            case string db:
                CancelPreviewLoad();
                SetPreview($"db\u0001{db}", $" database {db} ", DatabaseSummary(db), 0);
                break;
            default:
                CancelPreviewLoad();
                SetPreview(null, " preview ", "", 0);
                break;
        }
    }

    private void CancelPreviewLoad()
    {
        _previewCts?.Cancel();
        _pendingKey = null;
    }

    private void PreviewObject(string db, string schema, string name)
    {
        var key = ExplorerSource.DescriptionKey(db, schema, name);
        if (_src.Descriptions.TryGetValue(key, out var cached))
        {
            CancelPreviewLoad();
            Render(key, cached);
            return;
        }
        if (_pendingKey == key) return; // already on its way

        CancelPreviewLoad();
        var cts = _previewCts = new CancellationTokenSource();
        _pendingKey = key;
        SetPreview(key, $" {schema}.{name} ", "-- loading…", 0);
        _ = Task.Run(async () =>
        {
            try
            {
                if (_previewDelay > TimeSpan.Zero) await Task.Delay(_previewDelay, cts.Token); // skip rows you only pass over
                var desc = await _src.Describe(db, schema, name, cts.Token);
                Dispatch(() =>
                {
                    _src.Descriptions[key] = desc;
                    if (cts.IsCancellationRequested) return;
                    _pendingKey = null;
                    Render(key, desc);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _pendingKey = null;
                    SetPreview(key, $" {schema}.{name} ", $"-- could not load: {ex.Message}", 0);
                });
            }
        });
    }

    /// <summary>Shows a description, scrolled to the selected grep hit's line or column.</summary>
    private void Render(string key, ObjectDescription? d)
    {
        if (d is null)
        {
            SetPreview(key, " preview ", "-- not found (dropped since the list was loaded?)", 0);
            return;
        }

        var (text, defLine) = StructurePreview.Build(d);
        int target = Selected switch
        {
            GrepHit { IsColumn: true } h => Math.Max(0, Array.FindIndex(text.Split('\n'),
                                               l => l.StartsWith(h.Column + " ", StringComparison.OrdinalIgnoreCase))),
            GrepHit h when defLine >= 0 && h.Line > 0 => defLine + h.Line - 1,
            _                            => 0
        };
        SetPreview(key, $" {d.Kind.ToLowerInvariant()} {d.FullName} ", text, target);
    }

    private void SetPreview(string? key, string title, string text, int line)
    {
        if (_previewKey != key || _previewText != text)
            _preview.Text = _previewText = text;
        _previewKey         = key;
        _previewFrame.Title = title;

        // Put the line a few rows below the top, so there is context above it.
        line = Math.Clamp(line, 0, Math.Max(0, _preview.Lines - 1));
        EditorText.MoveCursor(_preview, line, 0);
        _preview.TopRow = Math.Max(0, line - 3);
        _preview.SetNeedsDraw();
    }

    private void ScrollPreview(int direction)
    {
        int half = Math.Max(1, _preview.Viewport.Height / 2);
        _preview.TopRow = Math.Clamp(_preview.TopRow + direction * half, 0, Math.Max(0, _preview.Lines - 1));
        _preview.SetNeedsDraw();
    }

    private string SchemaSummary(string schema)
    {
        var objs = (Objects(_db) ?? [])
            .Where(o => schema == AllSchemas || o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var lines = new List<string> { $"-- {(schema == AllSchemas ? "every schema in " + _db : "schema " + schema)} · {Fmt.Plural(objs.Count, "object")}" };

        if (schema == AllSchemas)
        {
            lines.Add("");
            foreach (var g in objs.GroupBy(o => o.Schema, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                lines.Add($"{g.Key}  -- {Fmt.Plural(g.Count(), "object")}");
            return string.Join("\n", lines);
        }

        foreach (var kind in ObjectCatalog.Kinds)
        {
            var names = objs.Where(o => o.Kind == kind).Select(o => o.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) continue;
            lines.Add("");
            lines.Add($"-- {kind}s ({names.Count:N0})");
            lines.AddRange(names.Take(500));
            if (names.Count > 500) lines.Add($"-- … {names.Count - 500:N0} more");
        }
        return string.Join("\n", lines);
    }

    private string DatabaseSummary(string db)
    {
        if (Objects(db) is not { } objs)
            return $"-- {db}\n-- → / Enter to browse it";
        var lines = new List<string> { $"-- {db} · {Fmt.Plural(objs.Count, "object")}", "" };
        foreach (var g in objs.GroupBy(o => o.Schema, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{g.Key}  -- {Fmt.Plural(g.Count(), "object")}");
        return string.Join("\n", lines);
    }

    // ── Keys ───────────────────────────────────────────────────────────────

    private static bool IsChar(Key k, char c) => !k.IsCtrl && !k.IsAlt && k.AsRune.Value == c;

    /// <summary>Keys that work in every mode: scroll the preview.</summary>
    private bool OnPreviewKey(Key k)
    {
        if (k.KeyCode == (KeyCode.D | KeyCode.CtrlMask)) { ScrollPreview(1);  return true; }
        if (k.KeyCode == (KeyCode.U | KeyCode.CtrlMask)) { ScrollPreview(-1); return true; }
        return false;
    }

    private bool OnListKey(Key k)
    {
        var kc = k.KeyCode;
        if (OnPreviewKey(k)) return true;
        if (kc == (KeyCode.F | KeyCode.CtrlMask) || IsChar(k, '/')) { if (_mode != Mode.Grep) StartFind(); return true; }
        if (kc == (KeyCode.G | KeyCode.CtrlMask))                   { if (_mode == Mode.Grep) LeaveGrep(); else StartGrep(); return true; }

        if (kc == KeyCode.Esc)
        {
            if (_mode == Mode.Grep)       LeaveGrep();
            else if (_filter.Length > 0) { _filter = ""; SetInput(""); Refresh(); }
            else                          Application.RequestStop(Dialog);
            return true;
        }

        if (kc is KeyCode.CursorRight or KeyCode.Enter || IsChar(k, 'l') || kc == KeyCode.F12) { Open(); return true; }
        if (_mode == Mode.Grep)
        {
            // The list has the focus (Tab from the search box): i inserts the object the hit is in
            if (!IsChar(k, 'i') || Selected is not GrepHit h) return false;
            Finish(new ExplorerAction(new CatalogObject(h.Schema, h.Object, h.Kind, default), h.Database, ExplorerVerb.Insert));
            return true;
        }

        if (IsChar(k, 'p')) { TogglePreview(); return true; }
        if (kc is KeyCode.CursorLeft or KeyCode.Backspace || IsChar(k, 'h')) { Up(); return true; }
        if (IsChar(k, 'j')) { Move(1);  return true; }
        if (IsChar(k, 'k')) { Move(-1); return true; }

        if (_level != Level.Objects) return false;

        if (IsChar(k, 't'))
        {
            _kind = _kind + 1 >= ObjectCatalog.Kinds.Length ? -1 : _kind + 1;
            Refresh();
            return true;
        }
        if (IsChar(k, 's'))
        {
            _sort = (Sort)(((int)_sort + 1) % Enum.GetValues<Sort>().Length);
            Refresh();
            return true;
        }
        if (Selected is CatalogObject o)
        {
            ExplorerVerb? verb = IsChar(k, 'i') ? ExplorerVerb.Insert
                               : IsChar(k, 'r') ? ExplorerVerb.Top1000
                               : kc == KeyCode.F5 ? ExplorerVerb.Top100
                               : null;
            if (verb is null) return false;
            if (verb != ExplorerVerb.Insert && o.Kind is not ("table" or "view")) return true;
            Finish(new ExplorerAction(o, _db, verb.Value));
            return true;
        }
        return false;
    }

    private void Finish(ExplorerAction action)
    {
        _action = action;
        Application.RequestStop(Dialog);
    }

    private bool OnInputKey(Key k)
    {
        if (_mode == Mode.Browse) return false;
        if (OnPreviewKey(k)) return true;
        switch (k.KeyCode)
        {
            case KeyCode.CursorDown: Move(1);   return true;
            case KeyCode.CursorUp:   Move(-1);  return true;
            case KeyCode.PageDown:   Move(10);  return true;
            case KeyCode.PageUp:     Move(-10); return true;

            case KeyCode.Enter:
                if (_mode == Mode.Grep) Open();
                else { _mode = Mode.Browse; UpdateChrome(); _list.SetFocus(); }
                return true;

            case KeyCode.Esc:
                if (_mode == Mode.Grep) LeaveGrep();
                else { _filter = ""; ResetFilter(); Refresh(); }
                return true;
        }
        if (k.KeyCode == (KeyCode.G | KeyCode.CtrlMask) && _mode == Mode.Grep) { LeaveGrep(); return true; }
        if (k.KeyCode == (KeyCode.F | KeyCode.CtrlMask) && _mode == Mode.Grep) return true;
        return false;
    }
}

/// <summary>Formats an <see cref="ObjectDescription"/> as SQL-ish text for the explorer's preview pane.</summary>
internal static class StructurePreview
{
    /// <returns>The text, and the 0-based line its definition starts on (-1 when it has none).</returns>
    public static (string Text, int DefinitionLine) Build(ObjectDescription d)
    {
        var lines = new List<string>();
        var rows  = d.Details.FirstOrDefault(x => x.StartsWith('≈'));
        lines.Add($"-- {d.Kind.ToLowerInvariant()} {d.FullName}{(rows is null ? "" : " · " + rows)}");

        var g = d.Grid;
        if (g.Rows.Count > 0 && g.Columns.Length > 2 && g.Columns[0] == "Column")
        {
            // name  type  NULL / NOT NULL  -- PK · default … · → referenced table
            lines.Add("");
            int nameW = g.Rows.Max(r => Cell(r, 0).Length);
            int typeW = g.Rows.Max(r => Cell(r, 1).Length);
            foreach (var r in g.Rows)
            {
                var notes = new List<string>();
                if (Cell(r, 3).Length > 0) notes.Add(Cell(r, 3));
                if (Cell(r, 4).Length > 0) notes.Add("default " + Cell(r, 4));
                if (Cell(r, 5).Length > 0) notes.Add("→ " + Cell(r, 5));
                var line = $"{Cell(r, 0).PadRight(nameW)}  {Cell(r, 1).PadRight(typeW)}  {(Cell(r, 2) == "NO" ? "NOT NULL" : "NULL"),-8}";
                lines.Add(notes.Count > 0 ? $"{line}  -- {string.Join(" · ", notes)}" : line.TrimEnd());
            }
        }
        else if (g.Rows.Count > 0 && g.Columns.Length > 2 && g.Columns[0] == "Parameter")
        {
            lines.Add("");
            int nameW = g.Rows.Max(r => Cell(r, 0).Length);
            int typeW = g.Rows.Max(r => Cell(r, 1).Length);
            foreach (var r in g.Rows)
                lines.Add($"--   {Cell(r, 0).PadRight(nameW)}  {Cell(r, 1).PadRight(typeW)}  {Cell(r, 2)}{(Cell(r, 3).Length > 0 ? "  = default" : "")}".TrimEnd());
        }

        var other = d.Details.Where(x => !x.StartsWith('≈')).ToList();
        if (other.Count > 0)
        {
            lines.Add("");
            lines.AddRange(other.Select(x => "-- " + x));
        }
        if (d.AlsoIn.Count > 0)
            lines.Add($"-- also in schema: {string.Join(", ", d.AlsoIn)}");

        int defLine = -1;
        if (!string.IsNullOrEmpty(d.Definition))
        {
            lines.Add("");
            defLine = lines.Count;
            lines.AddRange(d.Definition.Replace("\r\n", "\n").Split('\n'));
        }
        return (string.Join("\n", lines), defLine);
    }

    private static string Cell(string?[] row, int i) => i < row.Length ? row[i] ?? "" : "";
}
