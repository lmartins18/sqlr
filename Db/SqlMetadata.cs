using System.Data;
using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

public record TableInfo(string Schema, string Name, bool IsView)
{
    public string FullName   => $"{Schema}.{Name}";
    public string ShortName  => Name;
}

public record ColumnInfo(string Schema, string Table, string Column, string DataType);
public record RoutineInfo(string Schema, string Name);

public sealed record DatabaseSchema
{
    public string DatabaseName { get; init; } = "";
    public List<string>      Schemas   { get; } = [];
    public List<string>      Databases { get; } = [];
    public List<TableInfo>   Tables    { get; } = [];
    public List<ColumnInfo>  Columns   { get; } = [];
    public List<RoutineInfo> Routines  { get; } = [];

    // Lookup helpers
    private Dictionary<string, List<ColumnInfo>>? _colsByTable;
    private Dictionary<string, List<ColumnInfo>>? _colsBySchema;

    public IEnumerable<ColumnInfo> ColumnsForTable(string tableName) =>
        (_colsByTable ??= BuildColByTable())
            .GetValueOrDefault(tableName.ToUpperInvariant(), []);

    public IEnumerable<ColumnInfo> ColumnsForSchema(string schema) =>
        (_colsBySchema ??= BuildColBySchema())
            .GetValueOrDefault(schema.ToUpperInvariant(), []);

    private Dictionary<string, List<ColumnInfo>> BuildColByTable()
    {
        var d = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Columns)
        {
            if (!d.TryGetValue(c.Table, out var list))
                d[c.Table] = list = [];
            list.Add(c);
        }
        return d;
    }

    private Dictionary<string, List<ColumnInfo>> BuildColBySchema()
    {
        var d = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Columns)
        {
            if (!d.TryGetValue(c.Schema, out var list))
                d[c.Schema] = list = [];
            list.Add(c);
        }
        return d;
    }

    /// <summary>
    /// Loads full schema metadata from the connected SQL Server.
    /// Returns a best-effort result — individual query failures are silently skipped.
    /// </summary>
    public static async Task<DatabaseSchema> LoadAsync(string connectionString)
    {
        var schema = new DatabaseSchema();

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Current database name
            await using (var cmd = new SqlCommand("SELECT DB_NAME()", conn))
                schema = schema with { DatabaseName = (string)(await cmd.ExecuteScalarAsync() ?? "") };

            // Databases
            await RunQueryAsync(conn, "SELECT name FROM sys.databases ORDER BY 1",
                r => schema.Databases.Add(r.GetString(0)));

            // Schemas
            await RunQueryAsync(conn, "SELECT name FROM sys.schemas ORDER BY 1",
                r => schema.Schemas.Add(r.GetString(0)));

            // Tables
            await RunQueryAsync(conn,
                "SELECT table_schema, table_name, table_type FROM INFORMATION_SCHEMA.TABLES ORDER BY 1,2",
                r => schema.Tables.Add(new TableInfo(r.GetString(0), r.GetString(1), r.GetString(2) == "VIEW")));

            // Columns
            await RunQueryAsync(conn,
                """
                SELECT c.table_schema, c.table_name, c.column_name, c.data_type
                FROM INFORMATION_SCHEMA.COLUMNS c
                INNER JOIN INFORMATION_SCHEMA.TABLES t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                ORDER BY 1,2,c.ordinal_position
                """,
                r => schema.Columns.Add(new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3))));

            // Stored procedures + functions
            await RunQueryAsync(conn,
                "SELECT specific_schema, specific_name FROM INFORMATION_SCHEMA.ROUTINES ORDER BY 1,2",
                r => schema.Routines.Add(new RoutineInfo(r.GetString(0), r.GetString(1))));
        }
        catch
        {
            // Schema load is best-effort; return whatever we managed to get
        }

        return schema;
    }

    private static async Task RunQueryAsync(SqlConnection conn, string sql, Action<SqlDataReader> onRow)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                onRow(reader);
        }
        catch { /* skip failed metadata queries */ }
    }
}
