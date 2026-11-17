using Sqlr.Db;

namespace Sqlr.Cli;

public enum CliCommand
{
    LaunchPicker,
    Connect,
    RunQuery,
    AddToPath,
    ConnectionsList,
    ConnectionsAdd,
    ConnectionsEdit,
    ConnectionsRemove,
    ConnectionsTest,
    Help,
    Version,
    Invalid
}

public sealed class Args
{
    public CliCommand Command { get; init; }
    public string? ConnectionName { get; init; }
    /// <summary>SQL passed with -q.</summary>
    public string? Query { get; init; }
    /// <summary>Script file passed with -i (run and print, no UI).</summary>
    public string? InputFile { get; init; }
    /// <summary>A .sql file to open in the editor.</summary>
    public string? OpenFile { get; init; }
    /// <summary>Database override (-d).</summary>
    public string? Database { get; init; }
    public ExportFormat Format { get; init; } = ExportFormat.Table;
    public string? Error { get; init; }

    public const string Usage =
        """
        sqlr — a fast, keyboard-driven SQL Server client for the terminal

        USAGE
          sqlr                                   pick a saved connection
          sqlr -c <name> [-d <db>] [file.sql]    open the editor (optionally with a file)
          sqlr -c <name> -q "<sql>" [-f fmt]     run a query, print results, exit
          sqlr -c <name> -i script.sql [-f fmt]  run a script file, print results, exit
          echo "select 1" | sqlr -c <name>       run SQL from stdin

          sqlr connections                       list saved connections
          sqlr connections add                   add one (interactive)
          sqlr connections edit <name>           edit one (interactive)
          sqlr connections remove <name>         remove one
          sqlr connections test <name>           test connectivity

          sqlr --add-to-path                     add the install folder to your PATH
          sqlr --help | --version

        OPTIONS
          -c, --connect <name>    saved connection to use
          -d, --database <db>     override the connection's database
          -q, --query <sql>       SQL to run non-interactively
          -i, --input <file>      script file to run non-interactively (GO separators supported)
          -f, --format <fmt>      output for -q/-i: table (default), csv, tsv, json, md, sql
        """;

    public static Args Parse(string[] argv) => Parse(argv, Console.IsInputRedirected);

    /// <param name="stdinRedirected">When true, "-c name" with no query reads SQL from stdin instead of opening the UI.</param>
    public static Args Parse(string[] argv, bool stdinRedirected)
    {
        if (argv.Length == 0)
            return new Args { Command = CliCommand.LaunchPicker };

        var first = argv[0].ToLowerInvariant();

        switch (first)
        {
            case "-h" or "--help" or "help" or "/?":
                return new Args { Command = CliCommand.Help };
            case "-v" or "--version" or "version":
                return new Args { Command = CliCommand.Version };
            case "--add-to-path":
                return new Args { Command = CliCommand.AddToPath };
            case "connections" or "connection" or "conn":
                return ParseConnections(argv);
        }

        string? name = null, query = null, input = null, db = null, open = null;
        var format = ExportFormat.Table;

        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            string? Next() => i + 1 < argv.Length ? argv[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "-c" or "--connect" or "--connection":
                    name = Next();
                    if (name is null) return Invalid($"{a} needs a connection name.");
                    break;
                case "-q" or "--query":
                    query = Next();
                    if (query is null) return Invalid($"{a} needs the SQL to run.");
                    break;
                case "-i" or "--input":
                    input = Next();
                    if (input is null) return Invalid($"{a} needs a file path.");
                    break;
                case "-d" or "--database":
                    db = Next();
                    if (db is null) return Invalid($"{a} needs a database name.");
                    break;
                case "-f" or "--format":
                    var f = Next();
                    if (ResultExport.FormatFromName(f) is not { } fmt) return Invalid($"Unknown format '{f}'. Use table, csv, tsv, json, md or sql.");
                    format = fmt;
                    break;
                default:
                    if (a.StartsWith('-')) return Invalid($"Unknown option '{a}'.");
                    if (open is not null) return Invalid($"Unexpected argument '{a}'.");
                    open = a;
                    break;
            }
        }

        if (query is not null && input is not null)
            return Invalid("Use either -q or -i, not both.");

        if ((query is not null || input is not null) && name is null)
            return Invalid("-q/-i need a connection: sqlr -c <name> -q \"...\"");

        if (name is null)
            return open is null
                ? new Args { Command = CliCommand.LaunchPicker }
                : Invalid("Opening a file needs a connection: sqlr -c <name> file.sql");

        bool runNonInteractive = query is not null || input is not null || (stdinRedirected && open is null);

        return new Args
        {
            Command        = runNonInteractive ? CliCommand.RunQuery : CliCommand.Connect,
            ConnectionName = name,
            Query          = query,
            InputFile      = input,
            OpenFile       = open,
            Database       = db,
            Format         = format
        };
    }

    private static Args ParseConnections(string[] argv)
    {
        if (argv.Length == 1 || argv[1].Equals("list", StringComparison.OrdinalIgnoreCase) || argv[1].Equals("ls", StringComparison.OrdinalIgnoreCase))
            return new Args { Command = CliCommand.ConnectionsList };

        var sub  = argv[1].ToLowerInvariant();
        var name = argv.Length > 2 ? argv[2] : null;

        return sub switch
        {
            "add" or "new"              => new Args { Command = CliCommand.ConnectionsAdd },
            "edit"                      => name is null ? Invalid("Usage: sqlr connections edit <name>")   : new Args { Command = CliCommand.ConnectionsEdit,   ConnectionName = name },
            "remove" or "rm" or "delete" => name is null ? Invalid("Usage: sqlr connections remove <name>") : new Args { Command = CliCommand.ConnectionsRemove, ConnectionName = name },
            "test"                      => name is null ? Invalid("Usage: sqlr connections test <name>")   : new Args { Command = CliCommand.ConnectionsTest,   ConnectionName = name },
            _                           => Invalid($"Unknown connections command '{argv[1]}'. Use list, add, edit, remove or test.")
        };
    }

    private static Args Invalid(string message) => new() { Command = CliCommand.Invalid, Error = message };
}
