using System.Text.RegularExpressions;

namespace Sqlr.Db;

public enum CompletionKind { Keyword, Table, View, Column, Schema, Database, Routine, Function, Alias, Variable }

public record Completion(string Text, CompletionKind Kind, string? Detail = null, string? Insert = null)
{
    /// <summary>What actually goes into the editor (may be schema-qualified or [bracketed]).</summary>
    public string InsertText => Insert ?? Text;

    public string KindLabel => Kind switch
    {
        CompletionKind.Table    => "table",
        CompletionKind.View     => "view",
        CompletionKind.Column   => "column",
        CompletionKind.Schema   => "schema",
        CompletionKind.Database => "database",
        CompletionKind.Routine  => "proc/fn",
        CompletionKind.Function => "function",
        CompletionKind.Alias    => "alias",
        CompletionKind.Variable => "variable",
        _                       => "keyword"
    };

    public string Display => $"{Text,-35} [{KindLabel}]";
}

/// <summary>A FROM/JOIN/UPDATE/INTO reference found in the query text.</summary>
public sealed record TableRef(string? Database, string? Schema, string Name, string? Alias);

public sealed class SqlCompleter
{
    private static readonly string[] DmlKeywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
        "FULL JOIN", "CROSS JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AND", "OR", "NOT", "IN", "EXISTS",
        "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE", "OUTPUT",
        "GROUP BY", "ORDER BY", "HAVING", "DISTINCT", "TOP", "AS", "ASC", "DESC",
        "OFFSET", "FETCH NEXT", "ROWS ONLY",
        "UNION", "UNION ALL", "INTERSECT", "EXCEPT",
        "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "BEGIN TRAN", "BEGIN TRANSACTION",
        "IF", "WHILE", "RETURN", "TRY", "CATCH", "THROW", "RAISERROR",
        "USE", "GO", "PRINT", "SET NOCOUNT ON", "TRUNCATE TABLE",
        "NOLOCK", "WITH (NOLOCK)", "ROWLOCK", "TABLOCK", "NULL",
        "INFORMATION_SCHEMA", "sys"
    ];

    private static readonly string[] DdlKeywords =
    [
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE",
        "CREATE INDEX", "CREATE VIEW", "DROP VIEW", "CREATE OR ALTER",
        "CREATE PROCEDURE", "ALTER PROCEDURE", "CREATE FUNCTION",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "NOT NULL", "DEFAULT", "IDENTITY",
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT",
        "DECIMAL", "NUMERIC", "FLOAT", "REAL", "MONEY",
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "TEXT", "NTEXT",
        "DATETIME", "DATETIME2", "DATE", "TIME", "DATETIMEOFFSET",
        "UNIQUEIDENTIFIER", "VARBINARY", "IMAGE", "XML",
    ];

    private static readonly string[] SqlFunctions =
    [
        "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STRING_AGG",
        "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT", "COALESCE", "NULLIF", "ISNULL", "IIF",
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART",
        "DATEFROMPARTS", "EOMONTH", "YEAR", "MONTH", "DAY",
        "LEN", "DATALENGTH", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "SUBSTRING", "LEFT", "RIGHT",
        "CHARINDEX", "PATINDEX", "REPLACE", "REPLICATE", "STUFF", "FORMAT", "CONCAT", "CONCAT_WS", "STRING_SPLIT",
        "ROUND", "FLOOR", "CEILING", "ABS",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
        "OVER", "PARTITION BY",
        "JSON_VALUE", "JSON_QUERY", "OPENJSON", "NEWID", "OBJECT_ID", "DB_NAME", "SCHEMA_NAME", "@@ROWCOUNT", "@@IDENTITY", "SCOPE_IDENTITY",
    ];

    /// <summary>Clause keywords offered (on a prefix match) while typing column expressions.</summary>
    private static readonly string[] ClauseKeywords =
    [
        "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS NULL", "IS NOT NULL", "LIKE", "BETWEEN", "EXISTS",
        "AS", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "TOP", "NULL",
        "GROUP BY", "ORDER BY", "HAVING", "ASC", "DESC", "UNION", "UNION ALL",
        "JOIN", "INNER JOIN", "LEFT JOIN", "ON", "INTO", "OFFSET", "FETCH NEXT",
    ];

    /// <summary>Keywords that can follow a complete table reference.</summary>
    private static readonly string[] AfterTableKeywords =
    [
        "AS", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN",
        "CROSS APPLY", "OUTER APPLY", "ON", "GROUP BY", "ORDER BY", "HAVING", "SET", "WITH (NOLOCK)",
        "UNION", "UNION ALL", "OUTPUT", "VALUES", "SELECT", "OPTION",
    ];

    // Context-triggering keywords (after these, suggest tables/columns/etc.)
    private static readonly string[] TableKeywords  = ["FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "UPDATE", "INTO", "APPLY", "TABLE", "USING"];
    private static readonly string[] ColumnKeywords = ["SELECT", "WHERE", "SET", "HAVING", "ORDER BY", "GROUP BY", "ON", "AND", "OR", "DISTINCT", "BY", "WHEN", "THEN", "ELSE", "CASE", "NOT", "IN", "TOP", "PARTITION BY"];

    /// <summary>Words that can never be a table alias (they start the next clause instead).</summary>
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE","JOIN","INNER","LEFT","RIGHT","FULL","CROSS","OUTER","ON","GROUP","ORDER","HAVING","WITH","SET",
        "UNION","EXCEPT","INTERSECT","VALUES","SELECT","OUTPUT","OPTION","PIVOT","UNPIVOT","APPLY","FOR","AS",
        "USING","WHEN","THEN","ELSE","END","GO","AND","OR","NOT","DEFAULT","EXEC","EXECUTE","INSERT","UPDATE",
        "DELETE","MERGE","DECLARE","IF","BEGIN","RETURN","TABLESAMPLE","WINDOW","INTO","FROM","TOP","DISTINCT"
    };

    private readonly Func<DatabaseSchema> _schema;

    public SqlCompleter(DatabaseSchema schema) : this(() => schema) { }

    /// <summary>Takes a provider so a background schema reload is picked up on the next keystroke.</summary>
    public SqlCompleter(Func<DatabaseSchema> schemaProvider) => _schema = schemaProvider;

    /// <summary>
    /// Returns a ranked list of completions for the partial text before the cursor.
    /// <paramref name="fullText"/> is the entire editor content and is used for FROM/JOIN
    /// scanning so that table refs after the cursor position are also considered.
    /// </summary>
    public IReadOnlyList<Completion> GetCompletions(string textBeforeCursor, string? fullText = null)
    {
        if (SqlScript.IsInCommentOrString(textBeforeCursor, textBeforeCursor.Length))
            return [];

        var schema   = _schema();
        var scanText = SqlScript.Mask(string.IsNullOrEmpty(fullText) ? textBeforeCursor : fullText);
        var before   = SqlScript.Mask(textBeforeCursor);

        // "[Order D" — an unclosed bracket makes everything after it (spaces included) the word
        var (ctxEnd, word) = CurrentToken(before);
        int wordStart = before.Length - word.Length;

        List<Completion> candidates;
        bool keywordsByPrefixOnly = false;

        if (word.StartsWith('@'))
        {
            candidates = Variables(scanText, exceptAt: wordStart);
        }
        else if (ctxEnd > 0 && before[ctxEnd - 1] == '.')
        {
            candidates = DotCandidates(schema, before[..(ctxEnd - 1)], scanText);
        }
        else if (InsertColumnListTable(before[..ctxEnd]) is { } insertTarget)
        {
            // INSERT INTO t (a, b|  → columns of t
            candidates = Resolve(schema, insertTarget)
                .SelectMany(t => schema.ColumnsFor(t).Select(c => ColumnCompletion(c, t, false)))
                .ToList();
        }
        else
        {
            var ctx = DetectContext(before[..ctxEnd]);
            candidates = BuildCandidates(schema, ctx, scanText, out keywordsByPrefixOnly);
        }

        return Rank(candidates, word, keywordsByPrefixOnly);
    }

    private static IReadOnlyList<Completion> Rank(List<Completion> candidates, string word, bool keywordsByPrefixOnly)
    {
        var scored = new List<(Completion C, int Score)>();
        var seen   = new HashSet<(string, CompletionKind)>();

        foreach (var c in candidates)
        {
            // Same text in two schemas (sales.Orders / archive.Orders) are different suggestions.
            if (!seen.Add((c.InsertText.ToUpperInvariant(), c.Kind))) continue;
            int score = MatchScore(c.Text, word);
            if (score < 0) continue;
            if (keywordsByPrefixOnly && c.Kind == CompletionKind.Keyword && (score > 1 || word.Length == 0)) continue;
            scored.Add((c, score));
        }

        var ranked = scored
            .OrderBy(s => s.Score)
            .ThenBy(s => KindPriority(s.C.Kind));

        // Nothing typed yet: keep the natural order (columns in table order). Otherwise
        // shorter names first, so the closest match to what you typed wins.
        if (word.Length > 0)
            ranked = ranked
                .ThenBy(s => s.C.Text.Length)
                .ThenBy(s => s.C.Text, StringComparer.OrdinalIgnoreCase);

        return ranked.Take(50).Select(s => s.C).ToList();
    }

    private static int KindPriority(CompletionKind k) => k switch
    {
        CompletionKind.Column   => 0,
        CompletionKind.Alias    => 1,
        CompletionKind.Variable => 1,
        CompletionKind.Table    => 2,
        CompletionKind.View     => 2,
        CompletionKind.Routine  => 3,
        CompletionKind.Schema   => 4,
        CompletionKind.Keyword  => 5,
        CompletionKind.Function => 6,
        _                       => 7
    };

    // ── Word extraction ────────────────────────────────────────────────────

    /// <summary>
    /// The identifier being typed: its start (including an opening '[') and the text to match.
    /// Pass comment/string-masked text so brackets inside literals are ignored.
    /// </summary>
    public static (int Start, string Word) CurrentToken(string maskedBeforeCursor)
    {
        int open = maskedBeforeCursor.LastIndexOf('[');
        if (open >= 0 && maskedBeforeCursor.IndexOf(']', open) < 0 && maskedBeforeCursor.IndexOf('\n', open) < 0)
            return (open, maskedBeforeCursor[(open + 1)..]);

        var w = ExtractCurrentWord(maskedBeforeCursor);
        return (maskedBeforeCursor.Length - w.Length, w);
    }

    /// <summary>The word being typed follows a complete table reference ("FROM t o|"), so it's most likely an alias.</summary>
    public static bool IsAliasPosition(string textBeforeCursor)
    {
        var before = SqlScript.Mask(textBeforeCursor);
        var (start, _) = CurrentToken(before);
        return DetectContext(before[..start]) == CompletionContext.AfterTable;
    }

    public static string ExtractCurrentWord(string textBeforeCursor)
    {
        if (string.IsNullOrEmpty(textBeforeCursor)) return "";

        // Walk backwards to find start of current identifier
        int i = textBeforeCursor.Length - 1;
        while (i >= 0 && IsWordChar(textBeforeCursor[i]))
            i--;

        return textBeforeCursor[(i + 1)..];
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$';

    // ── Context detection ──────────────────────────────────────────────────

    private enum CompletionContext { Tables, Columns, Databases, Keywords, Schemas, Routines, AfterTable }

    private const string Ident = @"(?:\[[^\]]*\]|""[^""]*""|[\w@#$]+)";
    private const string MultiPartName = Ident + @"(?:\s*\.\s*" + Ident + @"?){0,3}";

    private static readonly Regex KeywordPattern = new(
        @"\b(SELECT|FROM|WHERE|JOIN|INNER\s+JOIN|LEFT\s+(?:OUTER\s+)?JOIN|RIGHT\s+(?:OUTER\s+)?JOIN|FULL\s+(?:OUTER\s+)?JOIN|CROSS\s+JOIN" +
        @"|APPLY|UPDATE|INTO|SET|HAVING|ORDER\s+BY|GROUP\s+BY|PARTITION\s+BY|ON|AND|OR|DISTINCT|BY|USE|DATABASE|SCHEMA" +
        @"|EXEC|EXECUTE|TABLE|USING|WHEN|THEN|ELSE|CASE|NOT|IN|TOP|VALUES)\b",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft | RegexOptions.CultureInvariant);

    private static CompletionContext DetectContext(string beforeWord)
    {
        var m = KeywordPattern.Match(beforeWord);
        if (!m.Success) return CompletionContext.Keywords;
        var afterKeyword = beforeWord[(m.Index + m.Length)..];

        var kw = Regex.Replace(m.Value, @"\s+", " ").ToUpperInvariant()
                      .Replace(" OUTER JOIN", " JOIN");

        if (TableKeywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
        {
            // "FROM Orders o W|" — past the table reference: clause keywords, not more tables.
            var rest = afterKeyword.Trim();
            if (rest.Length > 0 && !rest.EndsWith(',') && Regex.IsMatch(afterKeyword, @"^\s*" + MultiPartName + @"\s"))
                return CompletionContext.AfterTable;
            return CompletionContext.Tables;
        }

        if (kw is "EXEC" or "EXECUTE")
            return afterKeyword.Trim().Length == 0 ? CompletionContext.Routines : CompletionContext.Keywords;

        if (kw == "VALUES")
            return CompletionContext.Keywords;

        if (ColumnKeywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
            return CompletionContext.Columns;

        if (kw is "USE" or "DATABASE")
            return CompletionContext.Databases;

        if (kw == "SCHEMA")
            return CompletionContext.Schemas;

        return CompletionContext.Keywords;
    }

    // ── Candidate generation ───────────────────────────────────────────────

    // before   — masked text up to the cursor
    // scanText — masked full editor text, used for FROM/JOIN table ref scanning
    private List<Completion> BuildCandidates(
        DatabaseSchema schema, CompletionContext ctx, string scanText, out bool keywordsByPrefixOnly)
    {
        var list = new List<Completion>();
        keywordsByPrefixOnly = false;

        switch (ctx)
        {
            case CompletionContext.Tables:
                AddTables(list, schema);
                foreach (var cte in ExtractCteNames(scanText))
                    list.Add(new Completion(cte, CompletionKind.Table, "cte"));
                foreach (var s in schema.Schemas)
                    list.Add(new Completion(s, CompletionKind.Schema, Insert: SqlIdent.Quote(s)));
                break;

            case CompletionContext.AfterTable:
                foreach (var kw in AfterTableKeywords)
                    list.Add(new Completion(kw, CompletionKind.Keyword));
                break;

            case CompletionContext.Columns:
            {
                // Only columns of tables the query references — no all-schema fallback.
                var refs = ExtractTableRefs(scanText);
                foreach (var r in refs)
                {
                    foreach (var t in Resolve(schema, r))
                        foreach (var col in schema.ColumnsFor(t))
                            list.Add(ColumnCompletion(col, t, refs.Count > 1));
                    list.Add(new Completion(r.Alias ?? r.Name, CompletionKind.Alias,
                        r.Alias is null ? "table" : $"→ {(r.Schema is null ? "" : r.Schema + ".")}{r.Name}"));
                }
                foreach (var kw in ClauseKeywords)
                    list.Add(new Completion(kw, CompletionKind.Keyword));
                AddFunctions(list);
                keywordsByPrefixOnly = true;
                break;
            }

            case CompletionContext.Databases:
                foreach (var db in schema.Databases)
                    list.Add(new Completion(db, CompletionKind.Database, Insert: SqlIdent.Quote(db)));
                break;

            case CompletionContext.Schemas:
                foreach (var s in schema.Schemas)
                    list.Add(new Completion(s, CompletionKind.Schema, Insert: SqlIdent.Quote(s)));
                break;

            case CompletionContext.Routines:
                foreach (var r in schema.Routines)
                    list.Add(RoutineCompletion(r));
                foreach (var s in schema.Schemas)
                    list.Add(new Completion(s, CompletionKind.Schema, Insert: SqlIdent.Quote(s)));
                break;

            case CompletionContext.Keywords:
            default:
                AddDmlKeywords(list);
                AddDdlKeywords(list);
                AddFunctions(list);
                AddTables(list, schema);
                foreach (var r in schema.Routines)
                    list.Add(RoutineCompletion(r));
                break;
        }

        return list;
    }

    private static Completion ColumnCompletion(ColumnInfo col, TableInfo table, bool showTable) =>
        new(col.Column, CompletionKind.Column,
            showTable ? $"{col.DataType} · {table.Name}" : col.DataType,
            SqlIdent.Quote(col.Column));

    private static Completion RoutineCompletion(RoutineInfo r) =>
        new(r.Name, CompletionKind.Routine,
            $"{(r.IsProcedure ? "proc" : "function")} · {r.Schema}",
            r.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? SqlIdent.Quote(r.Name) : r.QuotedFullName);

    private static void AddTables(List<Completion> list, DatabaseSchema schema)
    {
        foreach (var t in schema.UserTables)
        {
            // Qualify anything outside dbo so the inserted name resolves to the table you picked.
            var insert = t.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? SqlIdent.Quote(t.Name) : t.QuotedFullName;
            list.Add(new Completion(t.Name, t.IsView ? CompletionKind.View : CompletionKind.Table, t.Schema, insert));
        }
    }

    private List<Completion> DotCandidates(DatabaseSchema schema, string beforeDot, string scanText)
    {
        var list = new List<Completion>();

        // Only the tail can hold the name; bounding it keeps the end-anchored match cheap.
        var tail = beforeDot[Math.Max(0, beforeDot.Length - 512)..];
        var m    = Regex.Match(tail, MultiPartNameAtEnd, RegexOptions.CultureInvariant);
        if (!m.Success || m.Length == 0) return list;

        var parts  = SqlIdent.SplitParts(m.Value);
        var parent = parts[^1];
        var grand  = parts.Count >= 2 ? parts[^2] : null;

        // alias.  or  table.
        if (parts.Count == 1)
        {
            var aliases = ExtractTableRefs(scanText)
                .Where(r => r.Alias is not null && r.Alias.Equals(parent, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var tables = aliases.Count > 0
                ? aliases.SelectMany(r => Resolve(schema, r)).ToList()
                : schema.FindTables(null, parent).ToList();

            foreach (var t in tables)
                foreach (var col in schema.ColumnsFor(t))
                    list.Add(ColumnCompletion(col, t, tables.Count > 1));
        }
        // schema.table.
        else if (!string.IsNullOrEmpty(grand))
        {
            foreach (var t in schema.FindTables(grand, parent))
                foreach (var col in schema.ColumnsFor(t))
                    list.Add(ColumnCompletion(col, t, false));
        }

        if (list.Count > 0) return list;

        // schema.  (or db.schema.)  → its tables, views and routines
        foreach (var t in schema.Tables.Where(t => t.Schema.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Completion(t.Name, t.IsView ? CompletionKind.View : CompletionKind.Table, t.Schema, SqlIdent.Quote(t.Name)));
        foreach (var r in schema.Routines.Where(r => r.Schema.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Completion(r.Name, CompletionKind.Routine, r.IsProcedure ? "proc" : "function", SqlIdent.Quote(r.Name)));

        // db.  or  db..  → schemas
        if (list.Count == 0 && (schema.Databases.Contains(parent, StringComparer.OrdinalIgnoreCase) || (parent.Length == 0 && grand is { Length: > 0 })))
            foreach (var s in schema.Schemas)
                list.Add(new Completion(s, CompletionKind.Schema, Insert: SqlIdent.Quote(s)));

        return list;
    }

    private const string MultiPartNameAtEnd = Ident + @"?(?:\s*\.\s*" + Ident + @"?){0,3}$";

    /// <summary>@variables used in the script, except the one being typed at <paramref name="exceptAt"/>.</summary>
    private static List<Completion> Variables(string scanText, int exceptAt)
    {
        var list = new List<Completion>();
        foreach (Match m in Regex.Matches(scanText, @"(?<![\w@])@[\w#$]+", RegexOptions.CultureInvariant))
            if (m.Index != exceptAt)
                list.Add(new Completion(m.Value, CompletionKind.Variable));
        return list;
    }

    private static void AddDmlKeywords(List<Completion> list)
    {
        foreach (var kw in DmlKeywords)
            list.Add(new Completion(kw, CompletionKind.Keyword));
    }

    private static void AddDdlKeywords(List<Completion> list)
    {
        foreach (var kw in DdlKeywords)
            list.Add(new Completion(kw, CompletionKind.Keyword));
    }

    private static void AddFunctions(List<Completion> list)
    {
        foreach (var fn in SqlFunctions)
            list.Add(new Completion(fn, CompletionKind.Function));
    }

    // ── Table reference scanning ───────────────────────────────────────────

    private static readonly Regex TableRefPattern = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO|APPLY|USING)\s+(?<name>" + MultiPartName + @")(?:\s+(?:AS\s+)?(?<alias>" + Ident + @"))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Continuation of an old-style comma join list: FROM a x, b y
    private static readonly Regex CommaRefPattern = new(
        @"\G\s*,\s*(?<name>" + MultiPartName + @")(?:\s+(?:AS\s+)?(?<alias>" + Ident + @"))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every table reference (with alias, when it has one) in FROM/JOIN/UPDATE/INTO/APPLY clauses.</summary>
    public static List<TableRef> ExtractTableRefs(string maskedSql)
    {
        var refs = new List<TableRef>();
        foreach (Match m in TableRefPattern.Matches(maskedSql))
        {
            AddRef(refs, m);
            if (!m.Value.StartsWith("FROM", StringComparison.OrdinalIgnoreCase)) continue;

            int at = m.Index + m.Length;
            for (var c = CommaRefPattern.Match(maskedSql, at); c.Success; c = CommaRefPattern.Match(maskedSql, at))
            {
                AddRef(refs, c);
                at = c.Index + c.Length;
            }
        }
        return refs;
    }

    private static void AddRef(List<TableRef> refs, Match m)
    {
        var parts = SqlIdent.SplitParts(m.Groups["name"].Value);
        var name  = parts[^1];
        if (name.Length == 0 || name.StartsWith('@')) return;

        string? alias = m.Groups["alias"].Success ? SqlIdent.Unquote(m.Groups["alias"].Value) : null;
        if (alias is not null && NotAnAlias.Contains(alias)) alias = null;

        var schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null;
        var db     = parts.Count >= 3 && parts[^3].Length > 0 ? parts[^3] : null;
        var r      = new TableRef(db, schema, name, alias);
        if (!refs.Contains(r)) refs.Add(r);
    }

    private static IEnumerable<TableInfo> Resolve(DatabaseSchema schema, TableRef r)
    {
        // Another database's objects aren't in the loaded metadata.
        if (r.Database is not null && !r.Database.Equals(schema.DatabaseName, StringComparison.OrdinalIgnoreCase))
            return [];
        return schema.FindTables(r.Schema, r.Name);
    }

    private static readonly Regex InsertColumnsPattern = new(
        @"\bINSERT\s+(?:INTO\s+)?(?<name>" + MultiPartName + @")\s*\((?<cols>[^()]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static TableRef? InsertColumnListTable(string before)
    {
        var m = InsertColumnsPattern.Match(before);
        if (!m.Success) return null;
        var parts = SqlIdent.SplitParts(m.Groups["name"].Value);
        return new TableRef(null, parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null, parts[^1], null);
    }

    private static readonly Regex CtePattern = new(
        @"(?:\bWITH|,)\s*(?<name>" + Ident + @")\s*(?:\([^()]*\))?\s*AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<string> ExtractCteNames(string maskedSql) =>
        CtePattern.Matches(maskedSql)
            .Select(m => SqlIdent.Unquote(m.Groups["name"].Value))
            .Where(n => !NotAnAlias.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // ── Fuzzy matching ─────────────────────────────────────────────────────

    /// <summary>0 exact · 1 prefix · 2 contains · 3 fuzzy (chars in order) · -1 no match.</summary>
    private static int MatchScore(string candidate, string typed)
    {
        if (string.IsNullOrEmpty(typed)) return 1;
        if (candidate.Equals(typed, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.Contains(typed, StringComparison.OrdinalIgnoreCase)) return 2;
        int ti = 0;
        foreach (char c in candidate)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(typed[ti]))
                if (++ti == typed.Length) return 3;
        }
        return -1;
    }
}
