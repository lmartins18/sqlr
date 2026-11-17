using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

public sealed class ObjectDescription
{
    public required string Schema   { get; init; }
    public required string Name     { get; init; }
    /// <summary>Table, View, Procedure, Function…</summary>
    public required string Kind     { get; init; }
    public required ResultSet Grid  { get; init; }
    public List<string> Details     { get; } = [];
    public string? Definition       { get; set; }
    /// <summary>Other schemas that have an object with the same name.</summary>
    public List<string> AlsoIn      { get; } = [];

    public string FullName => $"{Schema}.{Name}";
}

/// <summary>Looks up the structure of a table, view or routine (F12 peek).</summary>
public static class ObjectDescriber
{
    private static string TypeExpr(string alias) => $"""
        CASE
            WHEN t.name IN ('varchar','char','varbinary','binary')
                THEN t.name + '(' + CASE WHEN {alias}.max_length = -1 THEN 'max' ELSE CAST({alias}.max_length AS varchar(10)) END + ')'
            WHEN t.name IN ('nvarchar','nchar')
                THEN t.name + '(' + CASE WHEN {alias}.max_length = -1 THEN 'max' ELSE CAST({alias}.max_length / 2 AS varchar(10)) END + ')'
            WHEN t.name IN ('decimal','numeric')
                THEN t.name + '(' + CAST({alias}.precision AS varchar(10)) + ',' + CAST({alias}.scale AS varchar(10)) + ')'
            WHEN t.name IN ('datetime2','time','datetimeoffset') AND {alias}.scale <> 7
                THEN t.name + '(' + CAST({alias}.scale AS varchar(10)) + ')'
            ELSE t.name
        END
        """;

