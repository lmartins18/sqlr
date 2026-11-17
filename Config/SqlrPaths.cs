using System.Runtime.Versioning;
using System.Text;

namespace Sqlr.Config;

/// <summary>
/// Locations of everything sqlr persists. Defaults to <c>~/.sqlr</c>; override with the
/// <c>SQLR_HOME</c> environment variable (used by the tests so they never touch real config).
/// </summary>
public static class SqlrPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("SQLR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlr");

    public static string Connections => Path.Combine(Root, "connections.json");
    public static string Settings    => Path.Combine(Root, "settings.json");
    public static string History     => Path.Combine(Root, "history.jsonl");
    public static string Drafts      => Path.Combine(Root, "drafts");
    public static string ErrorLog    => Path.Combine(Root, "error.log");
    public static string ThemeFile   => Path.Combine(Root, "theme");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows())
            TrySetMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Writes via a temp file + rename so a crash mid-write can never leave a truncated file.
    /// <paramref name="privateFile"/> restricts the file to the current user on Unix (0600).
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, bool privateFile = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (privateFile && !OperatingSystem.IsWindows())
            TrySetMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    public static void LogError(Exception ex)
    {
        try
        {
            EnsureRoot();
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    [UnsupportedOSPlatform("windows")]
    private static void TrySetMode(string path, UnixFileMode mode)
    {
        try { File.SetUnixFileMode(path, mode); }
        catch { /* best effort — e.g. filesystems without permissions */ }
    }
}
