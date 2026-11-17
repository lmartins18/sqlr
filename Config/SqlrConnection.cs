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

    /// <summary>Plain-text password (macOS/Linux, or configs written by older versions).</summary>
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

    /// <summary>
    /// The usable password. Setting it stores it encrypted on Windows and in plain text elsewhere.
    /// </summary>
    [JsonIgnore]
    public string? Secret
    {
        get
        {
            if (ProtectedPassword is null || !OperatingSystem.IsWindows()) return Password;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Encrypted by another Windows user / machine — the login will fail with a clear error.
                return null;
            }
        }
        set
        {
            if (value is not null && OperatingSystem.IsWindows())
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                ProtectedPassword = Convert.ToBase64String(bytes);
                Password          = null;
            }
            else
            {
                Password          = value;
                ProtectedPassword = null;
            }
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
