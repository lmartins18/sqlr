using System.Runtime.CompilerServices;
using TAttr = Terminal.Gui.Attribute;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// Attaches T-SQL syntax highlighting to a <see cref="TextView"/> via the
/// <c>DrawNormalColor</c> event. Token map is rebuilt lazily when the text changes.
/// </summary>
public static class SqlSyntaxHighlighter
{
    // ── Token kinds ────────────────────────────────────────────────────────
    private enum T : byte { Default, Keyword, DdlKeyword, Function, StringLit, Comment, Number, Operator, AtVar }

    // ── Colours (from the active theme) ────────────────────────────────────
    private static TAttr ColorFor(T kind)
    {
        var p = Theme.Current;
        return new TAttr(kind switch
        {
            T.Keyword    => p.Keyword,
            T.DdlKeyword => p.Ddl,
            T.Function   => p.Function,
            T.StringLit  => p.String,
            T.Comment    => p.Comment,
            T.Number     => p.Number,
            T.Operator   => p.Operator,
            T.AtVar      => p.Variable,
            _            => p.Fg
        }, p.Bg);
    }

    // ── Keyword sets ───────────────────────────────────────────────────────
    private static readonly HashSet<string> DmlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","JOIN","INNER","LEFT","RIGHT","FULL","OUTER","CROSS",
        "ON","GROUP","ORDER","HAVING","DISTINCT","TOP","AS","INTO","SET","VALUES",
        "UNION","ALL","INTERSECT","EXCEPT","WITH","BY",
        "INSERT","UPDATE","DELETE","MERGE","USING",
        "AND","OR","NOT","IN","EXISTS","BETWEEN","LIKE","IS","NULL",
        "CASE","WHEN","THEN","ELSE","END",
        "EXEC","EXECUTE","GO","USE",
        "DECLARE","PRINT","BEGIN","COMMIT","ROLLBACK","TRANSACTION","TRAN",
        "IF","ELSE","WHILE","RETURN","BREAK","CONTINUE",
        "TRY","CATCH","THROW","RAISERROR",
        "OVER","PARTITION","ROWS","RANGE","PRECEDING","FOLLOWING","CURRENT","ROW",
        "OFFSET","FETCH","NEXT","ONLY",
        "ASC","DESC","NULLS","FIRST","LAST",
        "NOLOCK","ROWLOCK","TABLOCK","UPDLOCK","HOLDLOCK","READPAST","XLOCK",
        "OUTPUT","INTO","INSERTED","DELETED",
        "PIVOT","UNPIVOT","FOR","XML","JSON","PATH","AUTO","RAW",
    };

    private static readonly HashSet<string> DdlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE","ALTER","DROP","TRUNCATE","TABLE","VIEW","INDEX","PROCEDURE","PROC",
        "FUNCTION","TRIGGER","SCHEMA","DATABASE","COLUMN","CONSTRAINT",
        "PRIMARY","FOREIGN","KEY","REFERENCES","UNIQUE","DEFAULT","CHECK","IDENTITY",
        "CLUSTERED","NONCLUSTERED","INCLUDE","COLUMNSTORE",
        "ENABLE","DISABLE","REBUILD","REORGANIZE","STATISTICS",
    };

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT","COUNT_BIG","SUM","AVG","MIN","MAX","STDEV","VAR",
        "COALESCE","ISNULL","NULLIF","CAST","CONVERT","PARSE","TRY_CAST","TRY_CONVERT","TRY_PARSE",
        "GETDATE","GETUTCDATE","SYSDATETIME","DATEADD","DATEDIFF","DATENAME","DATEPART",
        "YEAR","MONTH","DAY","EOMONTH","DATEFROMPARTS",
        "LEN","DATALENGTH","LTRIM","RTRIM","TRIM","UPPER","LOWER",
        "SUBSTRING","LEFT","RIGHT","REPLACE","STUFF","FORMAT","REPLICATE","SPACE",
        "CHARINDEX","PATINDEX","STRING_AGG","STRING_SPLIT","CONCAT","CONCAT_WS",
        "ROW_NUMBER","RANK","DENSE_RANK","NTILE","LEAD","LAG",
        "FIRST_VALUE","LAST_VALUE","PERCENT_RANK","CUME_DIST",
        "ISNUMERIC","ISDATE","DB_NAME","OBJECT_ID","OBJECT_NAME","SCHEMA_NAME",
        "NEWID","NEWSEQUENTIALID",
        "ABS","CEILING","FLOOR","ROUND","POWER","SQRT","SQUARE","SIGN","LOG","EXP",
        "CHOOSE","IIF","SWITCH",
        "JSON_VALUE","JSON_QUERY","JSON_MODIFY","OPENJSON","FOR_JSON",
        "OPENROWSET","OPENQUERY","OPENDATASOURCE",
    };

    // ── Public attach ──────────────────────────────────────────────────────

    /// <param name="search">When given, every match of the term it returns is highlighted (like nvim's hlsearch).</param>
    public static void Attach(TextView tv, Func<string?>? search = null)
    {
        T[][]    map     = [];
        bool[][] matches = [];
        bool     dirty   = true;
        string?  term    = null;

        // Re-tokenize at most once per change (not once per drawn cell).
        tv.ContentsChanged += (_, _) => dirty = true;
        tv.TextChanged     += (_, _) => dirty = true;

        tv.DrawNormalColor += (_, e) =>
        {
            var wanted = search?.Invoke();
            if (dirty || wanted != term)
            {
                var text = tv.Text ?? "";
                if (dirty) map = BuildMap(text);
                matches = string.IsNullOrEmpty(wanted) ? [] : MatchMap(text, wanted);
                term    = wanted;
                dirty   = false;
            }

            // DrawNormalColor fires once per CELL: e.Col indexes the cell in e.Line, and
            // UnwrappedPosition is its (row, col) in the unwrapped text.
            var (row, col) = e.UnwrappedPosition;
            if (row < 0 || row >= map.Length) return;

            var rowMap = map[row];
            if (col < 0 || col >= rowMap.Length || e.Col < 0 || e.Col >= e.Line.Count) return;

            var cell = e.Line[e.Col];
            cell.Attribute = row < matches.Length && col < matches[row].Length && matches[row][col]
                ? new TAttr(Theme.Current.HiFg, Theme.Current.Gold)
                : ColorFor(rowMap[col]);
            e.Line[e.Col]  = cell;
        };
    }

    /// <summary>Per line, per rune: is the cell inside a (smart-case) match of <paramref name="term"/>?</summary>
    private static bool[][] MatchMap(string text, string term)
    {
        var flat = new bool[text.Length];
        var cmp  = term.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int i = text.IndexOf(term, cmp); i >= 0; i = text.IndexOf(term, i + term.Length, cmp))
            Array.Fill(flat, true, i, term.Length);
        return FlatToLines(flat, text);
    }

    // ── Tokenizer ──────────────────────────────────────────────────────────

    /// <summary>Token kind name per character, per line (exposed for tests).</summary>
    internal static string[][] Classify(string text) =>
        BuildMap(text).Select(line => line.Select(k => k.ToString()).ToArray()).ToArray();

    private static T[][] BuildMap(string text)
    {
        var flat = new T[text.Length];
        int i    = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // ── Block comment  /* ... */ (nestable in T-SQL) ──────────────
            if (c == '/' && Peek(text, i + 1) == '*')
            {
                int s = i, depth = 0;
                while (i < text.Length)
                {
                    if (text[i] == '/' && Peek(text, i + 1) == '*') { depth++; i += 2; continue; }
                    if (text[i] == '*' && Peek(text, i + 1) == '/') { depth--; i += 2; if (depth == 0) break; continue; }
                    i++;
                }
                Fill(flat, s, i, T.Comment);
                continue;
            }

            // ── Line comment  -- ... ──────────────────────────────────────
            if (c == '-' && Peek(text, i + 1) == '-')
            {
                int s = i;
                while (i < text.Length && text[i] != '\n') i++;
                Fill(flat, s, i, T.Comment);
                continue;
            }

            // ── String literal  '...'  ('' = escaped quote) ───────────────
            if (c == '\'')
            {
                int s = i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'' && Peek(text, i + 1) == '\'') { i += 2; continue; }
                    if (text[i] == '\'') { i++; break; }
                    i++;
                }
                Fill(flat, s, i, T.StringLit);
                continue;
            }

            // ── @variable / @@system ──────────────────────────────────────
            if (c == '@')
            {
                int s = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '@')) i++;
                Fill(flat, s, i, T.AtVar);
                continue;
            }

            // ── N'unicode string' ─────────────────────────────────────────
            if ((c == 'N' || c == 'n') && Peek(text, i + 1) == '\'' && (i == 0 || !IsIdentChar(text[i - 1])))
            {
                flat[i++] = T.StringLit;
                continue;
            }

            // ── Hex literal 0x1F ──────────────────────────────────────────
            if (c == '0' && (Peek(text, i + 1) == 'x' || Peek(text, i + 1) == 'X') && (i == 0 || !IsIdentChar(text[i - 1])))
            {
                int s = i; i += 2;
                while (i < text.Length && char.IsAsciiHexDigit(text[i])) i++;
                Fill(flat, s, i, T.Number);
                continue;
            }

            // ── Number literal ────────────────────────────────────────────
            if (char.IsDigit(c) && (i == 0 || !IsIdentChar(text[i - 1])))
            {
                int s = i;
                bool hasDot = false;
                while (i < text.Length)
                {
                    if (text[i] == '.' && !hasDot) { hasDot = true; i++; continue; }
                    if (char.IsDigit(text[i])) { i++; continue; }
                    break;
                }
                Fill(flat, s, i, T.Number);
                continue;
            }

            // ── Identifier / keyword ──────────────────────────────────────
            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                int s = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                var word = text[s..i];
                var kind = ClassifyWord(word);
                Fill(flat, s, i, kind);
                continue;
            }

            // ── Brackets [identifier] ─────────────────────────────────────
            if (c == '[')
            {
                int s = i++;
                while (i < text.Length)
                {
                    if (text[i] == ']' && Peek(text, i + 1) == ']') { i += 2; continue; }
                    if (text[i] == ']') { i++; break; }
                    i++;
                }
                Fill(flat, s, i, T.Default);
                continue;
            }

            // ── Operators ─────────────────────────────────────────────────
            if ("=<>!+-*/%&|^~".Contains(c))
            {
                flat[i++] = T.Operator;
                continue;
            }

            // Default (whitespace, newlines, punctuation) ──────────────────
            flat[i++] = T.Default;
        }

        return FlatToLines(flat, text);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char Peek(string s, int idx) => idx < s.Length ? s[idx] : '\0';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '$';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill(T[] arr, int from, int to, T kind)
    {
        for (int j = from; j < to; j++) arr[j] = kind;
    }

    private static T ClassifyWord(string word) =>
        DmlKeywords.Contains(word) ? T.Keyword :
        DdlKeywords.Contains(word) ? T.DdlKeyword :
        Functions.Contains(word)   ? T.Function :
        T.Default;

    /// <summary>Splits the per-char map into lines indexed by RUNE (the TextView's column unit).</summary>
    private static TItem[][] FlatToLines<TItem>(TItem[] flat, string text)
    {
        var lines = new List<TItem[]>();
        var cur   = new List<TItem>();
        for (int j = 0; j < flat.Length; j++)
        {
            if (text[j] == '\n')
            {
                lines.Add([.. cur]);
                cur.Clear();
                continue;
            }
            if (char.IsLowSurrogate(text[j]) && j > 0 && char.IsHighSurrogate(text[j - 1]))
                continue; // second half of a surrogate pair: same rune as the previous char
            cur.Add(flat[j]);
        }
        lines.Add([.. cur]);
        return [.. lines];
    }
}
