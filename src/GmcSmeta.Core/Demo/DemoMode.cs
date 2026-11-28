using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Demo;

/// <summary>Temporary native SQL Server test mode, not a second storage backend.</summary>
public static class DemoMode
{
    public const string Prefix = "GmcSmetaPilot_Test_Agreement_Demo_";
    public const string LocalServer = @"lpc:.\SQLEXPRESS";
    public const string Marker = "gmc_demo_ready";
    public static bool Active => IsRequested(Environment.GetCommandLineArgs().Skip(1));
    public static bool IsRequested(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--demo", StringComparison.Ordinal));

    public static string DatabaseFromArgs(IEnumerable<string> args)
    {
        var items = args.ToArray();
        if (items.Length is not (2 or 3) || items[0] != "--demo" ||
            (items.Length == 3 && items[2] != "--smoke-test") ||
            !Regex.IsMatch(items[1], @"\A" + Prefix + @"[0-9a-f]{12}\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Запуск: --demo GmcSmetaPilot_Test_Agreement_Demo_<идентификатор>.");
        return items[1];
    }

    private static string CurrentDatabase => DatabaseFromArgs(Environment.GetCommandLineArgs().Skip(1));

    public static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GMC-Smeta-Demo", "CSharp", CurrentDatabase);

    public static string ApplicationData => Active
        ? Path.Combine(ProfileRoot, "profile")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LocalData => Active
        ? Path.Combine(ProfileRoot, "local")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string CreateConnectionString(string database)
    {
        DatabaseFromArgs(["--demo", database]);
        return new SqlConnectionStringBuilder
        {
            DataSource = LocalServer,
            InitialCatalog = database,
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true, // local Shared Memory only, never the office TLS profile
            ConnectTimeout = 5,
            ApplicationName = "GMC-Smeta C# DEMO"
        }.ConnectionString;
    }

    public static void ConfigureProcess(string[] args)
    {
        var database = DatabaseFromArgs(args);
        var profile = ProfileRoot;
        Directory.CreateDirectory(ApplicationData);
        Directory.CreateDirectory(LocalData);
        Environment.SetEnvironmentVariable("APPDATA", ApplicationData);
        Environment.SetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION", CreateConnectionString(database));
        Environment.SetEnvironmentVariable("GMC_SMETA_HOME", null);
        Environment.SetEnvironmentVariable("GMC_SMETA_LAUNCHER", null);
        Environment.SetEnvironmentVariable("GMC_SMETA_NO_UPDATE", "1");
        Environment.SetEnvironmentVariable("GMC_SMETA_AUTO_RESTART", "0");
        foreach (var module in new[] { "PTO", "LETTERS", "PASSPORTS" })
        {
            var path = Path.Combine(profile, "workspace", module);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable("GMC_SMETA_" + module + "_ROOT", path);
        }
    }

    public static void ValidateConnection(SqlConnectionStringBuilder builder,
        IEnumerable<string> args)
    {
        if (!IsRequested(args))
        {
            if (builder.InitialCatalog.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Откройте тестовую базу отдельным ярлыком --demo.");
            return;
        }
        var database = DatabaseFromArgs(args);
        if (!string.Equals(builder.InitialCatalog, database, StringComparison.Ordinal) ||
            !string.Equals(builder.DataSource, LocalServer, StringComparison.OrdinalIgnoreCase) ||
            !builder.IntegratedSecurity || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new InvalidOperationException("Тестовый запуск привязан к своей локальной базе.");
    }

    private const string IdentitySql = """
        SELECT DB_NAME(),
          CONVERT(nvarchar(128),SERVERPROPERTY('ComputerNamePhysicalNetBIOS')),
          CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),
          CONVERT(nvarchar(128),CONNECTIONPROPERTY('net_transport')),
          (SELECT CONVERT(nvarchar(128),[value]) FROM dbo.meta WHERE [key]=N'gmc_demo_ready'),
          (SELECT CONVERT(nvarchar(32),[value]) FROM dbo.meta WHERE [key]=N'schema_version')
        """;

    public static void ValidateIdentity(IDataRecord row, string database, string machine)
    {
        string Text(int index) => row.IsDBNull(index) ? "" : Convert.ToString(row.GetValue(index)) ?? "";
        if (Text(0) != database || !Text(1).Equals(machine, StringComparison.OrdinalIgnoreCase) ||
            !Text(2).Equals("SQLEXPRESS", StringComparison.OrdinalIgnoreCase) ||
            !Text(3).Equals("Shared memory", StringComparison.OrdinalIgnoreCase) ||
            Text(4) != database || Text(5) != "32")
            throw new InvalidOperationException("Тестовая база, локальный сервер, метка готовности или схема 32 не подтверждены.");
    }

    public static void VerifyDatabase(SqlConnection connection)
    {
        if (!Active) return;
        using var cmd = new SqlCommand(IdentitySql, connection);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Не удалось проверить тестовую базу.");
        ValidateIdentity(reader, CurrentDatabase, Environment.MachineName);
    }

    public static async Task VerifyDatabaseAsync(SqlConnection connection, CancellationToken token)
    {
        if (!Active) return;
        using var cmd = new SqlCommand(IdentitySql, connection);
        using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Не удалось проверить тестовую базу.");
        ValidateIdentity(reader, CurrentDatabase, Environment.MachineName);
    }
}
