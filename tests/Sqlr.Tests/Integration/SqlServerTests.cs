using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Sqlr.Config;
using Sqlr.Db;
using Xunit;

namespace Sqlr.Tests.Integration;

/// <summary>Runs only when SQLR_TEST_SQL holds a server-level connection string (the fixture creates its own databases).</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrEmpty(SqlServerFixture.ServerConnectionString))
            Skip = "Set SQLR_TEST_SQL to a SQL Server connection string to run integration tests "
                 + "(e.g. docker run mcr.microsoft.com/mssql/server:2022-latest).";
    }
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("SQLR_TEST_SQL");

    public string TestDb => ForDatabase("SqlrTest");

    public static string ForDatabase(string db) =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = db }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(ServerConnectionString)) return;

        using var runner = new SqlRunner(ForDatabase("master"));
        Assert.Null(await runner.ConnectAsync());
        var setup  = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "setup.sql"));
        var result = await runner.ExecuteAsync(setup);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(m => m.Text)));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("SqlServer")]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

[Collection("SqlServer")]
public class SqlServerTests(SqlServerFixture fx)
{
    private async Task<SqlRunner> Connect(int maxRows = 100_000)
    {
        var r = new SqlRunner(fx.TestDb) { MaxRows = maxRows };
        Assert.Null(await r.ConnectAsync());
        return r;
    }

    [SqlFact]
    public async Task Multiple_result_sets_and_print_messages_are_all_captured()
    {
        using var r   = await Connect();
        var result    = await r.ExecuteAsync("SELECT 1 AS a; PRINT 'hello'; SELECT 2 AS b, 3 AS b");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(["b", "b_2"], result.ResultSets[1].Columns);      // duplicate names made unique
        Assert.Contains(result.Messages, m => m.Kind == MessageKind.Info && m.Text == "hello");
    }

    [SqlFact]
    public async Task GO_batches_run_separately_and_error_lines_map_to_the_editor()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("SELECT 1 AS x\nGO\nSELECT 2 AS y\nSELECT nope FROM sales.Customers\nGO\nSELECT 3 AS z");

