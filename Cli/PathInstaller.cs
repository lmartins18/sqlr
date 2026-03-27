using System.Runtime.InteropServices;

namespace Sqlr.Cli;

public static class PathInstaller
{
    public static void Install()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? Directory.GetCurrentDirectory();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            InstallWindows(exeDir);
        else
            InstallUnix(exeDir);
    }

    private static void InstallWindows(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var paths = current.Split(';', StringSplitOptions.RemoveEmptyEntries);

        var alreadyPresent = paths.Any(p =>
            p.TrimEnd('\\', '/').Equals(dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (alreadyPresent)
        {
            Console.WriteLine($"Already in PATH: {dir}");
            return;
        }

        var updated = string.IsNullOrEmpty(current) ? dir : $"{current};{dir}";
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        Console.WriteLine($"Added to PATH: {dir}");
        Console.WriteLine("Restart your terminal for the change to take effect.");
    }

    private static void InstallUnix(string dir)
    {
        var exportLine = $"\nexport PATH=\"$PATH:{dir}\"";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var rc in new[] { ".bashrc", ".zshrc" })
        {
            var rcPath = Path.Combine(home, rc);
            var content = File.Exists(rcPath) ? File.ReadAllText(rcPath) : "";

            if (content.Contains(dir))
            {
                Console.WriteLine($"~/{rc}: already contains entry, skipping.");
                continue;
            }

            File.AppendAllText(rcPath, exportLine);
            Console.WriteLine($"Appended to ~/{rc}");
        }

        Console.WriteLine();
        Console.WriteLine("To apply immediately, run:");
        Console.WriteLine("  source ~/.bashrc   # or ~/.zshrc");
    }
}
