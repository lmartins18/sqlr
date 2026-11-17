using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sqlr.Config;

public class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true
    };

    public List<SqlrConnection> Connections { get; set; } = [];

    public static ConnectionStore Load()
    {
        var file = SqlrPaths.Connections;
        if (!File.Exists(file))
            return new ConnectionStore();

        ConnectionStore store;
        try
        {
            store = JsonSerializer.Deserialize<ConnectionStore>(File.ReadAllText(file), JsonOptions) ?? new ConnectionStore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not load {file}: {ex.Message}");
            return new ConnectionStore();
        }

        store.Connections.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));

        // On Windows, move any plain-text password from older configs into DPAPI storage.
        if (OperatingSystem.IsWindows() && store.Connections.Any(c => c.Password is not null))
        {
            foreach (var c in store.Connections.Where(c => c.Password is not null))
                c.Secret = c.Password;
            try { store.Save(); } catch { /* read-only config is still usable */ }
        }

        return store;
    }

    public void Save()
    {
        SqlrPaths.EnsureRoot();
        SqlrPaths.WriteAllTextAtomic(SqlrPaths.Connections, JsonSerializer.Serialize(this, JsonOptions), privateFile: true);
    }

    public void Add(SqlrConnection conn)
    {
        Connections.RemoveAll(c => c.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
        Connections.Add(conn);
        Save();
    }

    /// <summary>Replaces <paramref name="originalName"/> in place (keeps list order), supporting renames.</summary>
    public void Update(string originalName, SqlrConnection conn)
    {
        var idx = Connections.FindIndex(c => c.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { Add(conn); return; }

        // A rename onto another existing name replaces that entry.
        var original = Connections[idx];
        Connections.RemoveAll(c => !ReferenceEquals(c, original)
                                   && c.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
        Connections[Connections.IndexOf(original)] = conn;
        Save();
    }

    public bool Remove(string name)
    {
        var removed = Connections.RemoveAll(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public SqlrConnection? Find(string name) =>
        Connections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
