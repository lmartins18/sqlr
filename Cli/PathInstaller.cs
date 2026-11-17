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
        var exportLine = $"\n# added by sqlr --add-to-path\nexport PATH=\"$PATH:{dir}\"\n";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Update the rc files that exist; if none do, create the one for the current shell.
        var candidates = new[] { ".zshrc", ".bashrc", ".bash_profile" };
        var targets    = candidates.Where(rc => File.Exists(Path.Combine(home, rc))).ToList();
        if (targets.Count == 0)
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "";
            targets.Add(shell.EndsWith("zsh") ? ".zshrc" : OperatingSystem.IsMacOS() ? ".bash_profile" : ".bashrc");
        }

        foreach (var rc in targets)
        {
            var rcPath  = Path.Combine(home, rc);
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
        Console.WriteLine($"  source ~/{targets[0]}");
    }
}
