using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Cli;

internal static class AgreementAuditCommand
{
    internal static int Run(string connectionString)
    {
        // Guard before opening a connection, even though this is read-only.
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        var revision = SqlServerDatabase.Revision(conn);
        var rows = Workspace.ReadAllDocumentMetadata(conn);
        if (string.IsNullOrWhiteSpace(revision) || revision != SqlServerDatabase.Revision(conn))
            throw new InvalidOperationException("Данные изменились во время аудита; повторите команду.");
        var findings = AgreementAudit.Inspect(rows);
        Console.WriteLine($"Карточек с приложениями/ДС: {rows.Where(d => d.Kind is "main" or "dop").Select(d => d.ObjectId).Distinct().Count()}");
        Console.WriteLine($"Замечаний: {findings.Count}. Карточек с замечаниями: {findings.Select(f => f.ObjectId).Distinct().Count()}.");
        foreach (var finding in findings)
            Console.WriteLine($"object_id={finding.ObjectId}; {finding.Code}; document_ids={string.Join(",", finding.DocumentIds)}");
        Console.WriteLine("Только диагностика. Записи, реквизиты и состав КС не изменялись.");
        return 0;
    }
}
