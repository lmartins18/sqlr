namespace Sqlr.Config;

public class SqlrConnection
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>"windows" or "sql"</summary>
    public string AuthType { get; set; } = "windows";
    public string? Username { get; set; }
    public string? Password { get; set; }

    public string ConnectionString => AuthType.Equals("sql", StringComparison.OrdinalIgnoreCase)
        ? $"Server={Server};Database={Database};User Id={Username};Password={Password};TrustServerCertificate=true;Connect Timeout=10;"
        : $"Server={Server};Database={Database};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=10;";

    public string DisplayLabel
    {
        get
        {
            var nameCol = Name.PadRight(20);
            var serverDb = $"{Server}/{Database}";
            return $"{nameCol}  {serverDb,-45}  [{AuthType}]";
        }
    }
}
