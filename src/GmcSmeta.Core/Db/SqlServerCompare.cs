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
        var a = token.CanBeCanceled
            ? EstimateReader.LoadSqlAsync(conn, idA, token).GetAwaiter().GetResult()
            : EstimateReader.Load(conn, idA);
        token.ThrowIfCancellationRequested();
        var b = token.CanBeCanceled
            ? EstimateReader.LoadSqlAsync(conn, idB, token).GetAwaiter().GetResult()
            : EstimateReader.Load(conn, idB);
        token.ThrowIfCancellationRequested();
        var result = DocumentCompare.CompareDocuments(a, b);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
