using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Text;
using Microsoft.Data.SqlClient;
using Sqlr.Config;
using Sqlr.Db;
using Xunit;

namespace Sqlr.Tests;

public class DomainLoginTests
{
    private static SqlrConnection Domain(string user = @"CORP\jdoe")
    {
        var c = new SqlrConnection { Name = "d", Server = "sql01.corp.local", Database = "Sales", AuthType = "domain", Username = user };
        c.Secret = "p;a=ss";
        return c;
    }

    [Fact]
    public void Domain_connection_string_is_integrated_security_with_the_user_and_password()
    {
        var b = new SqlConnectionStringBuilder(Domain().ConnectionString);

        Assert.True(b.IntegratedSecurity);
        Assert.Equal(@"CORP\jdoe", b.UserID);
        Assert.Equal("p;a=ss", b.Password);
        Assert.Equal("Sales", b.InitialCatalog);
    }

    [Fact]
    public void Only_domain_connections_get_the_ntlm_login()
    {
        var sql = new SqlrConnection { Name = "s", Server = "srv", AuthType = "sql", Username = "sa" };
        var win = new SqlrConnection { Name = "w", Server = "srv" };

        using var d = SqlConnect.Create(Domain().ConnectionString);
        using var s = SqlConnect.Create(sql.ConnectionString);
        using var w = SqlConnect.Create(win.ConnectionString);

        Assert.Same(NtlmLogin.Instance, d.SspiContextProvider);
        Assert.Null(s.SspiContextProvider);
        Assert.Null(w.SspiContextProvider);
    }

    [Fact]
    public void Labels_show_the_domain_user()
    {
        var c = Domain();
        Assert.True(c.UsesPassword);
        Assert.Equal(@"domain:CORP\jdoe", c.AuthLabel);
        Assert.Contains(@"[domain:CORP\jdoe]", c.DisplayLabel);
        Assert.Equal("windows", new SqlrConnection().AuthLabel);
    }

    [Theory]
    [InlineData(@"CORP\jdoe", true)]
    [InlineData("jdoe@corp.local", true)]
    [InlineData("jdoe", false)]
    [InlineData(@"\lmartins", false)]
    [InlineData(@"CORP\", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Domain_user_needs_a_domain(string? user, bool ok) =>
        Assert.Equal(ok, SqlrConnection.IsDomainUser(user));

    [Fact]
    public void Credential_splits_domain_and_keeps_upn()
    {
        var down = NtlmLogin.Credential(@"CORP\jdoe", "pw");
        Assert.Equal("jdoe", down.UserName);
        Assert.Equal("CORP", down.Domain);
        Assert.Equal("pw", down.Password);

        var upn = NtlmLogin.Credential("jdoe@corp.local", "pw");
        Assert.Equal("jdoe@corp.local", upn.UserName);
        Assert.Equal("", upn.Domain);
    }

    [Fact]
    public void Handshake_sends_ntlm_negotiate_then_answers_the_challenge()
    {
        var login = new NtlmLogin();
        const string spn = "MSSQLSvc/sql01.corp.local:1433";

        var first = new ArrayBufferWriter<byte>();
        Assert.True(login.Step([], first, @"CORP\jdoe", "pw", spn));
        Assert.Equal(1, NtlmMessageType(first.WrittenSpan));

        // A real NTLM server produces the challenge; its answer to our type 3 is the server's job.
        using var server = new NegotiateAuthentication(new NegotiateAuthenticationServerOptions { Package = "NTLM" });
        var challenge = server.GetOutgoingBlob(first.WrittenSpan, out var serverStatus);
        Assert.Equal(NegotiateAuthenticationStatusCode.ContinueNeeded, serverStatus);
        Assert.Equal(2, NtlmMessageType(challenge));

        var answer = new ArrayBufferWriter<byte>();
        Assert.True(login.Step(challenge, answer, @"CORP\jdoe", "pw", spn));
        Assert.Equal(3, NtlmMessageType(answer.WrittenSpan));

        // A new login starts over with a fresh negotiate.
        var again = new ArrayBufferWriter<byte>();
        Assert.True(login.Step([], again, @"CORP\jdoe", "pw", spn));
        Assert.Equal(1, NtlmMessageType(again.WrittenSpan));
    }

    private static int NtlmMessageType(ReadOnlySpan<byte> blob)
    {
        Assert.True(blob.Length >= 12, "blob too short for NTLM");
        Assert.Equal("NTLMSSP\0", Encoding.ASCII.GetString(blob[..8]));
        return BinaryPrimitives.ReadInt32LittleEndian(blob[8..12]);
    }
}
