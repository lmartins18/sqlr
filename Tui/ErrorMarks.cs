using System.Text.RegularExpressions;
using Sqlr.Db;

namespace Sqlr.Tui;

/// <summary>Editor text a failed run points at: <paramref name="Length"/> characters from (Row, Start), 0-based.</summary>
internal sealed record ErrorMark(int Row, int Start, int Length)
{
    public bool Covers(int row, int col) => row == Row && col >= Start && col < Start + Length;
}

internal static partial class ErrorMarks
{
    [GeneratedRegex(@"'([^']+)'|""([^""]+)""")]
    private static partial Regex Quoted();

    /// <summary>
    /// For each error with an editor line: the name its message quotes ("Invalid column name 'Foo'",
    /// "Incorrect syntax near 'FROM'"), found on that line; otherwise the whole line.
    /// </summary>
    public static List<ErrorMark> For(ExecutionResult result, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var marks = new List<ErrorMark>();
        foreach (var msg in result.Messages)
        {
            if (msg.Kind != MessageKind.Error || msg.Line is not int line || line < 1 || line > lines.Length) continue;
            var src = lines[line - 1];
            var body = msg.Text[(msg.Text.IndexOf('\n') + 1)..]; // after "Msg 207, Level 16, …, Line 3"
            var mark = Quoted().Matches(body)
                .Select(q => Find(line - 1, src, q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value))
                .FirstOrDefault(m => m is not null)
                ?? WholeLine(line - 1, src);
            if (mark is not null && !marks.Contains(mark)) marks.Add(mark);
        }
        return marks;
    }

    /// <summary>The name as written on the line, or its last part ("dbo.Foo" written as [Foo]).</summary>
    private static ErrorMark? Find(int row, string src, string name)
    {
        foreach (var part in new[] { name, name[(name.LastIndexOf('.') + 1)..] })
        {
            int at = part.Length == 0 ? -1 : src.IndexOf(part, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return new ErrorMark(row, at, part.Length);
        }
        return null;
    }

    private static ErrorMark? WholeLine(int row, string src)
    {
        var trimmed = src.Trim();
        return trimmed.Length == 0 ? null : new ErrorMark(row, src.IndexOf(trimmed, StringComparison.Ordinal), trimmed.Length);
    }
}
