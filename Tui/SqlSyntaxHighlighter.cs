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

    // ── Colours (btop-inspired) ────────────────────────────────────────────
    private static TAttr ColorFor(T kind) => kind switch
    {
        T.Keyword    => new TAttr(Color.Yellow,        Color.Black),
        T.DdlKeyword => new TAttr(Color.BrightMagenta, Color.Black),
        T.Function   => new TAttr(Color.BrightCyan,    Color.Black),
        T.StringLit  => new TAttr(Color.BrightGreen,   Color.Black),
        T.Comment    => new TAttr(Color.Gray,           Color.Black),
        T.Number     => new TAttr(Color.Magenta,        Color.Black),
        T.Operator   => new TAttr(Color.Cyan,           Color.Black),
        T.AtVar      => new TAttr(Color.BrightBlue,     Color.Black),
        _            => new TAttr(Color.White,           Color.Black)
    };

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

    public static void Attach(TextView tv)
    {
        T[][] map  = [];
        string? cached = null;

        tv.DrawNormalColor += (_, e) =>
        {
            var text = tv.Text ?? "";
            // Rebuild lazily on text change (reference compare is fast)
            if (!ReferenceEquals(text, cached))
            {
                cached = text;
                map    = BuildMap(text);
            }

            // DrawNormalColor fires once per LINE — e.Line contains all cells for the line.
            // We must set Cell.Attribute on each cell individually (Cell is a value type).
            int row = e.UnwrappedPosition.Item1;
            if (row < 0 || row >= map.Length) return;

            var rowMap = map[row];
            for (int col = 0; col < e.Line.Count; col++)
            {
                if (col >= rowMap.Length) break;
                var cell = e.Line[col];
                cell.Attribute = ColorFor(rowMap[col]);
                e.Line[col] = cell;
            }
        };
    }

    // ── Tokenizer ──────────────────────────────────────────────────────────

    private static T[][] BuildMap(string text)
    {
        var flat = new T[text.Length];
        int i    = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // ── Block comment  /* ... */ ───────────────────────────────────
            if (c == '/' && Peek(text, i + 1) == '*')
            {
                int s = i; i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '*' && Peek(text, i + 1) == '/') { i += 2; break; }
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
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                Fill(flat, s, i, T.AtVar);
                continue;
            }

            // ── Number literal ────────────────────────────────────────────
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
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
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[s..i];
                var kind = ClassifyWord(word);
                Fill(flat, s, i, kind);
                continue;
            }

            // ── Brackets [identifier] ─────────────────────────────────────
            if (c == '[')
            {
                int s = i++;
                while (i < text.Length && text[i] != ']') i++;
                if (i < text.Length) i++; // consume ']'
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
    private static void Fill(T[] arr, int from, int to, T kind)
    {
        for (int j = from; j < to; j++) arr[j] = kind;
    }

    private static T ClassifyWord(string word) =>
        DmlKeywords.Contains(word) ? T.Keyword :
        DdlKeywords.Contains(word) ? T.DdlKeyword :
        Functions.Contains(word)   ? T.Function :
        T.Default;

    private static T[][] FlatToLines(T[] flat, string text)
    {
        var lines = new List<T[]>();
        int start = 0;
        for (int j = 0; j <= flat.Length; j++)
        {
            if (j == flat.Length || text[j] == '\n')
            {
                lines.Add(flat[start..j]);
                start = j + 1;
            }
        }
        return [.. lines];
    }
}
