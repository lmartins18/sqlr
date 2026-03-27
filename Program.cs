using Sqlr.Cli;
using Sqlr.Config;
using Sqlr.Db;
using Sqlr.Tui;
using Terminal.Gui;

var parsed = Args.Parse(args);

switch (parsed.Command)
{
    case CliCommand.AddToPath:
        PathInstaller.Install();
        return;

    case CliCommand.ConnectionsList:
        ListConnections();
        return;

    case CliCommand.ConnectionsAdd:
        AddConnectionCli();
        return;

    case CliCommand.ConnectionsRemove:
        if (parsed.ConnectionName is null) { Console.Error.WriteLine("Usage: sqlr connections remove <name>"); return; }
        RemoveConnection(parsed.ConnectionName);
        return;

    case CliCommand.ConnectionsTest:
        if (parsed.ConnectionName is null) { Console.Error.WriteLine("Usage: sqlr connections test <name>"); return; }
        await TestConnection(parsed.ConnectionName);
        return;

    case CliCommand.Connect:
        if (parsed.ConnectionName is null) { Console.Error.WriteLine("Usage: sqlr -c <name>"); return; }
        await ConnectDirectAsync(parsed.ConnectionName);
        return;

    case CliCommand.LaunchPicker:
    default:
        await LaunchPickerAsync();
        return;
}

// ── Non-TUI helpers ───────────────────────────────────────────────────────

static void ListConnections()
{
    var store = ConnectionStore.Load();
    if (store.Connections.Count == 0) { Console.WriteLine("No connections saved.  Run: sqlr connections add"); return; }

    int nameW   = Math.Max(4,  store.Connections.Max(c => c.Name.Length));
    int serverW = Math.Max(6,  store.Connections.Max(c => c.Server.Length));
    int dbW     = Math.Max(8,  store.Connections.Max(c => c.Database.Length));

    var header = $"{"NAME".PadRight(nameW)}  {"SERVER".PadRight(serverW)}  {"DATABASE".PadRight(dbW)}  AUTH";
    Console.WriteLine(header);
    Console.WriteLine(new string('─', header.Length));
    foreach (var c in store.Connections)
        Console.WriteLine($"{c.Name.PadRight(nameW)}  {c.Server.PadRight(serverW)}  {c.Database.PadRight(dbW)}  {c.AuthType}");
}

static void AddConnectionCli()
{
    static string Prompt(string label, bool required = true)
    {
        while (true)
        {
            Console.Write(label);
            var val = Console.ReadLine()?.Trim() ?? "";
            if (!required || !string.IsNullOrEmpty(val)) return val;
            Console.Error.WriteLine("  (required)");
        }
    }

    var name     = Prompt("Connection name : ");
    var server   = Prompt("Server          : ");
    var database = Prompt("Database        : ");
    Console.Write("Auth [windows/sql] (default: windows) : ");
    var auth = (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "") == "sql" ? "sql" : "windows";

    string? username = null, password = null;
    if (auth == "sql")
    {
        username = Prompt("Username : ");
        Console.Write("Password : ");
        password = Console.ReadLine();
    }

    var store = ConnectionStore.Load();
    store.Add(new SqlrConnection { Name = name, Server = server, Database = database, AuthType = auth, Username = username, Password = password });
    Console.WriteLine($"Saved '{name}'.");
}

static void RemoveConnection(string name)
{
    var store = ConnectionStore.Load();
    if (store.Remove(name)) Console.WriteLine($"Removed '{name}'.");
    else Console.Error.WriteLine($"Not found: '{name}'.");
}

static async Task TestConnection(string name)
{
    var store = ConnectionStore.Load();
    var conn  = store.Find(name);
    if (conn is null) { Console.Error.WriteLine($"Not found: '{name}'."); return; }
    Console.Write($"Testing '{name}'... ");
    var (ok, err) = await SqlRunner.TestAsync(conn.ConnectionString);
    Console.WriteLine(ok ? "OK" : $"FAILED: {err}");
}

// ── TUI helpers ───────────────────────────────────────────────────────────

static async Task LaunchPickerAsync()
{
    var store = ConnectionStore.Load();
    if (store.Connections.Count == 0)
    {
        Console.WriteLine("No connections found. Starting wizard...");
        AddConnectionCli();
        store = ConnectionStore.Load();
        if (store.Connections.Count == 0) return;
    }

    Application.Init();
    var selected = ConnectionPicker.Pick(store);
    Application.Shutdown();

    if (selected is null) return;
    await RunQueryScreenAsync(selected);
}

static async Task ConnectDirectAsync(string name)
{
    var store = ConnectionStore.Load();
    var conn  = store.Find(name);
    if (conn is null) { Console.Error.WriteLine($"Not found: '{name}'."); return; }
    await RunQueryScreenAsync(conn);
}

static async Task RunQueryScreenAsync(SqlrConnection conn)
{
    Console.Write($"Connecting to {conn.Server}/{conn.Database}... ");
    using var runner = new SqlRunner(conn.ConnectionString);
    if (!await runner.ConnectAsync())
    {
        Console.Error.WriteLine("Connection failed.");
        return;
    }
    Console.WriteLine("Connected.");

    Console.Write("Loading schema metadata... ");
    var schema = await DatabaseSchema.LoadAsync(conn.ConnectionString);
    Console.WriteLine($"{schema.Tables.Count} tables, {schema.Columns.Count} columns, {schema.Routines.Count} routines.");

    Application.Init();
    QueryScreen.Run(conn, runner, schema);
    Application.Shutdown();
}
