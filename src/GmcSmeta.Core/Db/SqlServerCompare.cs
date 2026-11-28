using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Сравнение двух документов на SQL Server (ui/compare_view → compare.py).</summary>
public static class SqlServerCompare
{
    public static CompareResult Compare(string connectionString, int idA, int idB,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var revision = Revision();
        if (string.IsNullOrWhiteSpace(revision))
            throw new InvalidOperationException("Не удалось прочитать ревизию документов.");
        var a = token.CanBeCanceled
            ? EstimateReader.LoadSqlAsync(conn, idA, token).GetAwaiter().GetResult()
            : EstimateReader.Load(conn, idA);
        token.ThrowIfCancellationRequested();
        var b = token.CanBeCanceled
            ? EstimateReader.LoadSqlAsync(conn, idB, token).GetAwaiter().GetResult()
            : EstimateReader.Load(conn, idB);
        token.ThrowIfCancellationRequested();
        if (revision != Revision())
            throw new InvalidOperationException("Документы изменились при сравнении. Повторите загрузку.");
        var result = DocumentCompare.CompareDocuments(a, b, token);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
