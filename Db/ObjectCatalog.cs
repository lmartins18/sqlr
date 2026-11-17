using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

/// <summary>A table, view, procedure or function in one database (object explorer row).</summary>
public sealed record CatalogObject(string Schema, string Name, string Kind, DateTime Modified)
{
    public string FullName       => $"{Schema}.{Name}";
    public string QuotedFullName => $"{SqlIdent.Quote(Schema)}.{SqlIdent.Quote(Name)}";
}

/// <summary>
/// One grep match: a line of an object's definition (<see cref="Line"/> ≥ 1), or a column whose
/// name matches (<see cref="Line"/> = 0, <see cref="Text"/> = "column  type").
/// </summary>
public sealed record GrepHit(string Schema, string Object, string Kind, int Line, string Text, string? Column = null)
{
    public string FullName => $"{Schema}.{Object}";
    public bool IsColumn   => Line == 0;
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
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
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
    /// Plain-text search (not regex) over every module definition (procs, views, functions,
    /// triggers) and every table/view column name. Smart case: all-lowercase patterns ignore case.
    /// </summary>
    public static async Task<List<GrepHit>> GrepAsync(string connectionString, string pattern, int maxHits, CancellationToken ct = default)
    {
        var hits = new List<GrepHit>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // The server narrows to the matching objects (CHARINDEX follows the database collation);
        // exact smart-case matching and line splitting happen here.
        await using (var cmd = new SqlCommand(
            """
            SELECT s.name, o.name, c.name, t.name
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t   ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 AND CHARINDEX(@p, c.name) > 0
            ORDER BY s.name, o.name, c.column_id
            """, conn) { CommandTimeout = 60 })
        {
            cmd.Parameters.AddWithValue("@p", pattern);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (Contains(r.GetString(2), pattern))
                    hits.Add(new GrepHit(r.GetString(0), r.GetString(1), "column", 0, $"{r.GetString(2)}  {r.GetString(3)}", r.GetString(2)));
                if (hits.Count >= maxHits) { cmd.Cancel(); return hits; } // don't drain the rest
            }
        }

        await using (var cmd = new SqlCommand(
            """
            SELECT s.name, o.name, RTRIM(o.type), m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND CHARINDEX(@p, m.definition) > 0
            ORDER BY s.name, o.name
            """, conn) { CommandTimeout = 60 })
        {
            cmd.Parameters.AddWithValue("@p", pattern);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(3)) continue; // encrypted module
                hits.AddRange(MatchLines(r.GetString(0), r.GetString(1), KindOf(r.GetString(2)), r.GetString(3), pattern)
                    .Take(maxHits - hits.Count));
                if (hits.Count >= maxHits) { cmd.Cancel(); break; }
            }
        }
        return hits;
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
