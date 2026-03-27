using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

public sealed class QueryResult
{
    public DataTable? Data { get; init; }
    public string? Error { get; init; }
    public long ElapsedMs { get; init; }
    public int RowCount { get; init; }
    public bool IsSuccess => Error is null;
}

public sealed class SqlRunner : IDisposable
{
    private SqlConnection? _conn;
    private readonly string _connectionString;

    public SqlRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            _conn = new SqlConnection(_connectionString);
            await _conn.OpenAsync();
            return true;
        }
        catch
        {
            _conn?.Dispose();
            _conn = null;
            return false;
        }
    }

    /// <summary>Stateless test — opens and immediately closes a connection.</summary>
    public static async Task<(bool Success, string Error)> TestAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<QueryResult> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        if (_conn is null)
            return new QueryResult { Error = "Not connected." };

        var sw = Stopwatch.StartNew();
        try
        {
            await using var cmd = new SqlCommand(sql, _conn) { CommandTimeout = 60 };

            // Use a DataAdapter so we handle result sets, not just scalar
            var dt = new DataTable();
            await Task.Run(() =>
            {
                using var adapter = new SqlDataAdapter(cmd);
                adapter.Fill(dt);
            }, ct);

            sw.Stop();

            // Convert to an all-string display table, replacing DBNull with ∅
            var display = new DataTable();
            foreach (DataColumn col in dt.Columns)
                display.Columns.Add(col.ColumnName, typeof(string));

            foreach (DataRow src in dt.Rows)
            {
                var dest = display.NewRow();
                for (int i = 0; i < dt.Columns.Count; i++)
                    dest[i] = src[i] is DBNull || src[i] is null ? "∅" : src[i].ToString() ?? "∅";
                display.Rows.Add(dest);
            }

            return new QueryResult
            {
                Data = display,
                ElapsedMs = sw.ElapsedMilliseconds,
                RowCount = display.Rows.Count
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new QueryResult { Error = "Query cancelled.", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return new QueryResult { Error = FormatSqlError(ex), ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new QueryResult { Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    private static string FormatSqlError(SqlException ex)
    {
        var sb = new StringBuilder();
        foreach (SqlError err in ex.Errors)
            sb.AppendLine($"Msg {err.Number}, Level {err.Class}, State {err.State}: {err.Message}");
        return sb.ToString().TrimEnd();
    }

    public void Dispose() => _conn?.Dispose();
}
