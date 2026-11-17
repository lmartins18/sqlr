using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Sqlr.Config;

/// <summary>
/// Passwords as generic-password items in the macOS login keychain, through Security.framework's
/// SecItem API: service "sqlr", account = connection name, so they show up in Keychain Access and
/// in <c>security find-generic-password -s sqlr -a NAME -w</c>.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychain(string service) : ISecretVault
{
    public static readonly MacKeychain Default = new("sqlr");

    public string Service { get; } = service;

    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const int ErrSecAuthFailed = -25293;
    private const int ErrUserCanceled = -128;
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    public string? Read(string name)
    {
        using var cf = new CfScope();
        var query = cf.Dictionary(
            (Native.Class, Native.GenericPassword), (Native.Service, cf.String(Service)), (Native.Account, cf.String(name)),
            (Native.ReturnData, Native.True), (Native.MatchLimit, Native.MatchLimitOne));

        var status = SecItemCopyMatching(query, out var data);
        if (status == ErrSecItemNotFound) return null;
        Check(status, $"read the password for '{name}'");
        cf.Own(data);

        var bytes = new byte[CFDataGetLength(data)];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Write(string name, string secret)
    {
        using var cf = new CfScope();
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var data = cf.Data(bytes);
            var status = SecItemAdd(cf.Dictionary(
                (Native.Class, Native.GenericPassword), (Native.Service, cf.String(Service)), (Native.Account, cf.String(name)),
                (Native.Label, cf.String($"{Service}: {name}")), (Native.ValueData, data)), IntPtr.Zero);
            if (status == ErrSecDuplicateItem)
                status = SecItemUpdate(Match(cf, name), cf.Dictionary((Native.ValueData, data)));
            Check(status, $"save the password for '{name}'");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Delete(string name)
    {
        using var cf = new CfScope();
        var status = SecItemDelete(Match(cf, name));
        if (status != ErrSecItemNotFound) Check(status, $"delete the password for '{name}'");
    }

    private IntPtr Match(CfScope cf, string name) => cf.Dictionary(
        (Native.Class, Native.GenericPassword), (Native.Service, cf.String(Service)), (Native.Account, cf.String(name)));

    private static void Check(int status, string what)
    {
        if (status == 0) return;
        var hint = status switch
        {
            ErrSecInteractionNotAllowed => " The keychain is locked: over SSH run `security unlock-keychain` first, or set SQLR_NO_KEYCHAIN=1.",
            ErrSecAuthFailed or ErrUserCanceled => " Allow sqlr when macOS asks for the keychain.",
            _ => ""
        };
        throw new SecretVaultException($"Keychain: couldn't {what}: {Message(status)} ({status}).{hint}");
    }

    private static string Message(int status)
    {
        var s = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (s == IntPtr.Zero) return "unknown error";
        try
        {
            var buffer = new byte[CFStringGetLength(s) * 4 + 1];
            if (!CFStringGetCString(s, buffer, buffer.Length, Utf8)) return "unknown error";
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
        finally { CFRelease(s); }
    }

    /// <summary>Releases every Core Foundation object it creates or is handed.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr Own(IntPtr cf)
        {
            if (cf == IntPtr.Zero) throw new SecretVaultException("Keychain: Core Foundation ran out of memory.");
            _owned.Add(cf);
            return cf;
        }

        public IntPtr String(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            return Own(CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8, false));
        }

        public IntPtr Data(byte[] bytes) => Own(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs) => Own(CFDictionaryCreate(
            IntPtr.Zero, pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(), pairs.Length,
            Native.KeyCallBacks, Native.ValueCallBacks));

        public void Dispose()
        {
            foreach (var cf in _owned) CFRelease(cf);
        }
    }

    /// <summary>The frameworks' exported constants, loaded on first use.</summary>
    private static class Native
    {
        private static readonly IntPtr Security = NativeLibrary.Load(SecurityLib);
        private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationLib);

        public static readonly IntPtr Class = Constant(Security, "kSecClass");
        public static readonly IntPtr GenericPassword = Constant(Security, "kSecClassGenericPassword");
        public static readonly IntPtr Service = Constant(Security, "kSecAttrService");
        public static readonly IntPtr Account = Constant(Security, "kSecAttrAccount");
        public static readonly IntPtr Label = Constant(Security, "kSecAttrLabel");
        public static readonly IntPtr ValueData = Constant(Security, "kSecValueData");
        public static readonly IntPtr ReturnData = Constant(Security, "kSecReturnData");
        public static readonly IntPtr MatchLimit = Constant(Security, "kSecMatchLimit");
        public static readonly IntPtr MatchLimitOne = Constant(Security, "kSecMatchLimitOne");
        public static readonly IntPtr True = Constant(CoreFoundation, "kCFBooleanTrue");

        // The callback structs themselves are exported, so CFDictionaryCreate takes their address.
        public static readonly IntPtr KeyCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr ValueCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        // The other symbols are CFStringRef / CFBooleanRef variables: read the pointer they hold.
        private static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    [DllImport(SecurityLib)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityLib)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(SecurityLib)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFStringCreateWithBytes(
        IntPtr allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr str, [Out] byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);
}
