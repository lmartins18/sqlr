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

    /// <summary>Where passwords go instead of the file: the macOS Keychain, or null to keep them in it.</summary>
    [JsonIgnore]
    public ISecretVault? Vault { get; set; } = SecretVault.Platform;

    public static ConnectionStore Load() => Load(SecretVault.Platform);

    public static ConnectionStore Load(ISecretVault? vault)
    {
        var file = SqlrPaths.Connections;
        if (!File.Exists(file))
            return new ConnectionStore { Vault = vault };

        ConnectionStore store;
        try
        {
            store = JsonSerializer.Deserialize<ConnectionStore>(File.ReadAllText(file), JsonOptions) ?? new ConnectionStore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not load {file}: {ex.Message}");
            return new ConnectionStore { Vault = vault };
        }

        store.Vault = vault;
        store.Connections.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));
        foreach (var c in store.Connections) c.Vault = vault;

        // Move plain-text passwords from older configs into the Keychain (macOS) or DPAPI (Windows).
        if (vault is not null || OperatingSystem.IsWindows())
        {
            var moved = false;
            foreach (var c in store.Connections.Where(c => c.Password is not null))
            {
                try
                {
                    c.Secret = c.Password;
                    c.StoreSecret(vault);
                    moved = true;
                }
                catch (SecretVaultException ex)
                {
                    SqlrPaths.LogError(ex); // stays in the file and works as before; retried next start
                }
            }
            if (moved)
            {
                try { store.Save(); } catch { /* read-only config is still usable */ }
            }
        }

        return store;
    }

    public void Save()
    {
        SqlrPaths.EnsureRoot();
        SqlrPaths.WriteAllTextAtomic(SqlrPaths.Connections, JsonSerializer.Serialize(this, JsonOptions), privateFile: true);
    }

    /// <summary>
    /// Adds <paramref name="conn"/>, replacing any connection with its name. A password set on it is
    /// stored first; if that fails (<see cref="SecretVaultException"/>) nothing changes.
    /// </summary>
    public void Add(SqlrConnection conn) => Commit(conn, () =>
    {
        Connections.RemoveAll(c => c.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
        Connections.Add(conn);
    });

    /// <summary>
    /// Replaces <paramref name="originalName"/> in place (keeps list order), supporting renames.
    /// Unless a new password was set on <paramref name="conn"/>, the stored one is kept (and moved
    /// to the new name on a rename).
    /// </summary>
    public void Update(string originalName, SqlrConnection conn)
    {
        var original = Find(originalName);
        if (original is null) { Add(conn); return; }
        if (conn.UsesPassword && !conn.SecretChanged) conn.Secret = original.Secret;

        Commit(conn, () =>
        {
            // A rename onto another existing name replaces that entry.
            Connections.RemoveAll(c => !ReferenceEquals(c, original)
                                       && c.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
            Connections[Connections.IndexOf(original)] = conn;
        });
    }

    public bool Remove(string name)
    {
        var gone = Connections.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (gone.Count == 0) return false;
        Connections.RemoveAll(gone.Contains);
        Save();
        foreach (var c in gone) ForgetSecret(c.Name);
        return true;
    }

    private void Commit(SqlrConnection conn, Action change)
    {
        conn.StoreSecret(Vault);
        var before = Connections.Select(c => c.Name).ToList();
        change();
        Save();

        // Passwords of names that are gone (renamed or replaced), and of a connection that no longer has one.
        foreach (var name in before.Except(Connections.Select(c => c.Name), StringComparer.Ordinal))
            ForgetSecret(name);
        if (!conn.UsesPassword) ForgetSecret(conn.Name);
    }

    /// <summary>Best effort: a password left behind in the vault is harmless.</summary>
    private void ForgetSecret(string name)
    {
        try { Vault?.Delete(name); }
        catch (SecretVaultException ex) { SqlrPaths.LogError(ex); }
    }

    public SqlrConnection? Find(string name) =>
        Connections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
