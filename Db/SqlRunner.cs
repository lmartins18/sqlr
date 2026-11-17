using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

public enum MessageKind { Info, RowsAffected, Error }

/// <param name="Line">1-based editor line the message refers to, when known.</param>
public sealed record SqlMessage(MessageKind Kind, string Text, int? Line = null);

/// <summary>One result set, with every value already formatted to text (null = SQL NULL).</summary>
public sealed class ResultSet
{
    public required string[] Columns     { get; init; }
    public required string[] ColumnTypes { get; init; }
    public List<string?[]>   Rows        { get; } = [];
    /// <summary>Rows that came back beyond <see cref="SqlRunner.MaxRows"/> and were not kept.</summary>
    public long SkippedRows { get; set; }
    public bool Truncated => SkippedRows > 0;

    public bool IsNumeric(int col) => ValueFormatter.IsNumericType(ColumnTypes[col]);
}

public sealed class ExecutionResult
{
    public List<ResultSet>  ResultSets { get; } = [];
    public List<SqlMessage> Messages   { get; } = [];
    public bool     Cancelled    { get; set; }
    public TimeSpan Elapsed      { get; set; }
    public long     RowsAffected { get; set; }
    /// <summary>The session's current database after execution (changes with USE).</summary>
    public string?  Database     { get; set; }
    /// <summary>1-based editor line of the first error, when the server reported one.</summary>
    public int?     ErrorLine    { get; set; }

    public int  ErrorCount => Messages.Count(m => m.Kind == MessageKind.Error);
    public bool HasErrors  => ErrorCount > 0;
    public long TotalRows  => ResultSets.Sum(r => (long)r.Rows.Count);
}

