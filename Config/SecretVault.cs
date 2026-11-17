namespace Sqlr.Config;

/// <summary>Holds connection passwords outside connections.json, one per connection name.</summary>
public interface ISecretVault
{
    /// <returns>The password, or null when none is stored under <paramref name="name"/>.</returns>
    string? Read(string name);

    /// <summary>Stores the password, replacing any stored under the same name.</summary>
    void Write(string name, string secret);

    /// <summary>Removes the password; nothing happens when there is none.</summary>
    void Delete(string name);
}

public static class SecretVault
{
    public const string DisableVariable = "SQLR_NO_KEYCHAIN";

    /// <summary>
    /// The macOS Keychain, unless <c>SQLR_NO_KEYCHAIN=1</c>. Null elsewhere: Windows keeps passwords
    /// DPAPI-encrypted in the config, Linux in plain text in a file only the user can read.
    /// </summary>
    public static ISecretVault? Platform =>
        OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable(DisableVariable) is not ("1" or "true")
            ? MacKeychain.Default
            : null;
}

/// <summary>The vault refused a read or write; the message says why and what to do.</summary>
public sealed class SecretVaultException(string message) : Exception(message);
