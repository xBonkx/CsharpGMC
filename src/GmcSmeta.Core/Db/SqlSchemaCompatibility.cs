namespace GmcSmeta.Core.Db;

/// <summary>
/// Stage one of the Python v31 port. Schema 28 stays supported for the existing
/// test suite. A restored v31 database may only be opened as a server-enforced
/// read-only preview until every monetary reader and writer has been adapted.
/// </summary>
public static class SqlSchemaCompatibility
{
    public const string PreviewDatabase = "GmcSmetaPilot_Test_Csharp";
    public const string WriteTestDatabase = "GmcSmetaPilot_Schema31WriteTest";

    public static bool IsWriteTestDatabase(string? name) =>
        string.Equals(name, WriteTestDatabase, StringComparison.OrdinalIgnoreCase);

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
        (IsPreviewDatabase(sql.Database) || IsWriteTestDatabase(sql.Database));

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
        && !IsProductionCutover(name, dataSource) && !IsPreviewDatabase(name) && !IsWriteTestDatabase(name);

    public static void RejectProduction(string? name, string? dataSource = null)
    {
        if (IsProductionCutover(name, dataSource))
            throw new InvalidOperationException(
                "Рабочая база Cutover запрещена для C#. Используйте отдельную восстановленную копию.");
    }

    public static void Validate(string? version, string? name, string? updateability, bool requiredColumns,
        string? dataSource = null)
    {
        RejectProduction(name, dataSource);
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
        if (version != "28")
            throw new InvalidOperationException(
                "Схема SQL Server должна быть версии 28. Для просмотра схемы 31 подготовьте отдельную READ_ONLY копию GmcSmetaPilot_Test_Csharp.");
    }
}
