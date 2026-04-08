using System.Text.RegularExpressions;

namespace Sqlr.Db;

public enum CompletionKind { Keyword, Table, View, Column, Schema, Database, Routine }

public record Completion(string Text, CompletionKind Kind)
{
    public string Display => Kind switch
    {
        CompletionKind.Table   => $"{Text,-35} [table]",
        CompletionKind.View    => $"{Text,-35} [view]",
        CompletionKind.Column  => $"{Text,-35} [column]",
        CompletionKind.Schema  => $"{Text,-35} [schema]",
        CompletionKind.Database => $"{Text,-35} [database]",
        CompletionKind.Routine => $"{Text,-35} [proc/fn]",
        _                      => $"{Text,-35} [keyword]"
    };
}

public sealed class SqlCompleter
{
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
        "FULL JOIN", "CROSS JOIN", "ON", "AND", "OR", "NOT", "IN", "EXISTS",
        "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM",
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE",
        "CREATE INDEX", "CREATE VIEW", "DROP VIEW",
        "GROUP BY", "ORDER BY", "HAVING", "DISTINCT", "TOP", "AS",
        "UNION", "UNION ALL", "INTERSECT", "EXCEPT",
        "WITH", "CTE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "CAST", "CONVERT", "COALESCE", "NULLIF", "ISNULL",
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "GETDATE", "GETUTCDATE", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART",
        "YEAR", "MONTH", "DAY",
        "LEN", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "SUBSTRING",
        "CHARINDEX", "REPLACE", "STUFF", "FORMAT",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "OVER", "PARTITION BY",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK",
        "TRY", "CATCH", "THROW", "RAISERROR",
        "USE", "GO", "PRINT",
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT",
        "DECIMAL", "NUMERIC", "FLOAT", "REAL", "MONEY",
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "TEXT", "NTEXT",
        "DATETIME", "DATETIME2", "DATE", "TIME", "DATETIMEOFFSET",
        "UNIQUEIDENTIFIER", "VARBINARY", "IMAGE",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "NOT NULL", "DEFAULT",
        "NOLOCK", "WITH (NOLOCK)", "ROWLOCK", "TABLOCK",
        "INFORMATION_SCHEMA", "sys"
    ];

    // Context-triggering keywords (after these, suggest tables/columns/etc.)
    private static readonly string[] TableKeywords  = ["FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "UPDATE", "INTO"];
    private static readonly string[] ColumnKeywords = ["SELECT", "WHERE", "SET", "HAVING", "ORDER BY", "GROUP BY", "ON", "AND", "OR", "DISTINCT", "BY"];

    private readonly DatabaseSchema _schema;

    public SqlCompleter(DatabaseSchema schema) => _schema = schema;

    /// <summary>
    /// Returns a ranked list of completions for the partial text before the cursor.
    /// </summary>
    public IReadOnlyList<Completion> GetCompletions(string textBeforeCursor)
    {
        var word    = ExtractCurrentWord(textBeforeCursor);
        var context = DetectContext(textBeforeCursor, word);

        var candidates = BuildCandidates(context, textBeforeCursor);

        // Filter by fuzzy prefix match on the current word
        if (!string.IsNullOrEmpty(word))
            candidates = candidates.Where(c => FuzzyMatch(c.Text, word)).ToList();

        // Sort: exact prefix first, then alphabetical within kind
        return candidates
            .OrderBy(c => c.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => (int)c.Kind)
            .ThenBy(c => c.Text, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    // ── Word extraction ────────────────────────────────────────────────────

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
        char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@';

    // ── Context detection ──────────────────────────────────────────────────

    private enum CompletionContext { Tables, Columns, Databases, Keywords, Schemas, DotColumns }

    private CompletionContext DetectContext(string text, string currentWord)
    {
        // Dot-completion: "TableName." → suggest columns for that table
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith('.'))
        {
            return CompletionContext.DotColumns;
        }

        // Find the last significant keyword before the current word
        var beforeWord = string.IsNullOrEmpty(currentWord)
            ? text
            : text[..^currentWord.Length];

        var lastKw = FindLastKeyword(beforeWord);

        if (TableKeywords.Contains(lastKw, StringComparer.OrdinalIgnoreCase))
            return CompletionContext.Tables;

        if (ColumnKeywords.Contains(lastKw, StringComparer.OrdinalIgnoreCase))
            return CompletionContext.Columns;

        if (lastKw.Equals("USE", StringComparison.OrdinalIgnoreCase) ||
            lastKw.Equals("DATABASE", StringComparison.OrdinalIgnoreCase))
            return CompletionContext.Databases;

        if (lastKw.Equals("SCHEMA", StringComparison.OrdinalIgnoreCase))
            return CompletionContext.Schemas;

        return CompletionContext.Keywords;
    }

    private static readonly Regex KeywordPattern = new(
        @"\b(SELECT|FROM|WHERE|JOIN|INNER\s+JOIN|LEFT\s+JOIN|RIGHT\s+JOIN|FULL\s+JOIN|CROSS\s+JOIN" +
        @"|UPDATE|INTO|SET|HAVING|ORDER\s+BY|GROUP\s+BY|ON|AND|OR|DISTINCT|BY|USE|DATABASE|SCHEMA)\b",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

    private static string FindLastKeyword(string text)
    {
        var m = KeywordPattern.Match(text);
        return m.Success ? Regex.Replace(m.Value, @"\s+", " ").ToUpperInvariant() : "";
    }

    // ── Candidate generation ───────────────────────────────────────────────

    private List<Completion> BuildCandidates(CompletionContext ctx, string fullText)
    {
        var list = new List<Completion>();

        switch (ctx)
        {
            case CompletionContext.Tables:
                foreach (var t in _schema.Tables)
                {
                    list.Add(new Completion(t.Name,     t.IsView ? CompletionKind.View  : CompletionKind.Table));
                    list.Add(new Completion(t.FullName, t.IsView ? CompletionKind.View  : CompletionKind.Table));
                }
                foreach (var s in _schema.Schemas)
                    list.Add(new Completion(s, CompletionKind.Schema));
                break;

            case CompletionContext.Columns:
                // Try to extract table refs from the FROM clause
                var tableRefs = ExtractTableRefs(fullText);
                var seenCols  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (tableRefs.Count > 0)
                {
                    foreach (var tRef in tableRefs)
                        foreach (var col in _schema.ColumnsForTable(tRef))
                            if (seenCols.Add(col.Column))
                                list.Add(new Completion(col.Column, CompletionKind.Column));
                }
                else
                {
                    foreach (var col in _schema.Columns)
                        if (seenCols.Add(col.Column))
                            list.Add(new Completion(col.Column, CompletionKind.Column));
                }
                AddKeywords(list);
                break;

            case CompletionContext.DotColumns:
                // Parent is the identifier just before the dot
                var parent = ExtractParentBeforeDot(fullText);
                if (!string.IsNullOrEmpty(parent))
                {
                    // Try as table name first
                    var cols = _schema.ColumnsForTable(parent).ToList();
                    if (cols.Count > 0)
                        foreach (var col in cols)
                            list.Add(new Completion(col.Column, CompletionKind.Column));
                    else
                        // Try as schema name
                        foreach (var t in _schema.Tables.Where(t =>
                            t.Schema.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new Completion(t.Name, t.IsView ? CompletionKind.View : CompletionKind.Table));
                }
                break;

            case CompletionContext.Databases:
                foreach (var db in _schema.Databases)
                    list.Add(new Completion(db, CompletionKind.Database));
                break;

            case CompletionContext.Schemas:
                foreach (var s in _schema.Schemas)
                    list.Add(new Completion(s, CompletionKind.Schema));
                break;

            case CompletionContext.Keywords:
            default:
                AddKeywords(list);
                foreach (var t in _schema.Tables)
                    list.Add(new Completion(t.Name, t.IsView ? CompletionKind.View : CompletionKind.Table));
                foreach (var r in _schema.Routines)
                    list.Add(new Completion(r.Name, CompletionKind.Routine));
                break;
        }

        return list;
    }

    private static void AddKeywords(List<Completion> list)
    {
        foreach (var kw in Keywords)
            list.Add(new Completion(kw, CompletionKind.Keyword));
    }

    // Extract table/alias names from the FROM clause of the current statement
    private static readonly Regex FromPattern = new(
        @"\bFROM\b\s+([\w\.\[\]""]+)(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase);

    private static readonly Regex JoinPattern = new(
        @"\bJOIN\b\s+([\w\.\[\]""]+)(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase);

    private static List<string> ExtractTableRefs(string sql)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromPattern.Matches(sql))
        {
            var raw = m.Groups[1].Value.Trim('[', ']', '"', '`');
            refs.Add(raw.Contains('.') ? raw.Split('.').Last() : raw);
            if (m.Groups[2].Success) refs.Add(m.Groups[2].Value);
        }
        foreach (Match m in JoinPattern.Matches(sql))
        {
            var raw = m.Groups[1].Value.Trim('[', ']', '"', '`');
            refs.Add(raw.Contains('.') ? raw.Split('.').Last() : raw);
            if (m.Groups[2].Success) refs.Add(m.Groups[2].Value);
        }
        return [.. refs];
    }

    private static string ExtractParentBeforeDot(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd('.');
        int i = trimmed.Length - 1;
        while (i >= 0 && IsWordChar(trimmed[i])) i--;
        return trimmed[(i + 1)..];
    }

    // ── Fuzzy matching ─────────────────────────────────────────────────────

    private static bool FuzzyMatch(string candidate, string typed)
    {
        if (string.IsNullOrEmpty(typed)) return true;
        // Prefix match first (fast path)
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        // Contains match
        if (candidate.Contains(typed, StringComparison.OrdinalIgnoreCase)) return true;
        // Fuzzy: all typed chars appear in order in candidate
        int ti = 0;
        foreach (char c in candidate)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(typed[ti]))
                if (++ti == typed.Length) return true;
        }
        return false;
    }
}
