using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Revision-fenced cost totals, with optional summary reuse from the
/// same contract and connection context. Never pass a write receipt as a
/// published revision. A stale cache is ignored, not patched.</summary>
public sealed record CostFinancialSnapshot(CostSummaryData Summary, CostTotalsData Totals, string Revision)
{
    public static bool CanReuse(CostSummaryData? cachedSummary, string cachedRevision, string? observedRevision) =>
        cachedSummary is not null && !string.IsNullOrWhiteSpace(cachedRevision) &&
        string.Equals(cachedRevision, observedRevision, StringComparison.Ordinal);

    public static CostFinancialSnapshot ReadTotals(string connectionString, int contractId,
        CostSummaryData? cachedSummary = null, string cachedRevision = "")
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return ReadTotals(conn, contractId, cachedSummary, cachedRevision);
    }

    public static CostFinancialSnapshot ReadTotals(SqlConnection conn, int contractId,
        CostSummaryData? cachedSummary = null, string cachedRevision = "")
    {
        var before = SqlServerDatabase.Revision(conn);
        if (string.IsNullOrWhiteSpace(before))
            throw new InvalidOperationException("Отсутствует ревизия SQL Server.");
        var (summary, totals) = CanReuse(cachedSummary, cachedRevision, before)
            ? (cachedSummary!, SqlServerCostTotals.Totals(conn, contractId, cachedSummary!))
            : SqlServerCostTotals.SummaryAndTotals(conn, contractId);
        if (SqlServerDatabase.Revision(conn) != before)
            throw new InvalidOperationException("Данные изменились во время чтения итогов. Повтори обновление.");
        return new CostFinancialSnapshot(summary, totals, before);
    }
}