    /// <returns>The description, or null when no such object exists.</returns>
    public static async Task<ObjectDescription?> DescribeAsync(string connectionString, string? schema, string name, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // ── Resolve the object (prefer the user's default schema, then dbo) ──
        var matches = new List<(int Id, string Schema, string Name, string Type)>();
        await using (var cmd = new SqlCommand(
            """
            SELECT o.object_id, s.name, o.name, RTRIM(o.type)
            FROM sys.all_objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.name = @name
              AND (@schema IS NULL OR s.name = @schema)
              AND o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT','TR')
            ORDER BY CASE WHEN s.name = SCHEMA_NAME() THEN 0 WHEN s.name = 'dbo' THEN 1 ELSE 2 END, o.is_ms_shipped, s.name
            """, conn))
        {
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@schema", (object?)NullIfEmpty(schema) ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                matches.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        if (matches.Count == 0) return null;

        var (id, objSchema, objName, type) = matches[0];
        var desc = new ObjectDescription
        {
            Schema = objSchema,
            Name   = objName,
            Kind   = type switch
            {
                "U"                    => "Table",
                "V"                    => "View",
                "P" or "PC" or "X"     => "Procedure",
                "IF" or "TF" or "FT"   => "Table function",
                "TR"                   => "Trigger",
                _                      => "Function"
            },
            Grid = type is "U" or "V"
                ? await ColumnsAsync(conn, id, ct)
                : await ParametersAsync(conn, id, ct)
        };
        desc.AlsoIn.AddRange(matches.Skip(1).Select(m => m.Schema).Distinct());

        if (type == "U")
        {
            await using (var cmd = new SqlCommand(
                "SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = @id AND p.index_id IN (0,1)", conn))
            {
                cmd.Parameters.AddWithValue("@id", id);
                if (await cmd.ExecuteScalarAsync(ct) is long rows)
                    desc.Details.Add($"≈ {rows:N0} row{(rows == 1 ? "" : "s")}");
            }
            desc.Details.AddRange(await IndexesAsync(conn, id, ct));
        }

        if (type is "IF" or "TF" or "FT")
        {
            var cols = await ColumnsAsync(conn, id, ct);
            if (cols.Rows.Count > 0)
                desc.Details.Add("Returns: " + string.Join(", ", cols.Rows.Select(r => $"{r[0]} {r[1]}")));
        }

        if (type is not "U")
        {
            await using var cmd = new SqlCommand("SELECT OBJECT_DEFINITION(@id)", conn);
            cmd.Parameters.AddWithValue("@id", id);
            desc.Definition = await cmd.ExecuteScalarAsync(ct) as string;
        }

        return desc;
    }

    private static async Task<ResultSet> ColumnsAsync(SqlConnection conn, int id, CancellationToken ct)
    {
        var rs = new ResultSet
        {
            Columns     = ["Column", "Type", "Null", "Key", "Default", "References"],
            ColumnTypes = ["sysname", "sysname", "varchar", "varchar", "nvarchar", "nvarchar"]
        };

        await using var cmd = new SqlCommand(
            $"""
            SELECT c.name, {TypeExpr("c")}, c.is_nullable, c.is_identity, c.is_computed, dc.definition,
                   CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END, fk.ref
            FROM sys.all_columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            OUTER APPLY (
                SELECT TOP (1) rs.name + '.' + ro.name + '(' + rc.name + ')' AS ref
                FROM sys.foreign_key_columns fkc
                JOIN sys.objects ro ON ro.object_id = fkc.referenced_object_id
                JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
                JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
            ) fk
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var keys = new List<string>();
            if (r.GetInt32(6) == 1) keys.Add("PK");
            if (!r.IsDBNull(7))     keys.Add("FK");
            if (r.GetBoolean(3))    keys.Add("IDENTITY");
            if (r.GetBoolean(4))    keys.Add("COMPUTED");

            rs.Rows.Add(
            [
                r.GetString(0),
                r.GetString(1),
                r.GetBoolean(2) ? "YES" : "NO",
                string.Join(" ", keys),
                r.IsDBNull(5) ? "" : r.GetString(5),
                r.IsDBNull(7) ? "" : r.GetString(7)
            ]);
        }
        return rs;
    }

    private static async Task<ResultSet> ParametersAsync(SqlConnection conn, int id, CancellationToken ct)
    {
        var rs = new ResultSet
        {
            Columns     = ["Parameter", "Type", "Direction", "Default"],
            ColumnTypes = ["sysname", "sysname", "varchar", "varchar"]
        };

        await using var cmd = new SqlCommand(
            $"""
            SELECT p.parameter_id, p.name, {TypeExpr("p")}, p.is_output, p.has_default_value
            FROM sys.all_parameters p
            JOIN sys.types t ON t.user_type_id = p.user_type_id
            WHERE p.object_id = @id
            ORDER BY p.parameter_id
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            bool isReturn = r.GetInt32(0) == 0;
            rs.Rows.Add(
            [
                isReturn ? "(returns)" : r.GetString(1),
                r.GetString(2),
                isReturn ? "RETURN" : r.GetBoolean(3) ? "OUTPUT" : "IN",
                r.GetBoolean(4) ? "yes" : ""
            ]);
        }
        return rs;
    }

    private static async Task<List<string>> IndexesAsync(SqlConnection conn, int id, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var cmd = new SqlCommand(
            """
            SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
                   STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal
                          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
                   STUFF((SELECT ', ' + c.name
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                          ORDER BY c.name
                          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')
            FROM sys.indexes i
            WHERE i.object_id = @id AND i.type > 0
            ORDER BY i.is_primary_key DESC, i.name
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var kind = r.GetBoolean(3) ? "PK" : r.GetBoolean(2) ? "UNIQUE" : "INDEX";
            var cols = r.IsDBNull(4) ? "" : r.GetString(4);
            var incl = r.IsDBNull(5) ? "" : $"  INCLUDE ({r.GetString(5)})";
            lines.Add($"{kind,-6} {r.GetString(0)}  {r.GetString(1).ToLowerInvariant()}  ({cols}){incl}");
        }
        return lines;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
