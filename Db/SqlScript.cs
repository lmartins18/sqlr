using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Sqlr.Db;

/// <summary>A GO-delimited batch. <see cref="StartLine"/> is the 0-based line it starts on in the script.</summary>
public sealed record SqlBatch(string Text, int StartLine, int RepeatCount);

/// <summary>
/// Lexical helpers for T-SQL scripts: blanking out comments/strings so scanners can't be fooled
/// by their contents, GO-batch splitting, and finding the statement block under the cursor.
/// </summary>
public static class SqlScript
{
    /// <summary>
    /// Returns <paramref name="sql"/> with every comment and string literal replaced by spaces
    /// (line breaks kept), so the result has identical length and line/column positions.
    /// Bracketed and double-quoted identifiers are left intact.
    /// </summary>
    public static string Mask(string sql)
    {
        var buf = sql.ToCharArray();
        int i   = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            // ── Line comment ──────────────────────────────────────────────
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                int s = i;
                while (i < sql.Length && sql[i] != '\n') i++;
                Blank(buf, s, i);
                continue;
            }

            // ── Block comment (T-SQL allows nesting) ──────────────────────
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                int s = i, depth = 0;
                while (i < sql.Length)
                {
                    if (sql[i] == '/' && Peek(sql, i + 1) == '*') { depth++; i += 2; continue; }
                    if (sql[i] == '*' && Peek(sql, i + 1) == '/') { depth--; i += 2; if (depth == 0) break; continue; }
                    i++;
                }
                Blank(buf, s, Math.Min(i, sql.Length));
                continue;
            }

            // ── String literal ('' escapes a quote) ───────────────────────
            if (c == '\'')
            {
                int s = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (Peek(sql, i + 1) == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                Blank(buf, s, i);
                continue;
            }

            // ── Quoted identifiers: skip so "--" or "'" inside don't count ─
            if (c == '[') { i = SkipUntil(sql, i + 1, ']'); continue; }
            if (c == '"') { i = SkipUntil(sql, i + 1, '"'); continue; }

            i++;
        }

        return new string(buf);
    }

    /// <summary>True when <paramref name="pos"/> sits inside a comment or string literal.</summary>
    public static bool IsInCommentOrString(string sql, int pos)
    {
        if (pos <= 0 || pos > sql.Length) return false;
        // Append a sentinel at the cursor: it gets blanked only if an unterminated
        // string or comment is still open at that point.
        var probe = Mask(sql[..pos] + "\u0001");
        return probe[^1] != '\u0001';
    }

    private static readonly Regex GoLine = new(
        @"^\s*GO(?:\s+(\d+))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a script on <c>GO</c> separator lines (optionally <c>GO n</c> to repeat), ignoring
    /// any GO that appears inside a comment or string. Comment-only batches are dropped.
    /// </summary>
    public static List<SqlBatch> SplitBatches(string sql)
    {
        var batches     = new List<SqlBatch>();
        var lines       = sql.Split('\n');
        var maskedLines = Mask(sql).Split('\n');

        int start = 0;
        for (int i = 0; i <= lines.Length; i++)
        {
            Match? m = i < lines.Length ? GoLine.Match(maskedLines[i].TrimEnd('\r')) : null;
            if (i < lines.Length && !m!.Success) continue;

            if (i > start)
            {
                var masked = string.Join('\n', maskedLines[start..i]);
                if (!string.IsNullOrWhiteSpace(masked))
                {
                    int repeat = m is { Success: true } && m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var n)
                        ? Math.Max(1, n)
                        : 1;
                    batches.Add(new SqlBatch(string.Join('\n', lines[start..i]), start, repeat));
                }
            }
            start = i + 1;
        }

        return batches;
    }

    /// <summary>
    /// The block of consecutive non-blank lines around <paramref name="row"/> (blank lines and GO
    /// separate blocks). Returns the block text and its first line, or null if the row is blank
    /// and no block touches it.
    /// </summary>
    public static (string Text, int StartLine)? BlockAt(string sql, int row)
    {
        var lines = sql.Split('\n');
        if (lines.Length == 0) return null;
        row = Math.Clamp(row, 0, lines.Length - 1);

        bool IsBreak(int r) => string.IsNullOrWhiteSpace(lines[r]) || GoLine.IsMatch(lines[r].TrimEnd('\r'));

        // Cursor on a blank line right after a block (just finished typing it) → use that block.
        if (IsBreak(row))
        {
            if (row > 0 && !IsBreak(row - 1)) row--;
            else return null;
        }

        int s = row, e = row;
        while (s > 0 && !IsBreak(s - 1)) s--;
        while (e < lines.Length - 1 && !IsBreak(e + 1)) e++;

        return (string.Join('\n', lines[s..(e + 1)]), s);
    }

    // DDL, or SELECT … INTO a permanent table. "INTO" not preceded by INSERT/MERGE is a SELECT INTO;
    // #temp and @table targets don't change this database's schema. (Linear: no lazy scan to a later INTO.)
    private static readonly Regex DdlPattern = new(
        @"\b(CREATE|ALTER|DROP|sp_rename)\b|(?<!\b(?:INSERT|MERGE)\s+)\bINTO\s+(?![@#])[\w\[]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the script may have changed the schema (so completions should be reloaded).</summary>
    public static bool MayChangeSchema(string sql) => DdlPattern.IsMatch(Mask(sql));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char Peek(string s, int idx) => idx < s.Length ? s[idx] : '\0';

    private static int SkipUntil(string s, int i, char close)
    {
        while (i < s.Length)
        {
            if (s[i] == close)
            {
                // ]] and "" are escaped closers inside quoted identifiers
                if (Peek(s, i + 1) == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    private static void Blank(char[] buf, int from, int to)
    {
        for (int j = from; j < to && j < buf.Length; j++)
            if (buf[j] != '\n' && buf[j] != '\r') buf[j] = ' ';
    }
}
