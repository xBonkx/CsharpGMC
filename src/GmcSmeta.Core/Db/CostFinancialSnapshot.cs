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
        var projection = ReadProjection(conn, contractId, cachedSummary, cachedRevision, before, default);
        return Confirm(projection, before, SqlServerDatabase.Revision(conn), default);
    }

    /// <summary>Cancellable read on a worker; connection ownership remains here.</summary>
    public static async Task<CostFinancialSnapshot> ReadTotalsAsync(string connectionString, int contractId,
        CostSummaryData? cachedSummary, string cachedRevision, CancellationToken token)
    {
        using var conn = await SqlServerDatabase.OpenAsync(connectionString, token).ConfigureAwait(false);
        return await ReadTotalsAsync(conn, contractId, cachedSummary, cachedRevision, token).ConfigureAwait(false);
    }

    /// <summary>The caller owns the guarded connection. Both revision reads and
    /// either financial projection observe the same cancellation token.</summary>
    public static async Task<CostFinancialSnapshot> ReadTotalsAsync(SqlConnection conn, int contractId,
        CostSummaryData? cachedSummary, string cachedRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
        var projection = ReadProjection(conn, contractId, cachedSummary, cachedRevision, before, token);
        var after = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
        return Confirm(projection, before, after, token);
    }

    private static (CostSummaryData Summary, CostTotalsData Totals) ReadProjection(
        SqlConnection conn, int contractId, CostSummaryData? cachedSummary, string cachedRevision,
        string before, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(before))
            throw new InvalidOperationException("Отсутствует ревизия SQL Server.");
        return CanReuse(cachedSummary, cachedRevision, before)
            ? (cachedSummary!, SqlServerCostTotals.Totals(conn, contractId, cachedSummary!, token))
            : SqlServerCostTotals.SummaryAndTotals(conn, contractId, token);
    }

    private static CostFinancialSnapshot Confirm((CostSummaryData Summary, CostTotalsData Totals) projection,
        string before, string after, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (after != before)
            throw new InvalidOperationException("Данные изменились во время чтения итогов. Повтори обновление.");
        return new CostFinancialSnapshot(projection.Summary, projection.Totals, before);
    }
}
