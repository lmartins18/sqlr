using Microsoft.Data.SqlClient;
using Sqlr.Cli;
using Sqlr.Config;
using Sqlr.Db;
using Xunit;

namespace Sqlr.Tests;

public class ExportAndFormatTests
{
    private static ResultSet Sample()
    {
        var rs = new ResultSet { Columns = ["id", "name", "active", "amount"], ColumnTypes = ["int", "nvarchar", "bit", "decimal"] };
        rs.Rows.Add(["1", "Ada, \"the\" first\nline2", "1", "12.50"]);
        rs.Rows.Add(["2", null, "0", null]);
        rs.Rows.Add(["3", "O'Brien – ü", "1", "-0.5"]);
        return rs;
    }

    [Fact]
    public void Csv_quotes_only_when_needed_and_null_is_empty()
    {
        var csv = ResultExport.Write(ExportFormat.Csv, Sample());
        Assert.StartsWith("id,name,active,amount\r\n1,\"Ada, \"\"the\"\" first\nline2\",1,12.50\r\n2,,0,\r\n", csv);
    }

    [Fact]
    public void Json_keeps_numbers_bools_and_nulls_typed()
    {
        var json = ResultExport.Write(ExportFormat.Json, Sample());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rows = doc.RootElement;
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.True(rows[0].GetProperty("active").GetBoolean());
        Assert.Equal(12.50m, rows[0].GetProperty("amount").GetDecimal());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[1].GetProperty("name").ValueKind);
        Assert.Contains("O'Brien – ü", json); // not \u-escaped
    }

    [Fact]
    public void Sql_insert_escapes_quotes()
    {
        var sql = ResultExport.Write(ExportFormat.SqlInsert, Sample());
        Assert.Contains("VALUES (3, N'O''Brien – ü', 1, -0.5);", sql);
        Assert.Contains("VALUES (2, NULL, 0, NULL);", sql);
    }

    [Fact]
    public void Truncated_binary_never_exported_as_a_wrong_value()
    {
        Assert.Equal("0xABCD", ResultExport.SqlLiteral("0xABCD", "varbinary"));
        Assert.StartsWith("NULL", ResultExport.SqlLiteral("0xABCD…", "varbinary"));
    }

    [Fact]
    public void Markdown_escapes_pipes_and_newlines()
    {
        var rs = new ResultSet { Columns = ["a"], ColumnTypes = ["nvarchar"] };
        rs.Rows.Add(["x|y\nz"]);
        Assert.Contains("| x\\|y↵z |", ResultExport.Write(ExportFormat.Markdown, rs));
    }

    [Fact]
    public void Text_table_aligns_numbers_right()
    {
        var table = ResultExport.Write(ExportFormat.Table, Sample()).Split('\n');
        Assert.StartsWith("id", table[0]);
        Assert.Contains(" 12.50", table[2]);
        Assert.Contains("NULL", table[3]);
    }

    [Fact]
    public void Value_formatting_is_culture_independent()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.5", ValueFormatter.Format(1234.5m));
            Assert.Equal("0.1", ValueFormatter.Format(0.1d));
            Assert.Equal("2026-01-02 03:04:05.006", ValueFormatter.Format(new DateTime(2026, 1, 2, 3, 4, 5, 6), "datetime"));
            Assert.Equal("2026-01-02", ValueFormatter.Format(new DateTime(2026, 1, 2), "date"));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }

        Assert.Equal("1", ValueFormatter.Format(true));
        Assert.Equal("0xDEADBEEF", ValueFormatter.Format(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.Null(ValueFormatter.Format(DBNull.Value));
        Assert.Equal("12:34:56.7890000", ValueFormatter.Format(new TimeSpan(0, 12, 34, 56, 789)));
        Assert.Equal("a↵b→c", ValueFormatter.ForGrid("a\r\nb\tc"));
    }

    [Fact]
    public void Connection_string_escapes_special_characters_in_password()
    {
        const string pw = "p;a=s's\"w{o}rd";
        var conn = new SqlrConnection { Name = "x", Server = "srv,1433", Database = "", AuthType = "sql", Username = "u" };
        conn.Secret = pw;

        var b = new SqlConnectionStringBuilder(conn.ConnectionString);
        Assert.Equal(pw, b.Password);
        Assert.Equal("u", b.UserID);
        Assert.Equal("srv,1433", b.DataSource);
        Assert.Equal("", b.InitialCatalog);         // blank = login default
        Assert.Equal("sqlr", b.ApplicationName);

        var win = new SqlrConnection { Name = "w", Server = "s", Database = "Db" };
        var wb  = new SqlConnectionStringBuilder(win.BuildConnectionString("Other"));
        Assert.True(wb.IntegratedSecurity);
        Assert.Equal("Other", wb.InitialCatalog);
    }

    [Fact]
    public void Store_add_update_rename_and_remove()
    {
        var store = ConnectionStore.Load();
        store.Connections.Clear();
        store.Add(new SqlrConnection { Name = "a", Server = "s1" });
        store.Add(new SqlrConnection { Name = "b", Server = "s2" });
        store.Update("a", new SqlrConnection { Name = "a2", Server = "s1b" });

        var reloaded = ConnectionStore.Load();
        Assert.Equal(["a2", "b"], reloaded.Connections.Select(c => c.Name).ToList()); // order kept
        Assert.Equal("s1b", reloaded.Find("A2")!.Server);

        Assert.True(reloaded.Remove("b"));
        Assert.False(reloaded.Remove("b"));
        Assert.Single(ConnectionStore.Load().Connections);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SqlrPaths.Connections));
    }

    [Theory]
    [InlineData(new string[0], CliCommand.LaunchPicker)]
    [InlineData(new[] { "-c", "x" }, CliCommand.Connect)]
    [InlineData(new[] { "-c", "x", "q.sql" }, CliCommand.Connect)]
    [InlineData(new[] { "-c", "x", "-q", "select 1" }, CliCommand.RunQuery)]
    [InlineData(new[] { "--connect", "x", "-i", "f.sql", "-f", "json" }, CliCommand.RunQuery)]
    [InlineData(new[] { "connections" }, CliCommand.ConnectionsList)]
    [InlineData(new[] { "connections", "rm", "x" }, CliCommand.ConnectionsRemove)]
    [InlineData(new[] { "connections", "edit", "x" }, CliCommand.ConnectionsEdit)]
    [InlineData(new[] { "connections", "remove" }, CliCommand.Invalid)]
    [InlineData(new[] { "--bogus" }, CliCommand.Invalid)]
    [InlineData(new[] { "-q", "select 1" }, CliCommand.Invalid)]
    [InlineData(new[] { "-c" }, CliCommand.Invalid)]
    [InlineData(new[] { "-c", "x", "-f", "xml" }, CliCommand.Invalid)]
    [InlineData(new[] { "--help" }, CliCommand.Help)]
    [InlineData(new[] { "--version" }, CliCommand.Version)]
    public void Args_parse(string[] argv, CliCommand expected) =>
        Assert.Equal(expected, Args.Parse(argv, stdinRedirected: false).Command);

    [Fact]
    public void Piped_stdin_runs_non_interactively()
    {
        Assert.Equal(CliCommand.RunQuery, Args.Parse(["-c", "x"], stdinRedirected: true).Command);
        var a = Args.Parse(["-c", "x", "-q", "select 1", "-f", "csv", "-d", "db"], stdinRedirected: false);
        Assert.Equal(ExportFormat.Csv, a.Format);
        Assert.Equal("db", a.Database);
    }

    [Fact]
    public void History_dedupes_consecutive_runs_and_persists()
    {
        File.Delete(SqlrPaths.History);
        var h = QueryHistory.Load(3);
        for (int i = 0; i < 5; i++)
            h.Add(new HistoryEntry(DateTime.Now, "c", "db", $"SELECT {i}", true, 1, 1));
        h.Add(new HistoryEntry(DateTime.Now, "c", "db", "SELECT 4", true, 1, 1));

        var reloaded = QueryHistory.Load(3);
        Assert.Equal(["SELECT 2", "SELECT 3", "SELECT 4"], reloaded.Entries.Select(e => e.Sql).ToList());
    }

    [Fact]
    public void Identifier_helpers()
    {
        Assert.Equal("Orders", SqlIdent.Quote("Orders"));
        Assert.Equal("[Order Details]", SqlIdent.Quote("Order Details"));
        Assert.Equal("[select]", SqlIdent.Quote("select"));
        Assert.Equal("[a]]b]", SqlIdent.Quote("a]b"));
        Assert.Equal(["db", "", "t"], SqlIdent.SplitParts("db..t"));
        Assert.Equal(["my schema", "a.b"], SqlIdent.SplitParts("[my schema].[a.b]"));
    }
}
