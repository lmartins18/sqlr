namespace Sqlr.Cli;

public enum CliCommand
{
    LaunchPicker,
    Connect,
    AddToPath,
    ConnectionsList,
    ConnectionsAdd,
    ConnectionsRemove,
    ConnectionsTest
}

public sealed class Args
{
    public CliCommand Command { get; init; }
    public string? ConnectionName { get; init; }

    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0)
            return new Args { Command = CliCommand.LaunchPicker };

        return argv[0].ToLowerInvariant() switch
        {
            "--add-to-path" =>
                new Args { Command = CliCommand.AddToPath },

            "-c" when argv.Length > 1 =>
                new Args { Command = CliCommand.Connect, ConnectionName = argv[1] },

            "-c" =>
                new Args { Command = CliCommand.LaunchPicker },

            "connections" when argv.Length == 1 =>
                new Args { Command = CliCommand.ConnectionsList },

            "connections" when argv.Length > 1 && argv[1].Equals("add", StringComparison.OrdinalIgnoreCase) =>
                new Args { Command = CliCommand.ConnectionsAdd },

            "connections" when argv.Length > 2 && argv[1].Equals("remove", StringComparison.OrdinalIgnoreCase) =>
                new Args { Command = CliCommand.ConnectionsRemove, ConnectionName = argv[2] },

            "connections" when argv.Length > 2 && argv[1].Equals("test", StringComparison.OrdinalIgnoreCase) =>
                new Args { Command = CliCommand.ConnectionsTest, ConnectionName = argv[2] },

            _ => new Args { Command = CliCommand.LaunchPicker }
        };
    }
}
