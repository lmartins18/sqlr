using System.Buffers;
using System.Net;
using System.Net.Security;
using Microsoft.Data.SqlClient;

namespace Sqlr.Db;

/// <summary>
/// Creates and opens every sqlr connection. A connection string with Integrated Security plus a
/// User ID is a "domain" login: it signs in with NTLM using that DOMAIN\user and password
/// (DataGrip's "domain credentials"), so a Windows login works from macOS and Linux too.
/// </summary>
internal static class SqlConnect
{
    // SqlClient keys its pool on the provider instance and shares it across that pool's logins,
    // so two handshakes must never interleave on it. Pooled reopens skip the handshake entirely.
    private static readonly SemaphoreSlim LoginGate = new(1, 1);

    public static SqlConnection Create(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        if (IsDomainLogin(connectionString)) conn.SspiContextProvider = NtlmLogin.Instance;
        return conn;
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var conn = Create(connectionString);
        try
        {
            await OpenAsync(conn, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static async Task OpenAsync(SqlConnection conn, CancellationToken ct = default)
    {
        if (conn.SspiContextProvider is not NtlmLogin)
        {
            await conn.OpenAsync(ct);
            return;
        }
        await LoginGate.WaitAsync(ct);
        try { await conn.OpenAsync(ct); }
        finally { LoginGate.Release(); }
    }

    public static void Open(SqlConnection conn)
    {
        if (conn.SspiContextProvider is not NtlmLogin)
        {
            conn.Open();
            return;
        }
        LoginGate.Wait();
        try { conn.Open(); }
        finally { LoginGate.Release(); }
    }

    internal static bool IsDomainLogin(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        return b.IntegratedSecurity && b.UserID.Length > 0;
    }
}

/// <summary>NTLM handshake with the User ID / Password of the connection string.</summary>
internal sealed class NtlmLogin : SspiContextProvider
{
    public static readonly NtlmLogin Instance = new();

    private NegotiateAuthentication? _auth;

    protected override bool GenerateContext(ReadOnlySpan<byte> incomingBlob, IBufferWriter<byte> outgoingBlobWriter, SspiAuthenticationParameters authParams) =>
        Step(incomingBlob, outgoingBlobWriter, authParams.UserId, authParams.Password, authParams.Resource);

    internal bool Step(ReadOnlySpan<byte> incomingBlob, IBufferWriter<byte> outgoingBlobWriter, string? userId, string? password, string? targetName)
    {
        if (incomingBlob.IsEmpty || _auth is null)
        {
            _auth?.Dispose();
            _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package    = "NTLM",
                Credential = Credential(userId, password),
                TargetName = targetName
            });
        }

        var blob = _auth.GetOutgoingBlob(incomingBlob, out var status);
        if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
            return false;
        if (blob is not null) outgoingBlobWriter.Write(blob);
        if (status == NegotiateAuthenticationStatusCode.Completed)
        {
            _auth.Dispose();
            _auth = null;
        }
        return true;
    }

    /// <summary><c>DOMAIN\user</c> splits into user + domain; <c>user@domain</c> goes as is.</summary>
    internal static NetworkCredential Credential(string? userId, string? password)
    {
        var user  = userId ?? "";
        var slash = user.IndexOf('\\');
        return slash > 0
            ? new NetworkCredential(user[(slash + 1)..], password, user[..slash])
            : new NetworkCredential(user, password);
    }
}
