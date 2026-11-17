using System.Runtime.Versioning;
using Microsoft.Data.SqlClient;
using Sqlr.Config;
using Xunit;

namespace Sqlr.Tests;

[CollectionDefinition("Config file", DisableParallelization = true)]
public class ConfigFileCollection;

/// <summary>Connection passwords kept in a vault (the macOS Keychain) instead of connections.json.</summary>
[Collection("Config file")]
public sealed class SecretVaultTests
{
    private readonly FakeVault _vault = new();

    public SecretVaultTests() => File.Delete(SqlrPaths.Connections);

    private static SqlrConnection Sql(string name, string? password = null)
    {
        var c = new SqlrConnection { Name = name, Server = "sql01.corp.local", AuthType = "sql", Username = "app" };
        if (password is not null) c.Secret = password;
        return c;
    }

    private static string ConfigText => File.ReadAllText(SqlrPaths.Connections);

    [Fact]
    public void The_password_goes_to_the_vault_and_is_read_only_when_needed()
    {
        ConnectionStore.Load(_vault).Add(Sql("prod", "hunter2"));

        Assert.Equal("hunter2", _vault.Items["prod"]);
        Assert.DoesNotContain("hunter2", ConfigText);
        Assert.DoesNotContain("assword", ConfigText);

        var conn = ConnectionStore.Load(_vault).Find("prod")!;
        Assert.Equal(0, _vault.Reads);
        Assert.Equal("hunter2", new SqlConnectionStringBuilder(conn.ConnectionString).Password);
        Assert.Equal("hunter2", conn.Secret);
        Assert.Equal(1, _vault.Reads);
    }

    [Fact]
    public void Rename_moves_the_password_and_dropping_it_removes_it()
    {
        var store = ConnectionStore.Load(_vault);
        store.Add(Sql("a", "hunter2"));

        store.Update("a", Sql("b"));                      // password left blank: kept
        Assert.Equal(["b"], _vault.Items.Keys);
        Assert.Equal("hunter2", _vault.Items["b"]);

        store.Update("b", Sql("B"));                      // the Keychain is case-sensitive
        Assert.Equal(["B"], _vault.Items.Keys);

        store.Update("B", new SqlrConnection { Name = "B", Server = "s" }); // now Windows auth
        Assert.Empty(_vault.Items);
        Assert.Equal("windows", ConnectionStore.Load(_vault).Find("b")!.AuthLabel);
    }

    [Fact]
    public void Remove_and_replace_delete_the_old_password()
    {
        var store = ConnectionStore.Load(_vault);
        store.Add(Sql("Prod", "1"));
        store.Add(Sql("dev", "2"));

        Assert.True(store.Remove("PROD"));
        Assert.Equal(["dev"], _vault.Items.Keys);

        store.Add(new SqlrConnection { Name = "DEV", Server = "s" }); // replaces "dev", no password
        Assert.Empty(_vault.Items);
    }

    [Fact]
    public void A_blank_password_stores_nothing()
    {
        ConnectionStore.Load(_vault).Add(Sql("local", ""));

        Assert.Empty(_vault.Items);
        Assert.Equal("", new SqlConnectionStringBuilder(ConnectionStore.Load(_vault).Find("local")!.ConnectionString).Password);
    }

    [Fact]
    public void Plain_text_passwords_from_older_configs_move_into_the_vault()
    {
        File.WriteAllText(SqlrPaths.Connections,
            """{ "connections": [ { "name": "old", "server": "s", "authType": "sql", "username": "u", "password": "hunter2" } ] }""");

        var conn = ConnectionStore.Load(_vault).Find("old")!;

        Assert.Equal("hunter2", _vault.Items["old"]);
        Assert.DoesNotContain("hunter2", ConfigText);
        Assert.Equal("hunter2", conn.Secret);
    }

    [Fact]
    public void A_refusing_vault_leaves_plain_text_passwords_working()
    {
        File.WriteAllText(SqlrPaths.Connections,
            """{ "connections": [ { "name": "old", "server": "s", "authType": "sql", "username": "u", "password": "hunter2" } ] }""");
        _vault.Refuse = true;

        var conn = ConnectionStore.Load(_vault).Find("old")!;

        Assert.Contains("hunter2", ConfigText);
        Assert.Equal("hunter2", conn.Secret);
    }

    [Fact]
    public void A_failed_save_changes_nothing()
    {
        var store = ConnectionStore.Load(_vault);
        store.Add(Sql("a", "1"));
        _vault.Refuse = true;

        Assert.Throws<SecretVaultException>(() => store.Add(Sql("b", "2")));
        Assert.Throws<SecretVaultException>(() => store.Update("a", Sql("a2", "3")));

        Assert.Equal(["a"], store.Connections.Select(c => c.Name));
        Assert.Equal(["a"], ConnectionStore.Load(_vault).Connections.Select(c => c.Name));
    }

    [Fact]
    public void A_failed_read_says_why()
    {
        ConnectionStore.Load(_vault).Add(Sql("prod", "hunter2"));
        _vault.Refuse = true;

        var conn = ConnectionStore.Load(_vault).Find("prod")!;
        var ex = Assert.Throws<SecretVaultException>(() => conn.ConnectionString);
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public void Without_a_vault_passwords_stay_in_the_config_as_before()
    {
        ConnectionStore.Load(vault: null).Add(Sql("w", "hunter2"));

        Assert.Contains(OperatingSystem.IsWindows() ? "protectedPassword" : "\"password\": \"hunter2\"", ConfigText);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("hunter2", ConfigText);
        Assert.Equal("hunter2", ConnectionStore.Load(vault: null).Find("w")!.Secret);
    }

    /// <summary>Real Keychain round trip under a throwaway service; runs on macOS only.</summary>
    [MacFact]
    [SupportedOSPlatform("macos")]
    public void Keychain_round_trip()
    {
        var keychain = new MacKeychain("sqlr-tests");
        var name = "round-trip-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(keychain.Read(name));
            keychain.Write(name, "p;ä=ss 🔑");
            Assert.Equal("p;ä=ss 🔑", keychain.Read(name));
            keychain.Write(name, "changed");
            Assert.Equal("changed", keychain.Read(name));
            keychain.Delete(name);
            Assert.Null(keychain.Read(name));
            keychain.Delete(name);
        }
        finally
        {
            keychain.Delete(name);
        }
    }

    private sealed class FakeVault : ISecretVault
    {
        public SortedDictionary<string, string> Items { get; } = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public bool Refuse { get; set; }

        public string? Read(string name)
        {
            Fail();
            Reads++;
            return Items.GetValueOrDefault(name);
        }

        public void Write(string name, string secret)
        {
            Fail();
            Items[name] = secret;
        }

        public void Delete(string name)
        {
            Fail();
            Items.Remove(name);
        }

        private void Fail()
        {
            if (Refuse) throw new SecretVaultException("Keychain: the keychain is locked.");
        }
    }
}

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Uses the macOS Keychain.";
    }
}
