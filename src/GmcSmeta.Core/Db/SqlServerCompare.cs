using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Сравнение двух документов на SQL Server (ui/compare_view → compare.py).</summary>
public static class SqlServerCompare
{
    public static CompareResult Compare(string connectionString, int idA, int idB)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var a = EstimateReader.Load(conn, idA);
        var b = EstimateReader.Load(conn, idB);
        return DocumentCompare.CompareDocuments(a, b);
    }
}