        Assert.True(result.HasErrors);
        Assert.Equal(4, result.ErrorLine);
        Assert.Equal(["x", "z"], result.ResultSets.Select(s => s.Columns[0]).ToList()); // batch 2 failed to compile
        Assert.Contains(result.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains("Line 4") && m.Text.Contains("Invalid column name 'nope'"));
    }

    [SqlFact]
    public async Task Line_offset_is_applied_for_selections()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("SELECT\n nope FROM sales.Customers", lineOffset: 9);
        Assert.Equal(11, result.ErrorLine);
    }

    [SqlFact]
    public async Task Runtime_errors_do_not_hide_other_results()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("SELECT 1/0 AS x; SELECT 4 AS y");
        Assert.True(result.HasErrors);
        Assert.Contains(result.ResultSets, s => s.Columns[0] == "y" && s.Rows[0][0] == "4");
    }

    [SqlFact]
    public async Task Cancellation_stops_the_query_and_the_session_stays_usable()
    {
        using var r  = await Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw       = Stopwatch.StartNew();
        var result   = await r.ExecuteAsync("SELECT 1 AS before; WAITFOR DELAY '00:00:30'; SELECT 2", 0, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        var next = await r.ExecuteAsync("SELECT 42 AS answer");
        Assert.False(next.HasErrors);
        Assert.Equal("42", next.ResultSets[0].Rows[0][0]);
    }

    [SqlFact]
    public async Task Huge_decimals_do_not_throw_away_the_run()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("SELECT CAST('1000000000000000000000000000000' AS decimal(38,0)) AS big, CAST(-12345678901234567890123456789.123456789 AS decimal(38,9)) AS neg; SELECT 2 AS after");
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(m => m.Text)));
        Assert.Equal("1000000000000000000000000000000", result.ResultSets[0].Rows[0][0]);
        Assert.Equal("-12345678901234567890123456789.123456789", result.ResultSets[0].Rows[0][1]);
        Assert.Equal("2", result.ResultSets[1].Rows[0][0]);
    }

    [SqlFact]
    public async Task Cancel_keeps_rows_already_fetched()
    {
        using var r   = await Connect(maxRows: 5_000);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var result    = await r.ExecuteAsync("SELECT a.Id, b.x FROM dbo.BigTable a CROSS JOIN (SELECT TOP 1000 1 AS x FROM sys.all_objects) b", 0, cts.Token);

        Assert.True(result.Cancelled);
        var rs = Assert.Single(result.ResultSets);
        Assert.Equal(5_000, rs.Rows.Count);
    }

    [SqlFact]
    public async Task Reconnect_never_falls_back_to_another_database()
    {
        var cs = new SqlConnectionStringBuilder(fx.TestDb) { ConnectRetryCount = 0 }.ConnectionString;
        using var r = new SqlRunner(cs);
        Assert.Null(await r.ConnectAsync());
        await r.ExecuteAsync("USE SqlrOther");

        using var admin = new SqlRunner(fx.TestDb);
        Assert.Null(await admin.ConnectAsync());
        try
        {
            // Taking the database offline also kills our session.
            var off = await admin.ExecuteAsync("ALTER DATABASE SqlrOther SET OFFLINE WITH ROLLBACK IMMEDIATE");
            Assert.False(off.HasErrors, string.Join("\n", off.Messages.Select(m => m.Text)));

            await r.ExecuteAsync("SELECT 1");                      // notices the dead session
            var again = await r.ExecuteAsync("SELECT DB_NAME() AS db");
            Assert.True(again.HasErrors);
            Assert.Empty(again.ResultSets);                        // nothing ran in SqlrTest by mistake
            Assert.Contains(again.Messages, m => m.Text.Contains("Could not reconnect to database 'SqlrOther'"));
        }
        finally
        {
            await admin.ExecuteAsync("ALTER DATABASE SqlrOther SET ONLINE");
            // SET ONLINE returns before recovery finishes; wait until the database really opens.
            for (int i = 0; i < 100; i++)
            {
                var (ok, _, _) = await SqlRunner.TestAsync(SqlServerFixture.ForDatabase("SqlrOther"));
                if (ok) break;
                await Task.Delay(100);
            }
        }
    }

    [SqlFact]
    public async Task Row_cap_keeps_later_statements_running()
    {
        using var r = await Connect(maxRows: 1_000);
        var result  = await r.ExecuteAsync("SELECT * FROM dbo.BigTable ORDER BY Id; SELECT COUNT(*) AS n FROM dbo.BigTable");

        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(1_000, result.ResultSets[0].Rows.Count);
        Assert.Equal(249_000, result.ResultSets[0].SkippedRows);
        Assert.Equal("250000", result.ResultSets[1].Rows[0][0]);
    }

    [SqlFact]
    public async Task USE_changes_the_tracked_database()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("USE SqlrOther");
        Assert.Equal("SqlrOther", result.Database);
        Assert.Equal("SqlrOther", r.CurrentDatabase);
        Assert.Contains(result.Messages, m => m.Text.Contains("Changed database context"));
    }

    [SqlFact]
    public async Task Values_are_formatted_like_ssms()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync(
            """
            SELECT o.Total, o.Payload, o.OrderDate, c.IsActive, c.Balance, c.ExternalId, o.Notes
            FROM sales.Orders o JOIN sales.Customers c ON c.CustomerId = o.CustomerId
            WHERE o.OrderId = 1;
            SELECT Place, Dur, Doc, Price FROM dbo.[Order Details];
            SELECT ArchivedAt FROM archive.Orders;
            """);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(m => m.Text)));

        var row = result.ResultSets[0].Rows[0];
        Assert.Equal("99.9500", row[0]);
        Assert.Equal("0xDEADBEEF", row[1]);
        Assert.Equal("2026-01-02", row[2]);
        Assert.Equal("1", row[3]);
        Assert.Equal("1234.56", row[4]);
        Assert.Equal(row[5], row[5]!.ToUpperInvariant());
        Assert.Contains("\n", row[6]);
        Assert.True(result.ResultSets[0].IsNumeric(0));

        var od = result.ResultSets[1].Rows[0];
        Assert.StartsWith("0xE6100000", od[0]);                    // geography: raw bytes, not a crash
        Assert.Equal("geography", result.ResultSets[1].ColumnTypes[0]);
        Assert.Equal("12:34:56.7890000", od[1]);
        Assert.Equal("<a><b>1</b></a>", od[2]);
        Assert.Equal("3.14159", od[3]);

        Assert.Equal("2025-12-31 23:59:59.0000000 +01:00", result.ResultSets[2].Rows[0][0]);
    }

    [SqlFact]
    public async Task Rows_affected_are_reported()
    {
        using var r = await Connect();
        var result  = await r.ExecuteAsync("BEGIN TRAN; UPDATE sales.Customers SET Balance = Balance WHERE CustomerId < 3; ROLLBACK;");
        Assert.Equal(2, result.RowsAffected);
        Assert.Contains(result.Messages, m => m.Kind == MessageKind.RowsAffected && m.Text == "(2 rows affected)");
    }

    [SqlFact]
    public async Task Killed_session_recovers_transparently_with_default_settings()
    {
        // SqlClient's idle-connection resiliency reconnects and restores the session (incl. database).
        using var r = await Connect();
        await r.ExecuteAsync("USE SqlrOther");
        await Kill(r);

        var after = await r.ExecuteAsync("SELECT DB_NAME() AS db");
        Assert.False(after.HasErrors, string.Join("\n", after.Messages.Select(m => m.Text)));
        Assert.Equal("SqlrOther", after.ResultSets[0].Rows[0][0]);
    }

    [SqlFact]
    public async Task Lost_connection_is_reopened_by_sqlr_when_the_driver_gives_up()
    {
        var cs = new SqlConnectionStringBuilder(fx.TestDb) { ConnectRetryCount = 0 }.ConnectionString;
        using var r = new SqlRunner(cs);
        Assert.Null(await r.ConnectAsync());
        await r.ExecuteAsync("USE SqlrOther");
        await Kill(r);

        var broken = await r.ExecuteAsync("SELECT 1 AS x");     // the server dropped us: reported, not thrown
        Assert.True(broken.HasErrors);

        var again = await r.ExecuteAsync("SELECT DB_NAME() AS db");
        Assert.False(again.HasErrors, string.Join("\n", again.Messages.Select(m => m.Text)));
        Assert.Contains(again.Messages, m => m.Text.StartsWith("Reconnected"));
        Assert.Equal("SqlrOther", again.ResultSets[0].Rows[0][0]); // reconnect restores the database
    }

    private async Task Kill(SqlRunner victim)
    {
        var spid = (await victim.ExecuteAsync("SELECT @@SPID")).ResultSets[0].Rows[0][0];
        using var killer = new SqlRunner(fx.TestDb);
        Assert.Null(await killer.ConnectAsync());
        var k = await killer.ExecuteAsync($"KILL {spid}");
        Assert.False(k.HasErrors, string.Join("\n", k.Messages.Select(m => m.Text)));
        await Task.Delay(200);
    }

    [SqlFact]
    public async Task Schema_metadata_keeps_schemas_apart()
    {
        var s = await DatabaseSchema.LoadAsync(fx.TestDb);
        Assert.Null(s.LoadError);
        Assert.Equal("SqlrTest", s.DatabaseName);
        Assert.Contains(s.Tables, t => t is { Schema: "sales", Name: "Orders" });
        Assert.Contains(s.Tables, t => t is { Schema: "archive", Name: "Orders" });
        Assert.Contains(s.Tables, t => t is { Schema: "sys", Name: "objects", IsSystem: true });
        Assert.Equal(["ArchivedAt", "ArchivedOrderId", "Reason"],
            s.ColumnsForTable("archive", "Orders").Select(c => c.Column).Order().ToList());
        Assert.Contains(s.ColumnsForTable("sales", "Customers"), c => c is { Column: "Name", DataType: "nvarchar(100)", Nullable: false });
        Assert.Contains(s.Routines, r => r is { Name: "GetCustomer", IsProcedure: true });
        Assert.Contains(s.Routines, r => r is { Name: "AddOne", IsProcedure: false });
    }

    [SqlFact]
    public async Task Describe_table_shows_keys_indexes_and_row_count()
    {
        var d = await ObjectDescriber.DescribeAsync(fx.TestDb, "sales", "Orders");
        Assert.NotNull(d);
        Assert.Equal("Table", d.Kind);
        var cols = d.Grid.Rows.ToDictionary(r => r[0]!);
        Assert.Equal("PK IDENTITY", cols["OrderId"][3]);
        Assert.Equal("FK", cols["CustomerId"][3]);
        Assert.Equal("sales.Customers(CustomerId)", cols["CustomerId"][5]);
        Assert.Equal("varbinary(max)", cols["Payload"][1]);
        Assert.Contains(d.Details, l => l.Contains("3 rows"));
        Assert.Contains(d.Details, l => l.Contains("IX_Orders_Customer") && l.Contains("OrderDate DESC") && l.Contains("INCLUDE (Total)"));
    }

    [SqlFact]
    public async Task Describe_unqualified_name_reports_other_schemas()
    {
        var d = await ObjectDescriber.DescribeAsync(fx.TestDb, null, "Orders");
        Assert.NotNull(d);
        Assert.Single(d.AlsoIn);
    }

    [SqlFact]
    public async Task Describe_procedure_shows_parameters_and_definition()
    {
        var d = await ObjectDescriber.DescribeAsync(fx.TestDb, null, "GetCustomer");
        Assert.NotNull(d);
        Assert.Equal("Procedure", d.Kind);
        Assert.Equal(["@Id", "@Verbose"], d.Grid.Rows.Select(r => r[0]).ToList());
        Assert.Contains("CREATE PROCEDURE sales.GetCustomer", d.Definition);
        Assert.Null(await ObjectDescriber.DescribeAsync(fx.TestDb, null, "NoSuchThing"));
    }

    [SqlFact]
    public async Task Cli_runs_a_query_and_prints_csv()
    {
        var b    = new SqlConnectionStringBuilder(SqlServerFixture.ServerConnectionString);
        var home = Path.Combine(Path.GetTempPath(), "sqlr-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var conn = new SqlrConnection { Name = "it", Server = b.DataSource, Database = "SqlrTest", AuthType = "sql", Username = b.UserID };
            conn.Secret = b.Password;
            await File.WriteAllTextAsync(Path.Combine(home, "connections.json"),
                System.Text.Json.JsonSerializer.Serialize(new { connections = new[] { conn } },
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

            var (code, stdout, stderr) = await RunCli(home, "-c", "it", "-q", "SELECT Name FROM sales.Customers WHERE CustomerId <= 2 ORDER BY CustomerId", "-f", "csv");
            Assert.True(code == 0, stderr);
            Assert.Equal("Name\r\nAda Lovelace\r\nGrace Hopper\r\n", stdout);

            (code, _, stderr) = await RunCli(home, "-c", "it", "-q", "SELECT nope");
            Assert.Equal(1, code);
            Assert.Contains("Invalid column name", stderr);

            (code, _, _) = await RunCli(home, "--bogus");
            Assert.Equal(2, code);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCli(string home, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sqlr.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SQLR_HOME"] = home;

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }
}
