namespace GmcSmeta.Core.Db;

/// <summary>
/// Stage one of the Python v31 port. Schema 28 stays supported for the existing
/// test suite. A restored v31 database may only be opened as a server-enforced
/// read-only preview until every monetary reader and writer has been adapted.
/// </summary>
public static class SqlSchemaCompatibility
{
    // Single authoritative version for ordinary writable SQL Server clients.
    // Updating this requires an approved database migration and release gate.
    public const int WritableSchemaVersion = 32;
    public const int AgreementSchemaVersion = 32;
    public static bool IsAgreementTestDatabase(string? name, string? source) =>
        (name ?? "").StartsWith("GmcSmetaPilot_Test_Agreement_", StringComparison.Ordinal) && IsLocalServer(source);
    // Keep disabled until all schema31 routes (including undo/trash/export) pass SQL acceptance.
    public static bool OrdinarySchema31Enabled => WritableSchemaVersion == 31;
    public const string PreviewDatabase = "GmcSmetaPilot_Test_Csharp";
    public const string WriteTestDatabase = "GmcSmetaPilot_Schema31WriteTest";
    // The only database where the ordinary application may write schema 31 before
    // the production cutover is approved. Must stay a local, dedicated test copy.
    public const string OrdinarySchema31TestDatabase = "GmcSmetaPilot_Test_Schema31";

    public const string ApprovedCutoverDatabase = "GmcSmetaPilot_Cutover_20261125";
    public const string ApprovedCutoverDataSource = @".\SQLEXPRESS";

    public const string ApprovedCutoverNetworkSource = "tcp:192.168.1.46,1433";