/// <summary>
/// Owns the interactive session's connection. Runs scripts batch-by-batch (GO aware), capturing
/// every result set, PRINT/RAISERROR message and rows-affected count, with real cancellation.
/// </summary>
public sealed class SqlRunner : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private SqlConnection? _conn;
    private string? _lastDatabase;
    private long _rowsFetched;

    public SqlRunner(string connectionString)
    {
        // The session holds one long-lived connection, so pooling buys nothing — and a pool that
        // saw a failed open replays that error for up to a minute ("blocking period"), which would
        // make reconnecting fail after the server is back. Every (re)connect is a fresh login.
        _connectionString = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    public int MaxRows { get; init; } = 100_000;
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>Current database of the session (follows USE statements).</summary>
    public string? CurrentDatabase => _conn?.State == System.Data.ConnectionState.Open ? _conn.Database : _lastDatabase;

    public string? ServerVersion { get; private set; }

    /// <summary>Rows received so far by the running query (for live progress).</summary>
    public long RowsFetched => Interlocked.Read(ref _rowsFetched);

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Opens the session. Returns null on success, otherwise the server's error message.</summary>
    public async Task<string?> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _conn?.Dispose();
            _conn = CreateConnection(_connectionString);
            await SqlConnect.OpenAsync(_conn, ct);
            _lastDatabase = _conn.Database;
            ServerVersion = _conn.ServerVersion;
            return null;
        }
        catch (Exception ex)
        {
            _conn?.Dispose();
            _conn = null;
            return ex is SqlException sql ? FormatConnectError(sql) : ex.Message;
        }
    }

    /// <summary>Stateless test — opens and immediately closes a connection.</summary>
    public static async Task<(bool Success, string Error, string? Version)> TestAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
            return (true, "", conn.ServerVersion);
        }
        catch (Exception ex)
        {
            return (false, ex is SqlException sql ? FormatConnectError(sql) : ex.Message, null);
        }
    }

    /// <summary>
    /// Executes a script. <paramref name="lineOffset"/> is the editor line the script starts on,
    /// so server error line numbers can be mapped back to the editor.
    /// </summary>
    public Task<ExecutionResult> ExecuteAsync(string script, int lineOffset = 0, CancellationToken ct = default) =>
        Task.Run(() => Execute(script, lineOffset, ct));

    private ExecutionResult Execute(string script, int lineOffset, CancellationToken ct)
    {
        var result = new ExecutionResult();
        var sw     = Stopwatch.StartNew();

        if (!_gate.Wait(0))
        {
            result.Messages.Add(new SqlMessage(MessageKind.Error, "Another query is still running."));
            return result;
        }

        Interlocked.Exchange(ref _rowsFetched, 0);
        try
        {
            var batches = SqlScript.SplitBatches(script);
            if (batches.Count == 0)
            {
                result.Messages.Add(new SqlMessage(MessageKind.Info, "Nothing to execute."));
                return result;
            }

            if (!EnsureOpen(result))
                return result;

            foreach (var batch in batches)
            {
                for (int rep = 0; rep < batch.RepeatCount && !result.Cancelled; rep++)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                    RunBatch(batch, lineOffset, result, ct);
                    if (_conn?.State != System.Data.ConnectionState.Open) break;
                }
                if (result.Cancelled || _conn?.State != System.Data.ConnectionState.Open) break;
            }

            if (result.Cancelled)
                result.Messages.Add(new SqlMessage(MessageKind.Error, "Query cancelled by user."));
            else if (_conn?.State != System.Data.ConnectionState.Open)
                result.Messages.Add(new SqlMessage(MessageKind.Error, "The connection was closed by the server. It will be reopened on the next run."));
            else if (!result.HasErrors && result.Messages.Count == 0 && result.ResultSets.Count == 0)
                result.Messages.Add(new SqlMessage(MessageKind.Info, "Commands completed successfully."));
        }
        finally
        {
            sw.Stop();
            result.Elapsed  = sw.Elapsed;
            result.Database = CurrentDatabase;
            if (_conn?.State == System.Data.ConnectionState.Open) _lastDatabase = _conn.Database;
            _gate.Release();
        }

        return result;
    }

    private bool EnsureOpen(ExecutionResult result)
    {
        if (_conn?.State == System.Data.ConnectionState.Open) return true;

        try
        {
            _conn?.Dispose();
            // Reopen straight into the database the session was using. Doing it with Initial Catalog
            // (not ChangeDatabase) makes an inaccessible database fail loudly, instead of the error
            // being swallowed as an info message and the script running in the default database.
            var cs = string.IsNullOrEmpty(_lastDatabase)
                ? _connectionString
                : new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = _lastDatabase }.ConnectionString;
            _conn = CreateConnection(cs);
            SqlConnect.Open(_conn);

            if (!string.IsNullOrEmpty(_lastDatabase) && !_lastDatabase.Equals(_conn.Database, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the server put the session in '{_conn.Database}' instead of '{_lastDatabase}'.");

            result.Messages.Add(new SqlMessage(MessageKind.Info,
                "Reconnected. Session state (temp tables, SET options, open transactions) was reset."));
            return true;
        }
        catch (Exception ex)
        {
            _conn?.Dispose();
            _conn = null;
            var verb  = ServerVersion is null ? "connect" : "reconnect";
            var where = string.IsNullOrEmpty(_lastDatabase) ? "" : $" to database '{_lastDatabase}'";
            result.Messages.Add(new SqlMessage(MessageKind.Error,
                $"Could not {verb}{where}: {(ex is SqlException sql ? FormatConnectError(sql) : ex.Message)}\n" +
                "Nothing was run. F10 reconnects from the connection list."));
            return false;
        }
    }

    private static SqlConnection CreateConnection(string connectionString)
    {
        // Errors of severity <= 16 arrive as InfoMessage events instead of exceptions, so a failing
        // statement doesn't throw away the result sets and messages around it (SSMS behaviour).
        var conn = SqlConnect.Create(connectionString);
        conn.FireInfoMessageEventOnUserErrors = true;
        return conn;
    }

    private void RunBatch(SqlBatch batch, int lineOffset, ExecutionResult result, CancellationToken ct)
    {
        var conn     = _conn!;
        var lineBase = lineOffset + batch.StartLine;

        void OnInfo(object? _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                AddServerMessage(result, err, lineBase);
        }

        using var cmd = new SqlCommand(batch.Text, conn) { CommandTimeout = CommandTimeoutSeconds };
        cmd.StatementCompleted += (_, e) =>
        {
            result.RowsAffected += e.RecordCount;
            result.Messages.Add(new SqlMessage(MessageKind.RowsAffected,
                $"({e.RecordCount:N0} row{(e.RecordCount == 1 ? "" : "s")} affected)"));
        };

        conn.InfoMessage += OnInfo;
        using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
        try
        {
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount > 0)
                    ReadResultSet(reader, result);
            }
            while (!ct.IsCancellationRequested && reader.NextResult());

            if (ct.IsCancellationRequested) result.Cancelled = true;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (SqlException ex)
        {
            foreach (SqlError err in ex.Errors)
                AddServerMessage(result, err, lineBase, forceError: true);
        }
        catch (Exception ex)
        {
            // Anything else (driver/conversion errors) is reported, never allowed to discard the run.
            result.Messages.Add(new SqlMessage(MessageKind.Error, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            conn.InfoMessage -= OnInfo;
        }
    }

    /// <summary>
    /// Reads one result set into <paramref name="result"/>. The set is added before its rows are read,
    /// so rows already fetched survive a cancel or an error part-way through.
    /// </summary>
    private void ReadResultSet(SqlDataReader reader, ExecutionResult result)
    {
        int n     = reader.FieldCount;
        var names = new string[n];
        var types = new string[n];
        var udt   = new bool[n];

        for (int i = 0; i < n; i++)
        {
            names[i] = UniqueName(names, i, reader.GetName(i));
            types[i] = reader.GetDataTypeName(i);
            // CLR types we can't load (geography, geometry, hierarchyid without Microsoft.SqlServer.Types)
            // report no field type; read their raw bytes instead of letting GetValue throw per cell.
            udt[i] = reader.GetFieldType(i) is null;
            if (udt[i] && types[i].Contains('.')) types[i] = types[i][(types[i].LastIndexOf('.') + 1)..];
        }

        var rs = new ResultSet { Columns = names, ColumnTypes = types };
        result.ResultSets.Add(rs);

        while (reader.Read())
        {
            Interlocked.Increment(ref _rowsFetched);
            if (rs.Rows.Count >= MaxRows)
            {
                // Keep streaming (and discarding) so later statements in the batch still run.
                rs.SkippedRows++;
                continue;
            }

            var row = new string?[n];
            for (int i = 0; i < n; i++)
            {
                if (reader.IsDBNull(i)) continue;
                row[i] = udt[i]
                    ? ValueFormatter.Hex(reader.GetSqlBytes(i).Value)
                    : ReadValue(reader, i, types[i]);
            }
            rs.Rows.Add(row);
        }
    }

    private static string? ReadValue(SqlDataReader reader, int i, string type)
    {
        try
        {
            return ValueFormatter.Format(reader.GetValue(i), type);
        }
        catch (OverflowException)
        {
            // decimal(38) values beyond System.Decimal's 28-29 digits: SqlDecimal holds them exactly.
            return reader.GetSqlValue(i).ToString();
        }
    }

    private static string UniqueName(string[] names, int idx, string name)
    {
        if (string.IsNullOrEmpty(name)) name = "(No column name)";
        var candidate = name;
        for (int k = 2; names.Take(idx).Contains(candidate, StringComparer.OrdinalIgnoreCase); k++)
            candidate = $"{name}_{k}";
        return candidate;
    }

    private static void AddServerMessage(ExecutionResult result, SqlError err, int lineBase, bool forceError = false)
    {
        bool isError = forceError || err.Class > 10;
        if (!isError)
        {
            result.Messages.Add(new SqlMessage(MessageKind.Info, err.Message));
            return;
        }

        string where;
        int?   editorLine = null;
        if (string.IsNullOrEmpty(err.Procedure))
        {
            editorLine = lineBase + Math.Max(1, err.LineNumber);
            result.ErrorLine ??= editorLine;
            where = $"Line {editorLine}";
        }
        else
        {
            // The line number is inside the procedure's body, not the editor.
            where = $"Procedure {err.Procedure}, Line {err.LineNumber}";
        }
        result.Messages.Add(new SqlMessage(MessageKind.Error,
            $"Msg {err.Number}, Level {err.Class}, State {err.State}, {where}\n{err.Message}", editorLine));
    }

    private static string FormatConnectError(SqlException ex) =>
        string.Join("\n", ex.Errors.Cast<SqlError>().Select(e => e.Message).Distinct());

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
