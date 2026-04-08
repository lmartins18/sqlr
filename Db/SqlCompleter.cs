using System.Text.RegularExpressions;

namespace Sqlr.Db;

public enum CompletionKind { Keyword, Table, View, Column, Schema, Database, Routine, Function }

public record Completion(string Text, CompletionKind Kind)
{
    public string Display => Kind switch
    {
        CompletionKind.Table    => $"{Text,-35} [table]",
        CompletionKind.View     => $"{Text,-35} [view]",
        CompletionKind.Column   => $"{Text,-35} [column]",
        CompletionKind.Schema   => $"{Text,-35} [schema]",
        CompletionKind.Database => $"{Text,-35} [database]",
        CompletionKind.Routine  => $"{Text,-35} [proc/fn]",
        CompletionKind.Function => $"{Text,-35} [function]",
        _                       => $"{Text,-35} [keyword]"
    };
}

public sealed class SqlCompleter
{
    private static readonly string[] DmlKeywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
        "FULL JOIN", "CROSS JOIN", "ON", "AND", "OR", "NOT", "IN", "EXISTS",
        "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM",
        "GROUP BY", "ORDER BY", "HAVING", "DISTINCT", "TOP", "AS",
        "UNION", "UNION ALL", "INTERSECT", "EXCEPT",
        "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK",
        "TRY", "CATCH", "THROW", "RAISERROR",
        "USE", "GO", "PRINT",
        "NOLOCK", "WITH (NOLOCK)", "ROWLOCK", "TABLOCK",
        "INFORMATION_SCHEMA", "sys"
    ];

    private static readonly string[] DdlKeywords =
    [
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE",
        "CREATE INDEX", "CREATE VIEW", "DROP VIEW",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "NOT NULL", "DEFAULT",
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT",
        "DECIMAL", "NUMERIC", "FLOAT", "REAL", "MONEY",
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "TEXT", "NTEXT",
        "DATETIME", "DATETIME2", "DATE", "TIME", "DATETIMEOFFSET",
        "UNIQUEIDENTIFIER", "VARBINARY", "IMAGE",
    ];

    private static readonly string[] SqlFunctions =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "CAST", "CONVERT", "COALESCE", "NULLIF", "ISNULL",
        "GETDATE", "GETUTCDATE", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART",
        "YEAR", "MONTH", "DAY",
        "LEN", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "SUBSTRING",
        "CHARINDEX", "REPLACE", "STUFF", "FORMAT",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "OVER", "PARTITION BY",
    ];

    // Context-triggering keywords (after these, suggest tables/columns/etc.)
    private static readonly string[] TableKeywords  = ["FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "UPDATE", "INTO"];
    private static readonly string[] ColumnKeywords = ["SELECT", "WHERE", "SET", "HAVING", "ORDER BY", "GROUP BY", "ON", "AND", "OR", "DISTINCT", "BY"];

    private readonly DatabaseSchema _schema;

    public SqlCompleter(DatabaseSchema schema) => _schema = schema;

    /// <summary>
    /// Returns a ranked list of completions for the partial text before the cursor.
    /// <paramref name="fullText"/> is the entire editor content and is used for FROM/JOIN
    /// scanning so that table refs after the cursor position are also considered.
    /// </summary>
    public IReadOnlyList<Completion> GetCompletions(string textBeforeCursor, string? fullText = null)
    {
        var scanText = string.IsNullOrEmpty(fullText) ? textBeforeCursor : fullText;
        var word     = ExtractCurrentWord(textBeforeCursor);
        var context  = DetectContext(textBeforeCursor, word);

        var candidates = BuildCandidates(context, textBeforeCursor, scanText);

        // Filter by fuzzy prefix match on the current word
        if (!string.IsNullOrEmpty(word))
            candidates = candidates.Where(c => FuzzyMatch(c.Text, word)).ToList();

        // Sort: exact prefix first, then columns before everything else, then alphabetical
        return candidates
            .OrderBy(c => c.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Kind == CompletionKind.Column ? 0 : 1)
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

    // textBeforeCursor — text up to the cursor, used for context detection and dot-parent extraction
    // scanText         — full editor text, used for FROM/JOIN table ref scanning
    private List<Completion> BuildCandidates(CompletionContext ctx, string textBeforeCursor, string scanText)
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
                // Scan the full query text so FROM clauses after the cursor are also found.
                // Only show columns from tables explicitly referenced — no all-schema fallback.
                // Only functions (not DDL/DML keywords) are relevant alongside column names.
                var tableRefs = ExtractTableNames(scanText);
                if (tableRefs.Count > 0)
                {
                    var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tRef in tableRefs)
                        foreach (var col in _schema.ColumnsForTable(tRef))
                            if (seenCols.Add(col.Column))
                                list.Add(new Completion(col.Column, CompletionKind.Column));
                }
                AddFunctions(list);
                break;

            case CompletionContext.DotColumns:
                // Identifier immediately before the dot — may be a table name or an alias.
                var parent = ExtractParentBeforeDot(textBeforeCursor);
                if (!string.IsNullOrEmpty(parent))
                {
                    // Resolve alias → table name using the full query text
                    var aliases  = ExtractAliasMap(scanText);
                    var resolved = aliases.TryGetValue(parent, out var aliasTarget) ? aliasTarget : parent;

                    var cols = _schema.ColumnsForTable(resolved).ToList();
                    if (cols.Count > 0)
                        foreach (var col in cols)
                            list.Add(new Completion(col.Column, CompletionKind.Column));
                    else
                        // Try as schema name → suggest tables in that schema
                        foreach (var t in _schema.Tables.Where(t =>
                            t.Schema.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
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
                AddDmlKeywords(list);
                AddDdlKeywords(list);
                AddFunctions(list);
                foreach (var t in _schema.Tables)
                    list.Add(new Completion(t.Name, t.IsView ? CompletionKind.View : CompletionKind.Table));
                foreach (var r in _schema.Routines)
                    list.Add(new Completion(r.Name, CompletionKind.Routine));
                break;
        }

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

    private static readonly Regex FromPattern = new(
        @"\bFROM\b\s+([\w\.\[\]""]+)(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase);

    private static readonly Regex JoinPattern = new(
        @"\bJOIN\b\s+([\w\.\[\]""]+)(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase);

    private static string TableNameFromRaw(string raw) =>
        raw.Trim('[', ']', '"', '`').Split('.').Last(p => p.Length > 0);

    /// <summary>Returns the bare table names referenced in FROM/JOIN clauses (no aliases).</summary>
    private static List<string> ExtractTableNames(string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromPattern.Matches(sql))
            names.Add(TableNameFromRaw(m.Groups[1].Value));
        foreach (Match m in JoinPattern.Matches(sql))
            names.Add(TableNameFromRaw(m.Groups[1].Value));
        return [.. names];
    }

    /// <summary>Returns a map of alias → bare table name for every aliased FROM/JOIN clause.</summary>
    private static Dictionary<string, string> ExtractAliasMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromPattern.Matches(sql))
            if (m.Groups[2].Success)
                map[m.Groups[2].Value] = TableNameFromRaw(m.Groups[1].Value);
        foreach (Match m in JoinPattern.Matches(sql))
            if (m.Groups[2].Success)
                map[m.Groups[2].Value] = TableNameFromRaw(m.Groups[1].Value);
        return map;
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
