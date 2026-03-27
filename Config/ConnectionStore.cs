using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sqlr.Config;

public class ConnectionStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlr");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<SqlrConnection> Connections { get; set; } = [];

    public static ConnectionStore Load()
    {
        if (!File.Exists(ConfigFile))
            return new ConnectionStore();

        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<ConnectionStore>(json, JsonOptions) ?? new ConnectionStore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not load {ConfigFile}: {ex.Message}");
            return new ConnectionStore();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigFile, json);
    }

    public void Add(SqlrConnection conn)
    {
        Connections.RemoveAll(c => c.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
        Connections.Add(conn);
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
