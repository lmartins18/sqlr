using System.Drawing;
using System.Text.RegularExpressions;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// Cursor/text helpers for the SQL <see cref="TextView"/>. Edits go through selection + InsertText
/// (the same path as typing) so they are undoable and don't reset the scroll position.
/// Linear offsets always index the raw <c>tv.Text</c> string (UTF-16), so they stay consistent whether
/// lines end in \n or \r\n. The TextView's columns count runes, so emoji and other characters outside
/// the BMP are converted explicitly.
/// </summary>
internal static class EditorText
{
    public static int LinearCursor(TextView tv) => ToLinear(tv.Text ?? "", tv.CurrentRow, tv.CurrentColumn);

    /// <summary>(row, column in runes) → UTF-16 offset into <paramref name="text"/>.</summary>
    public static int ToLinear(string text, int row, int col)
    {
        int pos = 0, r = 0;
        while (r < row)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0) return text.Length;
            pos = nl + 1;
            r++;
        }
        int lineEnd = text.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = text.Length;
        if (lineEnd > pos && text[lineEnd - 1] == '\r') lineEnd--;

        for (int c = 0; c < col && pos < lineEnd; c++)
            pos += RuneLength(text, pos);
        return Math.Min(pos, lineEnd);
    }

    /// <summary>UTF-16 offset → (row, column in runes).</summary>
    public static (int Row, int Col) ToRowCol(string text, int linear)
    {
        linear = Math.Clamp(linear, 0, text.Length);
        int row = 0, lineStart = 0;
        for (int i = 0; i < linear; i++)
        {
            if (text[i] == '\n') { row++; lineStart = i + 1; }
        }

        int col = 0;
        for (int i = lineStart; i < linear; i += RuneLength(text, i))
            col++;
        return (row, col);
    }

    /// <summary>UTF-16 index of rune column <paramref name="col"/> within a single line.</summary>
    public static int Utf16Index(string line, int col)
    {
        int i = 0;
        for (int c = 0; c < col && i < line.Length; c++)
            i += RuneLength(line, i);
        return Math.Min(i, line.Length);
    }

    private static int RuneLength(string s, int i) =>
        char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;

    public static string TextBeforeCursor(TextView tv)
    {
        var text = tv.Text ?? "";
        return text[..LinearCursor(tv)];
    }

    public static void MoveCursor(TextView tv, int row, int col)
    {
        tv.IsSelecting    = false;
        tv.CursorPosition = new Point(Math.Max(0, col), Math.Max(0, row));
    }

    /// <summary>Replaces text[start..end) with <paramref name="replacement"/> as an undoable edit.</summary>
    public static void ReplaceRange(TextView tv, int start, int end, string replacement)
    {
        var text = tv.Text ?? "";
        start = Math.Clamp(start, 0, text.Length);
        end   = Math.Clamp(end, start, text.Length);

        var (sr, sc) = ToRowCol(text, start);
        var (er, ec) = ToRowCol(text, end);

        tv.IsSelecting    = false;
        tv.CursorPosition = new Point(sc, sr);
        if (end > start)
        {
            tv.SelectionStartRow    = sr;
            tv.SelectionStartColumn = sc;
            tv.CursorPosition       = new Point(ec, er);
            tv.IsSelecting          = true;
            if (replacement.Length == 0)
            {
                tv.DeleteCharLeft();  // deletes the selection
                tv.IsSelecting = false; // a lingering empty selection is drawn one cell left of the text
                return;
            }
        }
        if (replacement.Length > 0)
        {
            if (replacement.Any(char.IsSurrogate))
            {
                // TextView.InsertText throws on characters outside the BMP (e.g. emoji); fall back
                // to rewriting the text (not undoable, but correct).
                var fresh = tv.Text ?? "";
                tv.Text = fresh[..start] + replacement + fresh[start..];
                var (nr, nc) = ToRowCol(tv.Text ?? "", start + replacement.Length);
                MoveCursor(tv, nr, nc);
                return;
            }
            tv.InsertText(replacement);
        }
        tv.IsSelecting = false;
    }

    /// <summary>The current selection and the editor line it starts on, or null when nothing is selected.</summary>
    public static (string Text, int StartLine)? Selection(TextView tv)
    {
        if (!tv.IsSelecting || tv.SelectedLength == 0) return null;
        var sel = tv.SelectedText ?? "";
        if (string.IsNullOrWhiteSpace(sel)) return null;
        return (sel, Math.Min(tv.SelectionStartRow, tv.CurrentRow));
    }

    // ── Words ──────────────────────────────────────────────────────────────

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$';

    /// <summary>Ctrl+Backspace: deletes from start of current word to cursor.</summary>
    public static void DeleteWordLeft(TextView tv)
    {
        var text = tv.Text ?? "";
        var end  = LinearCursor(tv);
        if (end == 0) return;

        int i = end - 1;
        // Skip trailing whitespace (but stop at a line break — don't join lines silently)
        while (i >= 0 && text[i] is ' ' or '\t') i--;
        if (i >= 0 && IsWordChar(text[i]))
            while (i >= 0 && IsWordChar(text[i])) i--;
        else if (i == end - 1)
            i--; // a lone symbol or newline: delete just that
        int start = i + 1;
        if (start > 0 && start < text.Length && text[start] == '\n' && text[start - 1] == '\r') start--;

        ReplaceRange(tv, start, end, "");
    }

    /// <summary>Ctrl+Delete: deletes from cursor to end of current word.</summary>
    public static void DeleteWordRight(TextView tv)
    {
        var text  = tv.Text ?? "";
        var start = LinearCursor(tv);
        if (start >= text.Length) return;

        int i = start;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (i < text.Length && IsWordChar(text[i]))
            while (i < text.Length && IsWordChar(text[i])) i++;
        else if (i == start)
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;

        ReplaceRange(tv, start, i, "");
    }

    private static readonly Regex MultiPart = new(
        @"(?:\[[^\]]*\]|""[^""]*""|[\w@#$]+)(?:\.(?:\[[^\]]*\]|""[^""]*""|[\w@#$]+)?)*",
        RegexOptions.CultureInvariant);

    /// <summary>The (possibly multi-part, bracketed) identifier under or just before the cursor.</summary>
    public static string IdentifierUnderCursor(TextView tv)
    {
        var lines = (tv.Text ?? "").Split('\n');
        if (tv.CurrentRow >= lines.Length) return "";
        var line = lines[tv.CurrentRow].TrimEnd('\r');
        int col  = Utf16Index(line, tv.CurrentColumn);

        foreach (Match m in MultiPart.Matches(line))
        {
            if (col >= m.Index && col <= m.Index + m.Length)
                return m.Value.Trim('.');
        }
        return "";
    }

    // ── Comments ───────────────────────────────────────────────────────────

    /// <summary>Toggles "-- " on the selected lines (or the cursor line).</summary>
    public static void ToggleLineComment(TextView tv)
    {
        var text = tv.Text ?? "";
        int r0 = tv.CurrentRow, r1 = tv.CurrentRow;
        if (tv.IsSelecting && tv.SelectedLength > 0)
        {
            r0 = Math.Min(tv.SelectionStartRow, tv.CurrentRow);
            r1 = Math.Max(tv.SelectionStartRow, tv.CurrentRow);
            // A selection ending at column 0 doesn't include that line
            if (r1 > r0 && (tv.SelectionStartRow == r1 ? tv.SelectionStartColumn : tv.CurrentColumn) == 0) r1--;
        }

        var lines = text.Split('\n');
        r1 = Math.Min(r1, lines.Length - 1);
        var block = lines[r0..(r1 + 1)];

        bool allCommented = block.Where(l => l.Trim().Length > 0).All(l => l.TrimStart().StartsWith("--"));
        int  indent       = block.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        int  row = tv.CurrentRow, col = tv.CurrentColumn;

        // One single-line edit per line, bottom-up so earlier offsets stay valid. Each is a plain
        // insert/delete without newlines, which the TextView's undo history records correctly.
        for (int r = r1; r >= r0; r--)
        {
            var l = lines[r];
            if (l.Trim().Length == 0) continue;
            int lineStart = ToLinear(text, r, 0);

            if (allCommented)
            {
                int at = l.IndexOf("--", StringComparison.Ordinal);
                int n  = at + 2 < l.Length && l[at + 2] == ' ' ? 3 : 2;
                ReplaceRange(tv, lineStart + at, lineStart + at + n, "");
            }
            else
            {
                ReplaceRange(tv, lineStart + Math.Min(indent, l.Length), lineStart + Math.Min(indent, l.Length), "-- ");
            }
            text = tv.Text ?? "";
        }

        MoveCursor(tv, row, allCommented ? Math.Max(0, col - 3) : col + 3);
    }

    /// <summary>
    /// If the two characters immediately before the cursor are "..", after an identifier
    /// (outside strings/comments), replaces them with ".dbo." so that cross-database
    /// shorthand (db..table) expands to a valid three-part name.
    /// </summary>
    public static bool ExpandDoubleDot(TextView tv)
    {
        var text = tv.Text ?? "";
        var pos  = LinearCursor(tv);
        if (pos < 3 || text[pos - 1] != '.' || text[pos - 2] != '.') return false;
        if (!(IsWordChar(text[pos - 3]) || text[pos - 3] == ']')) return false;
        if (Sqlr.Db.SqlScript.IsInCommentOrString(text, pos)) return false;

        ReplaceRange(tv, pos - 2, pos, ".dbo.");
        return true;
    }
}
