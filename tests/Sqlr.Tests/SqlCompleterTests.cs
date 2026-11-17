using Sqlr.Db;
using Xunit;

namespace Sqlr.Tests;

public class SqlCompleterTests
{
    private readonly SqlCompleter _c = new(FakeSchema.Build());

    /// <summary>"|" marks the cursor; the rest of the text is still scanned for FROM clauses.</summary>
    private IReadOnlyList<Completion> At(string textWithCursor)
    {
        int i = textWithCursor.IndexOf('|');
        Assert.True(i >= 0, "test text needs a | cursor marker");
        var full = textWithCursor.Remove(i, 1);
        return _c.GetCompletions(full[..i], full);
    }

    private static List<string> Texts(IEnumerable<Completion> cs) => cs.Select(c => c.Text).ToList();

    [Fact]
    public void Tables_after_FROM_are_schema_qualified_outside_dbo()
    {
        var top = At("SELECT * FROM Cust|")[0];
        Assert.Equal("Customers", top.Text);
        Assert.Equal(CompletionKind.Table, top.Kind);
        Assert.Equal("sales.Customers", top.InsertText);
    }

    [Fact]
    public void Same_name_tables_in_two_schemas_are_both_offered()
    {
        var orders = At("SELECT * FROM Ord|").Where(c => c.Text == "Orders").ToList();
        Assert.Equal(2, orders.Count);
        Assert.Contains(orders, c => c.InsertText == "sales.Orders");
        Assert.Contains(orders, c => c.InsertText == "archive.Orders");
    }

    [Fact]
    public void Names_that_need_brackets_are_quoted_on_insert()
    {
        var od = At("SELECT * FROM [Order|").First(c => c.Text == "Order Details");
        Assert.Equal("[Order Details]", od.InsertText);

        var cols = At("SELECT d.| FROM dbo.[Order Details] d");
        Assert.Equal("[Line Id]", cols.First(c => c.Text == "Line Id").InsertText);
        Assert.Equal("[Key]", cols.First(c => c.Text == "Key").InsertText); // reserved word
    }

    [Fact]
    public void Columns_come_from_tables_referenced_anywhere_in_the_query()
    {
        var cs = At("SELECT Na| FROM sales.Customers");
        Assert.Equal("Name", cs[0].Text);
        Assert.Equal(CompletionKind.Column, cs[0].Kind);
        Assert.DoesNotContain("Reason", Texts(At("SELECT | FROM sales.Customers")));
    }

    [Fact]
    public void Alias_dot_completes_that_tables_columns_only()
    {
        var cs = Texts(At("SELECT o.| FROM sales.Orders o JOIN sales.Customers c ON c.CustomerId = o.CustomerId"));
        Assert.Equal(["CustomerId", "OrderId", "Total"], cs.Order().ToList());
    }

    [Fact]
    public void Columns_keep_table_order_until_something_is_typed()
    {
        Assert.Equal(["OrderId", "CustomerId", "Total"], Texts(At("SELECT o.| FROM sales.Orders o")));
    }

    [Fact]
    public void Alias_dot_keeps_working_after_typing_a_prefix()
    {
        var cs = At("SELECT o.To| FROM sales.Orders o");
        Assert.Equal("Total", cs[0].Text);
        Assert.All(cs, c => Assert.Equal(CompletionKind.Column, c.Kind));
    }

    [Fact]
    public void Alias_resolution_respects_the_schema()
    {
        var cs = Texts(At("SELECT * FROM archive.Orders a WHERE a.|"));
        Assert.Equal(["ArchivedOrderId", "Reason"], cs.Order().ToList());
    }

    [Fact]
    public void Schema_dot_lists_that_schemas_objects()
    {
        var cs = Texts(At("SELECT * FROM sales.|"));
        Assert.Contains("Customers", cs);
        Assert.Contains("ActiveCustomers", cs);
        Assert.Contains("GetCustomer", cs);
        Assert.DoesNotContain("Reason", cs);
        Assert.Single(cs, c => c == "Orders");
    }

    [Fact]
    public void System_views_complete_after_sys_dot()
    {
        Assert.Contains("objects", Texts(At("SELECT * FROM sys.|")));
        Assert.DoesNotContain("objects", Texts(At("SELECT * FROM obj|")));
    }

    [Fact]
    public void Nothing_inside_strings_or_comments()
    {
        Assert.Empty(At("SELECT 'FROM Cust|"));
        Assert.Empty(At("-- SELECT * FROM Cust|"));
        Assert.Empty(At("/* FROM Cust|"));
    }

    [Fact]
    public void Clause_keywords_after_a_complete_table_reference()
    {
        var cs = At("SELECT * FROM sales.Customers c W|");
        Assert.Equal("WHERE", cs[0].Text);
        Assert.DoesNotContain(cs, c => c.Kind == CompletionKind.Table);
    }

    [Fact]
    public void Keywords_are_not_mistaken_for_aliases()
    {
        // "WHERE" must not become an alias of Orders (it used to)
        Assert.Equal("Total", At("SELECT * FROM sales.Orders WHERE Tot|")[0].Text);
        var refs = SqlCompleter.ExtractTableRefs("SELECT * FROM sales.Orders WITH (NOLOCK) WHERE 1=1");
        Assert.Null(Assert.Single(refs).Alias);
    }

    [Fact]
    public void Comma_joins_and_CTEs_are_understood()
    {
        Assert.Equal(["CustomerId", "OrderId", "Total"],
            Texts(At("SELECT * FROM sales.Customers c, sales.Orders o WHERE o.|")).Order().ToList());
        Assert.Contains("recent", Texts(At("WITH recent AS (SELECT 1 AS x) SELECT * FROM rec|")));
    }

    [Fact]
    public void Insert_column_list_offers_target_columns()
    {
        Assert.Equal("Email", At("INSERT INTO sales.Customers (Name, Em|")[0].Text);
    }

    [Fact]
    public void Update_set_offers_columns()
    {
        Assert.Equal("Email", At("UPDATE sales.Customers SET Em|")[0].Text);
    }

    [Fact]
    public void Exec_offers_procedures()
    {
        var top = At("EXEC GetC|")[0];
        Assert.Equal("GetCustomer", top.Text);
        Assert.Equal("sales.GetCustomer", top.InsertText);
    }

    [Fact]
    public void Use_offers_databases()
    {
        Assert.Equal(["SqlrOther", "SqlrTest"], Texts(At("USE Sqlr|")).Order().ToList());
    }

    [Fact]
    public void Variables_declared_in_the_script_complete()
    {
        Assert.Equal("@from", At("DECLARE @from date = '2026-01-01'; SELECT * FROM t WHERE d > @f|")[0].Text);
    }

    [Fact]
    public void Prefix_matches_rank_above_fuzzy_matches()
    {
        var cs = Texts(At("SELECT * FROM sales.Customers WHERE Cu|"));
        Assert.Equal("CustomerId", cs[0]);
    }

    [Fact]
    public void Schema_provider_is_read_on_every_call()
    {
        var schema = new DatabaseSchema();
        var c      = new SqlCompleter(() => schema);
        Assert.DoesNotContain(c.GetCompletions("SELECT * FROM Cust"), x => x.Text == "Customers");
        schema = FakeSchema.Build();
        Assert.Contains(c.GetCompletions("SELECT * FROM Cust"), x => x.Text == "Customers");
    }
}
