using System.Reflection;
using System.Text;
using Sqlr.Cli;
using Sqlr.Config;
using Sqlr.Db;
using Sqlr.Tui;
using Terminal.Gui;

Console.OutputEncoding = Encoding.UTF8;

var parsed = Args.Parse(args);

switch (parsed.Command)
{
    case CliCommand.Help:
        Console.WriteLine(Args.Usage);
        return 0;

    case CliCommand.Version:
        Console.WriteLine($"sqlr {Version()}");
        return 0;

    case CliCommand.Invalid:
        Console.Error.WriteLine($"sqlr: {parsed.Error}");
        Console.Error.WriteLine("Run 'sqlr --help' for usage.");
        return 2;

    case CliCommand.AddToPath:
        PathInstaller.Install();
        return 0;

    case CliCommand.ConnectionsList:
        ListConnections();
        return 0;

    case CliCommand.ConnectionsAdd:
        return AddConnectionCli(null) is null ? 1 : 0;

    case CliCommand.ConnectionsEdit:
    {
        var existing = ConnectionStore.Load().Find(parsed.ConnectionName!);
        if (existing is null) { Console.Error.WriteLine($"Not found: '{parsed.ConnectionName}'."); return 1; }
        return AddConnectionCli(existing) is null ? 1 : 0;
    }

    case CliCommand.ConnectionsRemove:
        return RemoveConnection(parsed.ConnectionName!) ? 0 : 1;

    case CliCommand.ConnectionsTest:
        return await TestConnection(parsed.ConnectionName!) ? 0 : 1;

    case CliCommand.RunQuery:
        return await RunNonInteractiveAsync(parsed);

    case CliCommand.Connect:
        return await RunInteractiveAsync(parsed.ConnectionName, parsed.Database, parsed.OpenFile);

    case CliCommand.LaunchPicker:
    default:
        return await RunInteractiveAsync(null, parsed.Database, null);
}

// ── Non-TUI helpers ───────────────────────────────────────────────────────

static string Version() =>
    (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0];

static void ListConnections()
{
    var store = ConnectionStore.Load();
    if (store.Connections.Count == 0) { Console.WriteLine("No connections saved.  Run: sqlr connections add"); return; }

    int nameW   = Math.Max(4,  store.Connections.Max(c => c.Name.Length));
    int serverW = Math.Max(6,  store.Connections.Max(c => c.Server.Length));
    int dbW     = Math.Max(8,  store.Connections.Max(c => c.DatabaseLabel.Length));

    var header = $"{"NAME".PadRight(nameW)}  {"SERVER".PadRight(serverW)}  {"DATABASE".PadRight(dbW)}  AUTH";
    Console.WriteLine(header);
    Console.WriteLine(new string('─', header.Length));
    foreach (var c in store.Connections)
        Console.WriteLine($"{c.Name.PadRight(nameW)}  {c.Server.PadRight(serverW)}  {c.DatabaseLabel.PadRight(dbW)}  {c.AuthLabel}");
}

