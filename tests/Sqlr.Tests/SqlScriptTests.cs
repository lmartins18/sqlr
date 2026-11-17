using Sqlr.Db;
using Xunit;

namespace Sqlr.Tests;

public class SqlScriptTests
{
    [Fact]
    public void Mask_blanks_comments_and_strings_but_keeps_positions()
    {
        var sql    = "SELECT 'a--b', [x--y] -- tail\n/* FROM t */ FROM u";
        var masked = SqlScript.Mask(sql);

        Assert.Equal(sql.Length, masked.Length);
        Assert.Equal('\n', masked[sql.IndexOf('\n')]);
        Assert.DoesNotContain("a--b", masked);
        Assert.Contains("[x--y]", masked);          // bracketed identifiers survive
        Assert.DoesNotContain("tail", masked);
        Assert.DoesNotContain("FROM t", masked);
        Assert.EndsWith("FROM u", masked);
    }

    [Fact]
    public void Mask_handles_nested_block_comments_and_escaped_quotes()
    {
        var masked = SqlScript.Mask("/* a /* b */ c */ SELECT 'it''s' x");
        Assert.Equal("SELECT", masked.Trim()[..6]);
        Assert.EndsWith(" x", masked);
        Assert.DoesNotContain("it", masked);
    }

    [Theory]
    [InlineData("SELECT 'ab", true)]
    [InlineData("SELECT 'ab' ", false)]
    [InlineData("SELECT 'it''s", true)]
    [InlineData("-- note", true)]
    [InlineData("-- note\nSELECT ", false)]
    [InlineData("/* x", true)]
    [InlineData("/* x */ ", false)]
    [InlineData("SELECT [a b", false)]
    public void IsInCommentOrString(string text, bool expected) =>
        Assert.Equal(expected, SqlScript.IsInCommentOrString(text, text.Length));

    [Fact]
    public void SplitBatches_on_GO_lines_with_start_lines()
    {
        var batches = SqlScript.SplitBatches("SELECT 1\nGO\nSELECT 2\nSELECT 3\ngo\n");
        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1", batches[0].Text);
        Assert.Equal(0, batches[0].StartLine);
        Assert.Equal("SELECT 2\nSELECT 3", batches[1].Text);
        Assert.Equal(2, batches[1].StartLine);
    }

    [Fact]
    public void SplitBatches_supports_repeat_count_semicolon_and_trailing_comment()
    {
        var batches = SqlScript.SplitBatches("PRINT 1\nGO 3\nPRINT 2\n  GO; -- done\nPRINT 3");
        Assert.Equal(3, batches.Count);
        Assert.Equal(3, batches[0].RepeatCount);
        Assert.Equal(1, batches[1].RepeatCount);
        Assert.Equal("PRINT 3", batches[2].Text);
    }

    [Fact]
    public void SplitBatches_ignores_GO_inside_strings_and_comments()
    {
        Assert.Single(SqlScript.SplitBatches("SELECT '\nGO\n' AS x"));
        Assert.Single(SqlScript.SplitBatches("/*\nGO\n*/ SELECT 1"));
        Assert.Single(SqlScript.SplitBatches("SELECT 1 -- GO\nSELECT 2"));
        Assert.Single(SqlScript.SplitBatches("PRINT 'GO'"));
        Assert.Single(SqlScript.SplitBatches("SELECT * FROM GOODS"));
    }

    [Fact]
    public void SplitBatches_handles_crlf_and_drops_comment_only_batches()
    {
        var batches = SqlScript.SplitBatches("-- header only\r\nGO\r\nSELECT 1\r\nGO\r\n");
        var b = Assert.Single(batches);
        Assert.Equal(2, b.StartLine);
        Assert.Equal("SELECT 1\r", b.Text);
    }

    [Fact]
    public void SplitBatches_empty_script_has_no_batches()
    {
        Assert.Empty(SqlScript.SplitBatches(""));
        Assert.Empty(SqlScript.SplitBatches("  \n GO \n"));
    }

    [Fact]
    public void BlockAt_returns_paragraph_around_cursor()
    {
        const string sql = "SELECT 1\nFROM a\n\nSELECT 2\nFROM b\n";
        Assert.Equal(("SELECT 1\nFROM a", 0), SqlScript.BlockAt(sql, 1));
        Assert.Equal(("SELECT 2\nFROM b", 3), SqlScript.BlockAt(sql, 3));
        // Cursor on the blank line right after a block → that block
        Assert.Equal(("SELECT 1\nFROM a", 0), SqlScript.BlockAt(sql, 2));
        Assert.Null(SqlScript.BlockAt("\n\n\n", 1));
    }

    [Fact]
    public void BlockAt_treats_GO_as_a_separator()
    {
        Assert.Equal(("SELECT 2", 2), SqlScript.BlockAt("SELECT 1\nGO\nSELECT 2", 2));
    }

    [Fact]
    public void MayChangeSchema_is_linear_on_big_scripts()
    {
        var sql = string.Join("\nUNION ALL\n", Enumerable.Range(0, 10_000).Select(i => $"SELECT {i} AS n FROM sales.Orders"));
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SqlScript.MayChangeSchema(sql));
        Assert.True(sw.ElapsedMilliseconds < 1_000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("INSERT INTO dbo.t SELECT 1", false)]
    [InlineData("MERGE INTO dbo.t AS x USING s ON 1=0 WHEN NOT MATCHED THEN INSERT VALUES (1);", false)]
    [InlineData("CREATE TABLE t (x int)", true)]
    [InlineData("drop view v", true)]
    [InlineData("SELECT * INTO dbo.t2 FROM t", true)]
    [InlineData("SELECT * INTO #tmp FROM t", false)]   // temp tables live in tempdb
    [InlineData("SELECT * FROM t -- create table", false)]
    [InlineData("SELECT 'CREATE' AS x", false)]
    [InlineData("SELECT @x = 1", false)]
    public void MayChangeSchema(string sql, bool expected) =>
        Assert.Equal(expected, SqlScript.MayChangeSchema(sql));
}
