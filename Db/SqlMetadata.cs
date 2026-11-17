using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

public record TableInfo(string Schema, string Name, bool IsView, bool IsSystem = false)
{
    public string FullName       => $"{Schema}.{Name}";
    public string ShortName      => Name;
    public string QuotedFullName => $"{SqlIdent.Quote(Schema)}.{SqlIdent.Quote(Name)}";
}

public record ColumnInfo(string Schema, string Table, string Column, string DataType, bool Nullable = true);

public record RoutineInfo(string Schema, string Name, bool IsProcedure = true)
{
    public string FullName       => $"{Schema}.{Name}";
    public string QuotedFullName => $"{SqlIdent.Quote(Schema)}.{SqlIdent.Quote(Name)}";
}

public sealed class DatabaseSchema
{
    public string DatabaseName { get; set; } = "";
    public List<string>      Schemas   { get; } = [];
    public List<string>      Databases { get; } = [];
    public List<TableInfo>   Tables    { get; } = [];
    public List<ColumnInfo>  Columns   { get; } = [];
    public List<RoutineInfo> Routines  { get; } = [];
    /// <summary>Why loading failed, when it did (completion still works with whatever loaded).</summary>
    public string? LoadError { get; set; }

    public IEnumerable<TableInfo> UserTables => Tables.Where(t => !t.IsSystem);

    // Lookup helpers — built on first use; the schema is immutable once loaded.
    private Dictionary<string, List<ColumnInfo>>? _colsByQualified;
    private Dictionary<string, List<ColumnInfo>>? _colsByTable;

    /// <summary>Columns of every table/view named <paramref name="tableName"/>, in any schema.</summary>
    public IEnumerable<ColumnInfo> ColumnsForTable(string tableName) =>
        (_colsByTable ??= Index(c => c.Table)).GetValueOrDefault(tableName, []);

    /// <summary>Columns of <paramref name="schema"/>.<paramref name="tableName"/>, or of every schema's table when schema is null.</summary>
    public IEnumerable<ColumnInfo> ColumnsForTable(string? schema, string tableName) =>
        string.IsNullOrEmpty(schema)
            ? ColumnsForTable(tableName)
            : (_colsByQualified ??= Index(c => $"{c.Schema}.{c.Table}")).GetValueOrDefault($"{schema}.{tableName}", []);

    public IEnumerable<ColumnInfo> ColumnsFor(TableInfo t) => ColumnsForTable(t.Schema, t.Name);

    public IEnumerable<TableInfo> FindTables(string? schema, string name) =>
        Tables.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                          && (string.IsNullOrEmpty(schema) || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));

    private Dictionary<string, List<ColumnInfo>> Index(Func<ColumnInfo, string> key)
    {
        var d = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Columns)
        {
            var k = key(c);
            if (!d.TryGetValue(k, out var list))
                d[k] = list = [];
            list.Add(c);
        }
        return d;
    }

    private const string TypeExpr = """
        CASE
            WHEN t.name IN ('varchar','char','varbinary','binary')
                THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
            WHEN t.name IN ('nvarchar','nchar')
                THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
            WHEN t.name IN ('decimal','numeric')
                THEN t.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
            WHEN t.name IN ('datetime2','time','datetimeoffset') AND c.scale <> 7
                THEN t.name + '(' + CAST(c.scale AS varchar(10)) + ')'
            ELSE t.name
        END
        """;

    /// <summary>
    /// Loads schema metadata for the connection's database. Best effort: each query is independent,
    /// and failures are recorded in <see cref="LoadError"/> instead of thrown.
    /// </summary>
    public static async Task<DatabaseSchema> LoadAsync(string connectionString, CancellationToken ct = default)
    {
        var schema = new DatabaseSchema();

        try
        {
            await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
            schema.DatabaseName = conn.Database;

            await RunQueryAsync(schema, conn, "SELECT name FROM sys.databases ORDER BY name",
                r => schema.Databases.Add(r.GetString(0)), ct);

            await RunQueryAsync(schema, conn, "SELECT name FROM sys.schemas ORDER BY name",
                r => schema.Schemas.Add(r.GetString(0)), ct);

            // User tables/views, plus the system catalog views (sys.*, INFORMATION_SCHEMA.*)
            // so "sys." completes too.
            await RunQueryAsync(schema, conn,
                """
                SELECT s.name, o.name, CASE WHEN o.type = 'V' THEN 1 ELSE 0 END, CAST(o.is_ms_shipped AS int)
                FROM sys.all_objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type IN ('U','V')
                  AND (o.is_ms_shipped = 0 OR s.name IN ('sys','INFORMATION_SCHEMA'))
                ORDER BY s.name, o.name
                """,
                r => schema.Tables.Add(new TableInfo(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3) == 1)), ct);

            await RunQueryAsync(schema, conn,
                $"""
                SELECT s.name, o.name, c.name, {TypeExpr}, CAST(c.is_nullable AS int)
                FROM sys.all_columns c
                JOIN sys.all_objects o ON o.object_id = c.object_id
                JOIN sys.schemas s     ON s.schema_id = o.schema_id
                JOIN sys.types t       ON t.user_type_id = c.user_type_id
                WHERE o.type IN ('U','V')
                  AND (o.is_ms_shipped = 0 OR s.name IN ('sys','INFORMATION_SCHEMA'))
                ORDER BY s.name, o.name, c.column_id
                """,
                r => schema.Columns.Add(new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1)), ct);

            await RunQueryAsync(schema, conn,
                """
                SELECT s.name, o.name, CASE WHEN o.type IN ('P','PC','X') THEN 1 ELSE 0 END
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT')
                ORDER BY s.name, o.name
                """,
                r => schema.Routines.Add(new RoutineInfo(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1)), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            schema.LoadError ??= ex.Message;
        }

        return schema;
    }

    private static async Task RunQueryAsync(DatabaseSchema schema, SqlConnection conn, string sql, Action<SqlDataReader> onRow, CancellationToken ct)
    {
        try
        {
            await using var cmd    = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                onRow(reader);
        }
        catch (SqlException ex)
        {
            // e.g. missing VIEW DEFINITION permission on one catalog — keep going with the rest
            schema.LoadError ??= ex.Message;
        }
    }
}