/// <summary>Interactive add (existing == null) or edit. Returns the saved connection, or null if aborted.</summary>
static SqlrConnection? AddConnectionCli(SqlrConnection? existing)
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("This command is interactive; run it in a terminal.");
        return null;
    }

    static string Prompt(string label, string? current, bool required = true)
    {
        while (true)
        {
            Console.Write(current is null ? label : $"{label}[{current}] ");
            var val = Console.ReadLine()?.Trim();
            if (val is null) throw new OperationCanceledException(); // Ctrl+Z / Ctrl+D
            if (val.Length == 0 && current is not null) return current;
            if (!required || val.Length > 0) return val;
            Console.Error.WriteLine("  (required)");
        }
    }

    try
    {
        if (existing is not null)
            Console.WriteLine($"Editing '{existing.Name}' — press Enter to keep the value in [brackets].");

        var name     = Prompt("Connection name : ", existing?.Name);
        var server   = Prompt("Server          : ", existing?.Server);
        var database = Prompt("Database (blank = default) : ", existing?.Database, required: false);
        var auth     = Prompt("Auth [windows/sql/domain] : ", existing?.AuthType ?? "windows").ToLowerInvariant() switch
        {
            "sql"    => "sql",
            "domain" => "domain",
            _        => "windows"
        };

        var conn = new SqlrConnection
        {
            Name = name, Server = server, Database = database, AuthType = auth,
            TrustServerCertificate = existing?.TrustServerCertificate ?? true
        };

        if (conn.UsesPassword)
        {
            while (true)
            {
                conn.Username = Prompt(conn.IsDomainAuth ? @"Username (DOMAIN\user) : " : "Username : ", existing?.Username);
                if (!conn.IsDomainAuth || SqlrConnection.IsDomainUser(conn.Username)) break;
                Console.Error.WriteLine(@"  (use DOMAIN\user or user@domain)");
            }
            Console.Write(existing?.UsesPassword == true ? "Password (blank = unchanged) : " : "Password : ");
            var pw = ReadSecret();
            conn.Secret = pw.Length == 0 && existing?.UsesPassword == true ? existing.Secret : pw;
        }

        var store   = ConnectionStore.Load();
        bool clashes = store.Find(name) is not null
                       && (existing is null || !existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (clashes)
        {
            Console.Write($"A connection named '{name}' already exists. Replace it? [y/N] ");
            if (!(Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) ?? false))
                throw new OperationCanceledException();
        }

        if (existing is null) store.Add(conn);
        else store.Update(existing.Name, conn);
        Console.WriteLine($"Saved '{name}'.");
        return conn;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.Error.WriteLine("Cancelled.");
        return null;
    }
}

/// <summary>Reads a line without echoing it (shows * per character).</summary>
static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
        if (k.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
            continue;
        }
        if (k.Key == ConsoleKey.Escape) throw new OperationCanceledException();
        if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
    }
}

static bool RemoveConnection(string name)
{
    var store = ConnectionStore.Load();
    if (store.Remove(name)) { Console.WriteLine($"Removed '{name}'."); return true; }
    Console.Error.WriteLine($"Not found: '{name}'.");
    return false;
}

static async Task<bool> TestConnection(string name)
{
    var store = ConnectionStore.Load();
    var conn  = store.Find(name);
    if (conn is null) { Console.Error.WriteLine($"Not found: '{name}'."); return false; }
    Console.Write($"Testing '{name}' ({conn.Server}/{conn.DatabaseLabel})... ");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (ok, err, version) = await SqlRunner.TestAsync(conn.ConnectionString);
    Console.WriteLine(ok ? $"OK in {sw.ElapsedMilliseconds} ms (SQL Server {version})" : $"FAILED\n{err}");
    return ok;
}

// ── Non-interactive query mode ────────────────────────────────────────────

