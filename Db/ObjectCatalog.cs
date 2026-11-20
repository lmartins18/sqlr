using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

/// <summary>A table, view, procedure or function in one database (object explorer row).</summary>
public sealed record CatalogObject(string Schema, string Name, string Kind, DateTime Modified)
{
    public string FullName       => $"{Schema}.{Name}";
    public string QuotedFullName => $"{SqlIdent.Quote(Schema)}.{SqlIdent.Quote(Name)}";
}

/// <summary>
/// One grep match: a line of an object's definition (<see cref="Line"/> ≥ 1), a column whose
/// name matches (<see cref="Line"/> = 0, <see cref="Text"/> = "column  type"), or the object's
/// own schema-qualified name (<see cref="Line"/> = -1).
/// </summary>
public sealed record GrepHit(string Schema, string Object, string Kind, int Line, string Text, string? Column = null)
{
    /// <summary>Set by the explorer, which can grep several databases at once.</summary>
    public string Database { get; init; } = "";
    public string FullName => $"{Schema}.{Object}";
    public bool IsColumn   => Line == 0;
    public bool IsName     => Line < 0;
}

public sealed record SearchObject(string Schema, string Name, string Kind);
public sealed record SearchColumn(string Schema, string Table, string Column, string Type);
public sealed record SearchModule(string Schema, string Name, string Kind, string Definition);

/// <summary>A database's searchable text, loaded once and grepped in memory on every keystroke.</summary>
public sealed record SearchIndex(IReadOnlyList<SearchObject> Objects, IReadOnlyList<SearchColumn> Columns, IReadOnlyList<SearchModule> Modules)
{
    /// <summary>
    /// Plain-text search (not regex), smart case: object names first (see <see cref="ObjectCatalog.MatchName"/>),
    /// then column names, then definition lines. At most <paramref name="maxHits"/>.
    /// </summary>
    public List<GrepHit> Grep(string pattern, int maxHits, string? database = null) =>
        Objects.Select(o => ObjectCatalog.MatchName(o.Schema, o.Name, o.Kind, pattern, database)).OfType<GrepHit>()
            .Concat(Columns.Where(c => ObjectCatalog.Contains(c.Column, pattern))
                .Select(c => new GrepHit(c.Schema, c.Table, "column", 0, $"{c.Column}  {c.Type}", c.Column)))
            .Concat(Modules.Where(m => ObjectCatalog.Contains(m.Definition, pattern))
                .SelectMany(m => ObjectCatalog.MatchLines(m.Schema, m.Name, m.Kind, m.Definition, pattern)))
            .Take(maxHits)
            .ToList();
}

/// <summary>Object lists and text search over definitions for the object explorer (F4).</summary>
public static class ObjectCatalog
{
    public const int MaxGrepHits = 2000;

    /// <summary>Kinds in their natural order (used for "sort by type").</summary>
    public static readonly string[] Kinds = ["table", "view", "function", "proc"];

    public static int KindRank(string kind) => Array.IndexOf(Kinds, kind) is var i and >= 0 ? i : Kinds.Length;

    private static string KindOf(string type) => type switch
    {
        "U"                => "table",
        "V"                => "view",
        "P" or "PC" or "X" => "proc",
        "TR"               => "trigger",
        _                  => "function"
    };

    public static async Task<List<CatalogObject>> LoadAsync(string connectionString, CancellationToken ct = default)
    {
        var list = new List<CatalogObject>();
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        await using var cmd = new SqlCommand(
            """
            SELECT s.name, o.name, RTRIM(o.type), o.modify_date
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT') AND o.is_ms_shipped = 0
            """, conn) { CommandTimeout = 30 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CatalogObject(r.GetString(0), r.GetString(1), KindOf(r.GetString(2)), r.GetDateTime(3)));
        return list;
    }

    /// <summary>
    /// Everything grep searches in one database: object names, table/view columns and module
    /// definitions (procs, views, functions, triggers). One round trip; a few MB for a large database.
    /// </summary>
    public static async Task<SearchIndex> LoadSearchIndexAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        await using var cmd = new SqlCommand(
            """
            SELECT s.name, o.name, RTRIM(o.type)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT','TR') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;

            SELECT s.name, o.name, c.name, t.name
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id;

            SELECT s.name, o.name, RTRIM(o.type), m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND m.definition IS NOT NULL -- NULL: encrypted
            ORDER BY s.name, o.name;
            """, conn) { CommandTimeout = 120 };

        var objects = new List<SearchObject>();
        var columns = new List<SearchColumn>();
        var modules = new List<SearchModule>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            objects.Add(new SearchObject(r.GetString(0), r.GetString(1), KindOf(r.GetString(2))));
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            columns.Add(new SearchColumn(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            modules.Add(new SearchModule(r.GetString(0), r.GetString(1), KindOf(r.GetString(2)), r.GetString(3)));
        return new SearchIndex(objects, columns, modules);
    }

    /// <summary>
    /// A name hit: "Nimo" matches the object's name; a dotted pattern ("EDI.Nimo", "CMaster.EDI.Nimo")
    /// matches "database.schema.name". SSMS-style [brackets] in the pattern are ignored.
    /// </summary>
    public static GrepHit? MatchName(string schema, string obj, string kind, string pattern, string? database = null)
    {
        var p = pattern.Replace("[", "").Replace("]", "");
        var text = !p.Contains('.') ? obj : database is null ? $"{schema}.{obj}" : $"{database}.{schema}.{obj}";
        return p.Length > 0 && Contains(text, p) ? new GrepHit(schema, obj, kind, -1, "") : null;
    }

    /// <summary>The definition lines containing <paramref name="pattern"/>, numbered from 1.</summary>
    public static IEnumerable<GrepHit> MatchLines(string schema, string obj, string kind, string definition, string pattern)
    {
        int line = 0;
        foreach (var raw in definition.Split('\n'))
        {
            line++;
            if (Contains(raw, pattern))
                yield return new GrepHit(schema, obj, kind, line, raw.TrimEnd('\r'));
        }
    }

    /// <summary>Smart case: case-insensitive unless the pattern has an upper-case letter.</summary>
    public static bool Contains(string text, string pattern) =>
        text.Contains(pattern, pattern.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
