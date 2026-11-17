using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sqlr.Db;

public enum ExportFormat { Csv, Tsv, Json, Markdown, SqlInsert, Table }

/// <summary>Serialises result rows to text formats (files, clipboard, stdout).</summary>
public static class ResultExport
{
    public static ExportFormat? FormatFromName(string? name) => name?.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "csv"                   => ExportFormat.Csv,
        "tsv" or "tab"          => ExportFormat.Tsv,
        "json"                  => ExportFormat.Json,
        "md" or "markdown"      => ExportFormat.Markdown,
        "sql" or "insert"       => ExportFormat.SqlInsert,
        "table" or "txt" or ""  => ExportFormat.Table,
        _                       => null
    };

    public static ExportFormat FormatFromPath(string path) =>
        FormatFromName(Path.GetExtension(path)) is { } f && f != ExportFormat.Table ? f : ExportFormat.Csv;

    public static string Write(ExportFormat format, IReadOnlyList<string> columns, IReadOnlyList<string> types,
                               IEnumerable<string?[]> rows, string tableName = "#results")
    {
        var sb = new StringBuilder();
        switch (format)
        {
            case ExportFormat.Csv:       WriteDelimited(sb, columns, rows, ',', csv: true);  break;
            case ExportFormat.Tsv:       WriteDelimited(sb, columns, rows, '\t', csv: false); break;
            case ExportFormat.Json:      WriteJson(sb, columns, types, rows);                break;
            case ExportFormat.Markdown:  WriteMarkdown(sb, columns, types, rows);            break;
            case ExportFormat.SqlInsert: WriteInserts(sb, columns, types, rows, tableName);  break;
            default:                     WriteTable(sb, columns, types, rows);               break;
        }
        return sb.ToString();
    }

    public static string Write(ExportFormat format, ResultSet rs, IEnumerable<string?[]>? rows = null) =>
        Write(format, rs.Columns, rs.ColumnTypes, rows ?? rs.Rows);

    // ── Delimited ──────────────────────────────────────────────────────────

    private static void WriteDelimited(StringBuilder sb, IReadOnlyList<string> columns, IEnumerable<string?[]> rows, char sep, bool csv)
    {
        AppendRow(sb, columns, sep, csv);
        foreach (var row in rows)
            AppendRow(sb, row, sep, csv);
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string?> values, char sep, bool csv)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(sep);
            var v = values[i];
            if (v is null) continue; // NULL → empty field
            if (csv) sb.Append(CsvField(v));
            else     sb.Append(v.Replace("\t", " ").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '));
        }
        sb.Append(csv ? "\r\n" : "\n");
    }

    private static string CsvField(string v) =>
        v.AsSpan().IndexOfAny(",\"\r\n") >= 0 || v.StartsWith(' ') || v.EndsWith(' ')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;

    /// <summary>A single row as tab-separated values (clipboard-friendly: pastes into Excel columns).</summary>
    public static string RowToTsv(IReadOnlyList<string?> values)
    {
        var sb = new StringBuilder();
        AppendRow(sb, values, '\t', csv: false);
        return sb.ToString().TrimEnd('\n');
    }

    // ── JSON ───────────────────────────────────────────────────────────────

    private static readonly JsonWriterOptions JsonWriterOpts = new()
    {
        Indented = true,
        Encoder  = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static void WriteJson(StringBuilder sb, IReadOnlyList<string> columns, IReadOnlyList<string> types, IEnumerable<string?[]> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWriterOpts))
        {
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < columns.Count; i++)
                {
                    var v = row[i];
                    if (v is null) { w.WriteNull(columns[i]); continue; }
                    var t = types[i].ToLowerInvariant();
                    if (t == "bit")
                        w.WriteBoolean(columns[i], v == "1");
                    else if (ValueFormatter.IsNumericType(t) && decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        w.WritePropertyName(columns[i]);
                        w.WriteRawValue(v, skipInputValidation: false);
                    }
                    else
                        w.WriteString(columns[i], v);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        sb.Append(Encoding.UTF8.GetString(ms.ToArray())).Append('\n');
    }

    // ── Markdown ───────────────────────────────────────────────────────────

    private static void WriteMarkdown(StringBuilder sb, IReadOnlyList<string> columns, IReadOnlyList<string> types, IEnumerable<string?[]> rows)
    {
        static string Cell(string? v) => v is null ? "NULL" : ValueFormatter.ForGrid(v).Replace("|", "\\|");

        sb.Append("| ").AppendJoin(" | ", columns.Select(Cell)).Append(" |\n");
        sb.Append("| ").AppendJoin(" | ", types.Select(t => ValueFormatter.IsNumericType(t) ? "---:" : "---")).Append(" |\n");
        foreach (var row in rows)
            sb.Append("| ").AppendJoin(" | ", row.Select(Cell)).Append(" |\n");
    }

    // ── SQL INSERT ─────────────────────────────────────────────────────────

    private static void WriteInserts(StringBuilder sb, IReadOnlyList<string> columns, IReadOnlyList<string> types,
                                     IEnumerable<string?[]> rows, string tableName)
    {
        var colList = string.Join(", ", columns.Select(SqlIdent.Bracket));
        foreach (var row in rows)
        {
            sb.Append("INSERT INTO ").Append(tableName).Append(" (").Append(colList).Append(") VALUES (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(SqlLiteral(row[i], types[i]));
            }
            sb.Append(");\n");
        }
    }

    public static string SqlLiteral(string? value, string type)
    {
        if (value is null) return "NULL";
        var t = type.ToLowerInvariant();
        if (ValueFormatter.IsNumericType(t) || t == "bit") return value;
        if (t is "binary" or "varbinary" or "image" or "timestamp" or "rowversion" && value.StartsWith("0x"))
            // Large binaries are truncated for display — never emit a silently-wrong value.
            return value.EndsWith('…') ? "NULL /* binary value too large, truncated */" : value;
        return "N'" + value.Replace("'", "''") + "'";
    }

    // ── Aligned text table (stdout) ────────────────────────────────────────

    private const int MaxTableCellWidth = 80;

    private static void WriteTable(StringBuilder sb, IReadOnlyList<string> columns, IReadOnlyList<string> types, IEnumerable<string?[]> rows)
    {
        var materialized = rows.ToList();
        int n            = columns.Count;
        var widths       = columns.Select(c => Math.Min(DisplayWidth(c), MaxTableCellWidth)).ToArray();

        static string Show(string? v) => v is null ? "NULL" : ValueFormatter.ForGrid(v);
        foreach (var row in materialized)
            for (int i = 0; i < n; i++)
                widths[i] = Math.Max(widths[i], Math.Min(DisplayWidth(Show(row[i])), MaxTableCellWidth));

        string Fit(string s, int i)
        {
            if (DisplayWidth(s) > widths[i]) s = TruncateToWidth(s, widths[i] - 1) + "…";
            var pad = new string(' ', Math.Max(0, widths[i] - DisplayWidth(s)));
            return ValueFormatter.IsNumericType(types[i]) ? pad + s : s + pad;
        }

        static string Line(IEnumerable<string> cells) => string.Join("  ", cells).TrimEnd();

        sb.Append(Line(columns.Select((c, i) => Fit(c, i)))).Append('\n');
        sb.Append(Line(widths.Select(w => new string('-', w)))).Append('\n');
        foreach (var row in materialized)
            sb.Append(Line(row.Select((v, i) => Fit(Show(v), i)))).Append('\n');
    }

    /// <summary>Terminal columns a string occupies: East Asian wide / emoji = 2, combining marks = 0.</summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var r in s.EnumerateRunes())
            w += RuneWidth(r);
        return w;
    }

    private static string TruncateToWidth(string s, int width)
    {
        var sb = new StringBuilder();
        int w  = 0;
        foreach (var r in s.EnumerateRunes())
        {
            int rw = RuneWidth(r);
            if (w + rw > width) break;
            sb.Append(r.ToString());
            w += rw;
        }
        return sb.ToString();
    }

    private static int RuneWidth(Rune r)
    {
        int v = r.Value;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark
                 or System.Globalization.UnicodeCategory.Format)
            return 0;
        bool wide = v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
                      or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60
                      or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }
}