    public static bool IsNetworkCutover(string? name, string? dataSource) =>
        string.Equals(name, ApprovedCutoverDatabase, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(dataSource, ApprovedCutoverNetworkSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsApprovedCutover(string? name, string? dataSource) =>
        string.Equals(name, ApprovedCutoverDatabase, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(dataSource, ApprovedCutoverDataSource, StringComparison.OrdinalIgnoreCase) ||
            IsNetworkCutover(name, dataSource));

    public static bool IsWriteTestDatabase(string? name) =>
        string.Equals(name, WriteTestDatabase, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalServer(string? dataSource)
    {
        var source = (dataSource ?? "").Trim();
        if (source.Equals(GmcSmeta.Core.Demo.DemoMode.LocalServer, StringComparison.OrdinalIgnoreCase))
            return true;
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            source = source[4..];
        var comma = source.IndexOf(',');
        if (comma >= 0) source = source[..comma];
        source = source.Trim();
        return source.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || source == "." || source == "(local)"
            || source == "127.0.0.1"
            || source.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(@".\", StringComparison.Ordinal);
    }

    public static bool IsOrdinarySchema31TestDatabase(string? name, string? dataSource) =>
        string.Equals(name, OrdinarySchema31TestDatabase, StringComparison.OrdinalIgnoreCase)
        && IsLocalServer(dataSource);

    /// <summary>True when ordinary (UI) writers may persist schema 31 rows for this
    /// database. Production stays on schema 28 until the owner approves cutover.</summary>
    public static bool Schema31WritesAllowed(string? name, string? dataSource) =>
        OrdinarySchema31Enabled || IsAgreementTestDatabase(name, dataSource) || IsOrdinarySchema31TestDatabase(name, dataSource) ||
        IsApprovedCutover(name, dataSource);

    public static bool Schema31WritesAllowed(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return Schema31WritesAllowed(builder.InitialCatalog, builder.DataSource);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void RequireWriteTestDatabase(string? name)
    {
        if (!IsWriteTestDatabase(name))
            throw new InvalidOperationException("Запись v31 пока разрешена только специальному тесту на отдельной базе " + WriteTestDatabase + ".");
    }

    public static void ValidateWriteTest(string? version, string? name, string? updateability, bool requiredColumns)
    {
        RequireWriteTestDatabase(name);
        if (version != "31" || !requiredColumns ||
            !string.Equals(updateability, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для теста записи нужна схема 31 READ_WRITE со всеми новыми полями.");
    }

    // Open() verifies the actual schema and read-only state before these readers
    // run. Keep the old projection for schema 28 and legacy offline fixtures.
    public static bool HasPreviewColumns(System.Data.Common.DbConnection conn) =>
        conn is Microsoft.Data.SqlClient.SqlConnection sql &&
        (IsPreviewDatabase(sql.Database) || IsWriteTestDatabase(sql.Database) ||
            IsOrdinarySchema31TestDatabase(sql.Database, sql.DataSource) ||
            IsApprovedCutover(sql.Database, sql.DataSource) || IsAgreementTestDatabase(sql.Database, sql.DataSource) ||
            OrdinarySchema31Enabled && IsWritablePilot(sql.Database,sql.DataSource));

    public static bool IsPreviewDatabase(string? name) =>
        string.Equals(name, PreviewDatabase, StringComparison.OrdinalIgnoreCase);

    // Owner-confirmed local test copy (24.11.2026). Never allow this name on remote servers.
    public const string LocalTestCutover = "GmcSmetaPilot_Cutover_20261120_160840";

    public static bool IsLocalTestCutover(string? name, string? dataSource)
    {
        if (!string.Equals(name, LocalTestCutover, StringComparison.OrdinalIgnoreCase))
            return false;
        var source = (dataSource ?? "").Trim();
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            source = source[4..];
        return source.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || source.Equals("localhost,1433", StringComparison.OrdinalIgnoreCase)
            || source == "127.0.0.1" || source == "127.0.0.1,1433";
    }

    public static bool IsProductionCutover(string? name, string? dataSource = null) =>
        (name ?? "").StartsWith("GmcSmetaPilot_Cutover", StringComparison.OrdinalIgnoreCase)
        && !IsLocalTestCutover(name, dataSource);

    public static bool IsWritablePilot(string? name, string? dataSource = null) =>
        (name ?? "").StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase)
        && (!IsProductionCutover(name, dataSource) || IsApprovedCutover(name, dataSource)) && !IsPreviewDatabase(name) && !IsWriteTestDatabase(name);

    public static void RejectProduction(string? name, string? dataSource = null)
    {
        if (IsProductionCutover(name, dataSource) && !IsApprovedCutover(name, dataSource))
            throw new InvalidOperationException(
                "Рабочая база Cutover запрещена для C#. Используйте отдельную восстановленную копию.");
    }

    public static void Validate(string? version, string? name, string? updateability, bool requiredColumns,
        string? dataSource = null)
    {
        RejectProduction(name, dataSource);
        if (version == "28" && !IsProductionCutover(name,dataSource) && !IsPreviewDatabase(name) && !IsWriteTestDatabase(name) && !IsOrdinarySchema31TestDatabase(name,dataSource)) return;
        if (version == "32" && (IsApprovedCutover(name, dataSource) || IsAgreementTestDatabase(name, dataSource)))
        {
            if (!requiredColumns || !string.Equals(updateability, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Схема договоров 32 неполна или недоступна для записи.");
            return;
        }
        if (version == "32") throw new InvalidOperationException("Схема договоров 32 допускается только для согласованной рабочей базы и отдельной локальной копии Agreement.");
        if (IsWriteTestDatabase(name))
            throw new InvalidOperationException("Специальная база теста записи v31 недоступна обычному приложению.");
        if (IsPreviewDatabase(name))
        {
            if (version != "31" || !requiredColumns ||
                !string.Equals(updateability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Для предпросмотра нужна восстановленная база схемы 31 GmcSmetaPilot_Test_Csharp в режиме READ_ONLY со всеми полями скидок и КС.");
            return;
        }
        if (IsOrdinarySchema31TestDatabase(name, dataSource) || IsApprovedCutover(name, dataSource))
        {
            if (version != "31" || !requiredColumns ||
                !string.Equals(updateability, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Для записи v31 нужна проверенная база READ_WRITE со всеми полями скидок и КС.");
            return;
        }
        if (version != WritableSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                $"Схема SQL Server должна быть версии {WritableSchemaVersion}. Для просмотра схемы 31 подготовьте отдельную READ_ONLY копию GmcSmetaPilot_Test_Csharp.");
        if(OrdinarySchema31Enabled&&(!IsWritablePilot(name,dataSource)||!requiredColumns||
            !string.Equals(updateability,"READ_WRITE",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Штатная запись v31 требует отдельной пилотной базы READ_WRITE со всеми новыми полями.");
    }
}
