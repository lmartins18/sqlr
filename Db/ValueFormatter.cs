using System.Globalization;
using System.Text;

namespace Sqlr.Db;

/// <summary>
/// Turns SQL Server values into stable, culture-independent text (matching SSMS conventions:
/// ISO dates, 0x-hex binary, 1/0 for bit, upper-case GUIDs).
/// </summary>
public static class ValueFormatter
{
    /// <summary>Binary values longer than this are shown truncated (the full value is rarely useful on screen).</summary>
    public const int MaxBinaryBytes = 8_192;

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "decimal", "numeric", "money", "smallmoney", "float", "real"
    };

    public static bool IsNumericType(string sqlTypeName) => NumericTypes.Contains(sqlTypeName);

    /// <summary>Formats a value read from a data reader. Returns null for SQL NULL.</summary>
    public static string? Format(object? value, string sqlTypeName = "")
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "1" : "0";
            case byte[] bytes:
                return Hex(bytes);
            case DateTime dt:
                return sqlTypeName.ToLowerInvariant() switch
                {
                    "date"          => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "smalldatetime" => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "datetime"      => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _               => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                };
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString().ToUpperInvariant();
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    public static string Hex(byte[] bytes)
    {
        var len = Math.Min(bytes.Length, MaxBinaryBytes);
        var sb  = new StringBuilder(2 + len * 2 + 1);
        sb.Append("0x").Append(Convert.ToHexString(bytes, 0, len));
        if (bytes.Length > len) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>One-line rendering for grids: control characters become visible symbols.</summary>
    public static string ForGrid(string value)
    {
        if (value.AsSpan().IndexOfAny("\r\n\t") < 0) return value;
        return value.Replace("\r\n", "↵").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', '→');
    }
}
