using System.Text;
using System.Text.Json;

namespace Sqlr.Config;

public sealed record HistoryEntry(DateTime At, string Connection, string? Database, string Sql, bool Ok, long ElapsedMs, long Rows);

/// <summary>
/// Executed queries, newest last, persisted as JSON lines in <c>~/.sqlr/history.jsonl</c>.
/// </summary>
public sealed class QueryHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<HistoryEntry> _entries = [];
    private readonly int _max;

    private QueryHistory(int max) => _max = max;

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public static QueryHistory Load(int max)
    {
        var h = new QueryHistory(max);
        if (max <= 0) return h;
        try
        {
            if (!File.Exists(SqlrPaths.History)) return h;
            foreach (var line in File.ReadLines(SqlrPaths.History))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions) is { Sql.Length: > 0 } e)
                        h._entries.Add(e);
                }
                catch (JsonException) { /* skip a corrupt line, keep the rest */ }
            }
            if (h._entries.Count > max)
                h._entries.RemoveRange(0, h._entries.Count - max);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // unreadable history — start empty rather than refuse to start
        }
        return h;
    }

    public void Add(HistoryEntry entry)
    {
        if (_max <= 0 || string.IsNullOrWhiteSpace(entry.Sql)) return;

        // Re-running the same query just refreshes its entry instead of stacking duplicates.
        bool replaced = _entries.Count > 0 && _entries[^1].Sql == entry.Sql && _entries[^1].Connection == entry.Connection;
        if (replaced)
            _entries.RemoveAt(_entries.Count - 1);
        _entries.Add(entry);

        try
        {
            SqlrPaths.EnsureRoot();
            if (_entries.Count > _max || replaced)
            {
                if (_entries.Count > _max)
                    _entries.RemoveRange(0, _entries.Count - _max);
                Rewrite();
            }
            else
            {
                File.AppendAllText(SqlrPaths.History, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            /* history is a convenience; never fail a query over it */
        }
    }

    private void Rewrite()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
            sb.Append(JsonSerializer.Serialize(e, JsonOptions)).Append('\n');
        SqlrPaths.WriteAllTextAtomic(SqlrPaths.History, sb.ToString(), privateFile: true);
    }
}

/// <summary>The editor buffer per connection, restored the next time you connect.</summary>
public static class Drafts
{
    private static string PathFor(string connection)
    {
        var safe = string.Concat(connection.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        return Path.Combine(SqlrPaths.Drafts, safe + ".sql");
    }

    public static string Load(string connection)
    {
        try
        {
            var p = PathFor(connection);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch (IOException) { return ""; }
    }

    public static void Save(string connection, string text)
    {
        try
        {
            SqlrPaths.EnsureRoot();
            SqlrPaths.WriteAllTextAtomic(PathFor(connection), text, privateFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