static async Task<int> RunNonInteractiveAsync(Args a)
{
    var conn = ConnectionStore.Load().Find(a.ConnectionName!);
    if (conn is null) { Console.Error.WriteLine($"Not found: '{a.ConnectionName}'. Run 'sqlr connections' to list them."); return 1; }

    string sql;
    try
    {
        sql = a.Query ?? (a.InputFile is not null ? File.ReadAllText(a.InputFile) : Console.In.ReadToEnd());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Cannot read {a.InputFile}: {ex.Message}");
        return 1;
    }

    var settings = SqlrSettings.Load();
    using var runner = new SqlRunner(conn.BuildConnectionString(a.Database))
    {
        MaxRows               = int.MaxValue,
        CommandTimeoutSeconds = settings.QueryTimeoutSeconds
    };

    var connectError = await runner.ConnectAsync();
    if (connectError is not null)
    {
        Console.Error.WriteLine($"Connection to {conn.Server} failed:\n{connectError}");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    ExecutionResult result;
    try
    {
        result = await runner.ExecuteAsync(sql, 0, cts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"sqlr: {ex.Message}");
        return 1;
    }

    var stdout = Console.Out;
    for (int i = 0; i < result.ResultSets.Count; i++)
    {
        if (i > 0 && a.Format is ExportFormat.Table or ExportFormat.Markdown) stdout.WriteLine();
        stdout.Write(ResultExport.Write(a.Format, result.ResultSets[i]));
    }

    // Messages (PRINT, rows affected, errors) go to stderr so stdout stays machine-readable.
    foreach (var m in result.Messages)
        Console.Error.WriteLine(m.Text);

    return result.HasErrors || result.Cancelled ? 1 : 0;
}

// ── TUI ───────────────────────────────────────────────────────────────────

static async Task<int> RunInteractiveAsync(string? connectionName, string? database, string? openFile)
{
    var settings = SqlrSettings.Load();
    var history  = QueryHistory.Load(settings.HistorySize);
    Theme.LoadSaved();
    string? preselect = connectionName;
    bool viaPicker = connectionName is null;

    string? initialText = null;
    if (openFile is not null)
    {
        try { initialText = File.ReadAllText(openFile); }
        catch (Exception ex) { Console.Error.WriteLine($"Cannot open {openFile}: {ex.Message}"); return 1; }
    }

    while (true)
    {
        var store = ConnectionStore.Load();
        SqlrConnection? conn;

        if (viaPicker)
        {
            if (store.Connections.Count == 0)
            {
                Console.WriteLine("No connections found. Starting wizard...");
                if (AddConnectionCli(null) is null) return 1;
                store = ConnectionStore.Load();
            }

            conn = RunTui(() => ConnectionPicker.Pick(store, preselect));
            if (conn is null) return 0;
        }
        else
        {
            conn = store.Find(connectionName!);
            if (conn is null) { Console.Error.WriteLine($"Not found: '{connectionName}'. Run 'sqlr connections' to list them."); return 1; }
        }

        preselect = conn.Name;
        var exit = await RunQueryScreenAsync(conn, database, settings, history, initialText, openFile);
        if (exit is null)
        {
            // Connection failed: back to the picker when we came from it, otherwise give up.
            if (!viaPicker) return 1;
            Console.WriteLine("Press Enter to return to the connection list...");
            Console.ReadLine();
            continue;
        }
        if (exit == ScreenExit.Quit) return 0;

        // F10 — switch connection: always via the picker from here on.
        viaPicker   = true;
        database    = null;
        initialText = null;
        openFile    = null;
    }
}

static async Task<ScreenExit?> RunQueryScreenAsync(SqlrConnection conn, string? database, SqlrSettings settings,
                                                   QueryHistory history, string? initialText, string? openFile)
{
    Console.Write($"Connecting to {conn.Server}/{database ?? conn.DatabaseLabel}... ");
    using var runner = new SqlRunner(conn.BuildConnectionString(database))
    {
        MaxRows               = settings.MaxRows,
        CommandTimeoutSeconds = settings.QueryTimeoutSeconds
    };

    var error = await runner.ConnectAsync();
    if (error is not null)
    {
        Console.Error.WriteLine("failed.");
        Console.Error.WriteLine(error);
        return null;
    }
    Console.WriteLine($"connected (SQL Server {runner.ServerVersion}).");

    // Schema loads in the background — the editor is usable immediately.
    var schema = new SchemaService(db => conn.BuildConnectionString(db));
    schema.Reload(runner.CurrentDatabase ?? conn.Database);

    var text = initialText ?? Drafts.Load(conn.Name);
    return RunTui(() => QueryScreen.Run(conn, runner, schema, settings, history, text, openFile));
}

/// <summary>Runs one TUI screen, always restoring the terminal — even if the screen throws.</summary>
static T RunTui<T>(Func<T> screen)
{
    Application.Init();
    try
    {
        return screen();
    }
    catch (Exception ex)
    {
        SqlrPaths.LogError(ex);
        throw;
    }
    finally
    {
        Application.Shutdown();
    }
}