/// <summary>
/// The schema for the session's current database, loaded in the background and swapped in
/// atomically. Reloads when the session switches database (USE) or after DDL.
/// </summary>
public sealed class SchemaService
{
    private readonly Func<string, string> _connectionStringFor;
    private CancellationTokenSource? _loadCts;
    private int _generation;

    public SchemaService(Func<string, string> connectionStringForDatabase, DatabaseSchema? initial = null)
    {
        _connectionStringFor = connectionStringForDatabase;
        Current = initial ?? new DatabaseSchema();
        IsLoaded = initial is not null;
    }

    public DatabaseSchema Current { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsLoaded  { get; private set; }

    /// <summary>Raised (on a background thread) after a load completes.</summary>
    public event Action<DatabaseSchema>? Loaded;

    public void Reload(string database)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        int gen = Interlocked.Increment(ref _generation);
        IsLoading = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var s = await DatabaseSchema.LoadAsync(_connectionStringFor(database), cts.Token);
                if (gen != _generation) return; // a newer load superseded this one
                Current   = s;
                IsLoaded  = true;
                IsLoading = false;
                Loaded?.Invoke(s);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (gen != _generation) return;
                IsLoading = false;
                Current   = new DatabaseSchema { DatabaseName = database, LoadError = ex.Message };
                Loaded?.Invoke(Current);
            }
        });
    }
}

/// <summary>Identifier quoting helpers.</summary>
public static class SqlIdent
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD","ALL","ALTER","AND","ANY","AS","ASC","BACKUP","BEGIN","BETWEEN","BREAK","BY","CASCADE","CASE","CHECK",
        "COLUMN","COMMIT","CONSTRAINT","CONTAINS","CREATE","CROSS","CURRENT","CURSOR","DATABASE","DEFAULT","DELETE",
        "DESC","DISTINCT","DROP","ELSE","END","EXCEPT","EXEC","EXECUTE","EXISTS","FETCH","FILE","FOR","FOREIGN",
        "FROM","FULL","FUNCTION","GRANT","GROUP","HAVING","IDENTITY","IF","IN","INDEX","INNER","INSERT","INTERSECT",
        "INTO","IS","JOIN","KEY","LEFT","LIKE","MERGE","NOT","NULL","OF","OFF","ON","OPEN","OPTION","OR","ORDER",
        "OUTER","OVER","PERCENT","PIVOT","PLAN","PRIMARY","PRINT","PROC","PROCEDURE","PUBLIC","REFERENCES","RETURN",
        "REVOKE","RIGHT","ROLLBACK","ROWCOUNT","RULE","SCHEMA","SELECT","SET","TABLE","THEN","TO","TOP","TRAN",
        "TRANSACTION","TRIGGER","TRUNCATE","UNION","UNIQUE","UPDATE","USE","USER","VALUES","VIEW","WHEN","WHERE",
        "WHILE","WITH"
    };

    /// <summary>Wraps a name in [brackets] only when it needs them (spaces, symbols, keywords…).</summary>
    public static string Quote(string name)
    {
        if (name.Length == 0) return "[]";
        bool plain = (char.IsLetter(name[0]) || name[0] is '_' or '#' or '@')
                     && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$')
                     && !Reserved.Contains(name);
        return plain ? name : "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>Always-bracketed form (for generated SQL).</summary>
    public static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Strips [..] / ".." quoting from one identifier part.</summary>
    public static string Unquote(string part)
    {
        part = part.Trim();
        if (part.Length >= 2 && part[0] == '[' && part[^1] == ']') return part[1..^1].Replace("]]", "]");
        if (part.Length >= 2 && part[0] == '"' && part[^1] == '"') return part[1..^1].Replace("\"\"", "\"");
        return part;
    }

    /// <summary>
    /// Splits a multi-part name like <c>db.[my schema].tbl</c> or <c>db..tbl</c> into its parts
    /// (unquoted). Empty parts are kept (db..tbl → ["db", "", "tbl"]).
    /// </summary>
    public static List<string> SplitParts(string multipart)
    {
        var parts = new List<string>();
        var cur   = new System.Text.StringBuilder();
        for (int i = 0; i < multipart.Length; i++)
        {
            char c = multipart[i];
            if (c == '[' || c == '"')
            {
                char close = c == '[' ? ']' : '"';
                cur.Append(c);
                i++;
                while (i < multipart.Length)
                {
                    cur.Append(multipart[i]);
                    if (multipart[i] == close)
                    {
                        if (i + 1 < multipart.Length && multipart[i + 1] == close) { cur.Append(close); i += 2; continue; }
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '.') { parts.Add(Unquote(cur.ToString())); cur.Clear(); continue; }
            cur.Append(c);
        }
        parts.Add(Unquote(cur.ToString()));
        return parts;
    }
}
