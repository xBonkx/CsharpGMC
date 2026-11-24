using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

public class DatabaseTests
{
    private static string NewTempBase(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "gmcsmeta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "base.db");
    }

    [Fact]
    public void NewBase_GetsPythonSchema()
    {
        var path = NewTempBase(out var dir);
        try
        {
            using var conn = Database.OpenBase(path);

            Assert.Equal(26, CountTables(conn));
            Assert.Equal("28", Database.GetMeta(conn, "schema_version"));
            Assert.Equal(AppConfig.AppVersion, Database.GetMeta(conn, "app_version"));
            Assert.True(Database.TableExists(conn, "positions"));
            Assert.True(HasColumn(conn, "positions", "work_sell"));
            Assert.True(HasColumn(conn, "contracts", "transport_cost"));
            Assert.False(File.Exists(path + ".init-lock"));   // замок снят
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Reopen_DoesNotRewriteSchema()
    {
        var path = NewTempBase(out var dir);
        try
        {
            using (var conn = Database.OpenBase(path)) Assert.Equal(26, CountTables(conn));
            using (var conn = Database.OpenBase(path)) Assert.Equal(26, CountTables(conn));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void OlderProgram_RefusesNewerBase()
    {
        var path = NewTempBase(out var dir);
        try
        {
            using (var conn = Database.OpenBase(path))
                Database.SetMeta(conn, "app_version", "99.0.0");

            var e = Assert.Throws<OldProgramException>(() => Database.OpenBase(path, "0.66.4"));
            Assert.Equal("99.0.0", e.BaseVersion);
            Assert.Contains("более новая версия", e.Message);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void VersionTuple_ComparesLikePython()
    {
        Assert.True(Database.CompareVersions("0.66.4", "0.66.3") > 0);
        Assert.True(Database.CompareVersions("0.67", "0.66.4") > 0);
        Assert.True(Database.CompareVersions("0.7", "0.66.4") < 0);   // 7 < 66, как в Python
        Assert.Equal(0, Database.CompareVersions("1", "1.0.0"));
        Assert.Equal(0, Database.CompareVersions("", "0"));
    }

    private static void Cleanup(string dir)
    {
        SqliteConnection.ClearAllPools();   // пул соединений держит файл открытым
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(dir, true); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private static long CountTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
