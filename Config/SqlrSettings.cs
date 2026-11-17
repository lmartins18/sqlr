using System.Text.Json;

namespace Sqlr.Config;

/// <summary>Optional user settings, read from <c>~/.sqlr/settings.json</c>.</summary>
public sealed class SqlrSettings
{
    /// <summary>Rows kept per result set; the rest are streamed past so later statements still run.</summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>Per-batch command timeout. 0 = no timeout (cancel with Esc instead).</summary>
    public int QueryTimeoutSeconds { get; set; } = 0;

    /// <summary>Number of query history entries kept on disk.</summary>
    public int HistorySize { get; set; } = 2_000;

    /// <summary>Widest a results column is drawn before truncating with "…" (Enter shows the full value).</summary>
    public int MaxColumnWidth { get; set; } = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true
    };

    public static SqlrSettings Load()
    {
        try
        {
            if (File.Exists(SqlrPaths.Settings))
            {
                var s = JsonSerializer.Deserialize<SqlrSettings>(File.ReadAllText(SqlrPaths.Settings), JsonOptions);
                if (s is not null) return s.Normalized();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: ignoring invalid {SqlrPaths.Settings}: {ex.Message}");
        }
        return new SqlrSettings();
    }

    private SqlrSettings Normalized()
    {
        MaxRows             = MaxRows <= 0 ? 100_000 : MaxRows;
        QueryTimeoutSeconds = Math.Max(0, QueryTimeoutSeconds);
        HistorySize         = Math.Clamp(HistorySize, 0, 100_000);
        MaxColumnWidth      = Math.Clamp(MaxColumnWidth, 8, 500);
        return this;
    }
}
