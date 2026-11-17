using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Sqlr.Config;

public class SqlrConnection
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    /// <summary>Initial database. Empty = the login's default database.</summary>
    public string Database { get; set; } = "";
    /// <summary>"windows", "sql", or "domain" (DOMAIN\user + password over NTLM, for macOS/Linux)</summary>
    public string AuthType { get; set; } = "windows";
    public string? Username { get; set; }

    /// <summary>
    /// Plain-text password: Linux, macOS with SQLR_NO_KEYCHAIN=1, or configs written by older versions
    /// (moved to the Keychain / DPAPI when the store loads).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>DPAPI-encrypted password, base64 (Windows only — tied to the current Windows user).</summary>
    public string? ProtectedPassword { get; set; }

    public bool TrustServerCertificate { get; set; } = true;

    [JsonIgnore]
    public bool IsSqlAuth => AuthType.Equals("sql", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsDomainAuth => AuthType.Equals("domain", StringComparison.OrdinalIgnoreCase);

    /// <summary>SQL and domain logins carry a username and password.</summary>
    [JsonIgnore]
    public bool UsesPassword => IsSqlAuth || IsDomainAuth;

    [JsonIgnore]
    public string AuthLabel => IsSqlAuth ? $"sql:{Username}" : IsDomainAuth ? $"domain:{Username}" : "windows";

    /// <summary>A domain login names its domain: <c>DOMAIN\user</c> or <c>user@domain</c>.</summary>
    public static bool IsDomainUser(string? user)
    {
        var i = user?.IndexOfAny(['\\', '@']) ?? -1;
        return i > 0 && i < user!.Length - 1;
    }

    /// <summary>Where this connection's password lives when it isn't in the file (the macOS Keychain).</summary>
    internal ISecretVault? Vault { get; set; }

    private string? _secret;
    private bool _secretKnown;

    /// <summary>A password was set and not yet stored.</summary>
    internal bool SecretChanged { get; private set; }

    /// <summary>
    /// The usable password. Setting it only changes this object: <see cref="ConnectionStore"/> stores
    /// it on Add / Update (Keychain on macOS, DPAPI on Windows, plain text on Linux). Reading it may
    /// fetch it from the Keychain, which throws <see cref="SecretVaultException"/> when access fails.
    /// </summary>
    [JsonIgnore]
    public string? Secret
    {
        get
        {
            if (_secretKnown) return _secret;
            if (ProtectedPassword is not null && OperatingSystem.IsWindows()) return Unprotect(ProtectedPassword);
            if (Password is not null || Vault is null || !UsesPassword) return Password;

            _secret = Vault.Read(Name);
            _secretKnown = true;
            return _secret;
        }
        set
        {
            _secret = value;
            _secretKnown = true;
            SecretChanged = true;
        }
    }

    /// <summary>
    /// Stores a password set since the last call where it belongs: in <paramref name="vault"/> when
    /// there is one, otherwise in this object's DPAPI / plain-text field. Without a password (none
    /// needed, or blank) the vault entry is removed.
    /// </summary>
    internal void StoreSecret(ISecretVault? vault)
    {
        Vault = vault;
        if (!SecretChanged) return;

        var secret = UsesPassword && !string.IsNullOrEmpty(_secret) ? _secret : null;
        if (vault is not null)
        {
            if (secret is null) vault.Delete(Name);
            else vault.Write(Name, secret);
            Password = null;
            ProtectedPassword = null;
        }
        else if (secret is not null && OperatingSystem.IsWindows())
        {
            ProtectedPassword = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
            Password = null;
        }
        else
        {
            Password = secret;
            ProtectedPassword = null;
        }
        SecretChanged = false;
    }

    [SupportedOSPlatform("windows")]
    private static string? Unprotect(string protectedPassword)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted by another Windows user / machine — the login will fail with a clear error.
            return null;
        }
    }

    /// <summary>
    /// Builds the connection string with <see cref="SqlConnectionStringBuilder"/> so values containing
    /// <c>;</c>, <c>=</c> or quotes (common in passwords) are escaped correctly.
    /// </summary>
    public string BuildConnectionString(string? database = null)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource             = Server,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout         = 10,
            ApplicationName        = "sqlr"
        };

        var db = database ?? Database;
        if (!string.IsNullOrWhiteSpace(db)) b.InitialCatalog = db;

        if (UsesPassword)
        {
            b.UserID   = Username ?? "";
            b.Password = Secret ?? "";
        }
        // Domain = Integrated Security with an explicit user; SqlConnect signs it in with NTLM.
        if (!IsSqlAuth) b.IntegratedSecurity = true;

        return b.ConnectionString;
    }

    [JsonIgnore]
    public string ConnectionString => BuildConnectionString();

    [JsonIgnore]
    public string DatabaseLabel => string.IsNullOrWhiteSpace(Database) ? "(default)" : Database;

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var nameCol  = Name.PadRight(20);
            var serverDb = $"{Server}/{DatabaseLabel}";
            return $"{nameCol}  {serverDb,-45}  [{AuthLabel}]";
        }
    }
}
