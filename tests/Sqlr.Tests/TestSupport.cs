using System.Runtime.CompilerServices;
using Sqlr.Db;

namespace Sqlr.Tests;

internal static class TestSetup
{
    /// <summary>Point all persisted state (connections, history, drafts) at a throwaway folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var home = Path.Combine(Path.GetTempPath(), "sqlr-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("SQLR_HOME", home);
        Environment.SetEnvironmentVariable(Sqlr.Config.SecretVault.DisableVariable, "1"); // never the real Keychain
    }
}

internal static class FakeSchema
{
    public static DatabaseSchema Build()
    {
        var s = new DatabaseSchema { DatabaseName = "SqlrTest" };
        s.Schemas.AddRange(["archive", "dbo", "sales", "sys"]);
        s.Databases.AddRange(["master", "SqlrOther", "SqlrTest"]);
        s.Tables.AddRange(
        [
            new TableInfo("sales",   "Customers",       false),
            new TableInfo("sales",   "Orders",          false),
            new TableInfo("archive", "Orders",          false),
            new TableInfo("dbo",     "Order Details",   false),
            new TableInfo("sales",   "ActiveCustomers", true),
            new TableInfo("sys",     "objects",         true, IsSystem: true),
        ]);
        s.Columns.AddRange(
        [
            new ColumnInfo("sales",   "Customers",       "CustomerId",      "int"),
            new ColumnInfo("sales",   "Customers",       "Name",            "nvarchar(100)"),
            new ColumnInfo("sales",   "Customers",       "Email",           "varchar(200)"),
            new ColumnInfo("sales",   "Orders",          "OrderId",         "int"),
            new ColumnInfo("sales",   "Orders",          "CustomerId",      "int"),
            new ColumnInfo("sales",   "Orders",          "Total",           "money"),
            new ColumnInfo("archive", "Orders",          "ArchivedOrderId", "int"),
            new ColumnInfo("archive", "Orders",          "Reason",          "nvarchar(200)"),
            new ColumnInfo("dbo",     "Order Details",   "Line Id",         "int"),
            new ColumnInfo("dbo",     "Order Details",   "Qty",             "smallint"),
            new ColumnInfo("dbo",     "Order Details",   "Key",             "int"),
            new ColumnInfo("sales",   "ActiveCustomers", "CustomerId",      "int"),
            new ColumnInfo("sys",     "objects",         "object_id",       "int"),
            new ColumnInfo("sys",     "objects",         "name",            "sysname"),
        ]);
        s.Routines.AddRange(
        [
            new RoutineInfo("sales", "GetCustomer", true),
            new RoutineInfo("dbo",   "AddOne",      false),
        ]);
        return s;
    }
}
