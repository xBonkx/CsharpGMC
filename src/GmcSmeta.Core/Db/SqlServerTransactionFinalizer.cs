namespace GmcSmeta.Core.Db;

/// <summary>
/// A single ordering contract for SQL Server revision-guarded writes:
/// bump meta.rev while the transaction is active, then commit. If revision
/// generation or SQL update fails, never commit partial changes.
/// </summary>
public static class SqlServerTransactionFinalizer
{
    public static string BumpAndCommit(Func<string> bumpRevision, Action commit)
    {
        ArgumentNullException.ThrowIfNull(bumpRevision);
        ArgumentNullException.ThrowIfNull(commit);
        var revision = bumpRevision();
        if (string.IsNullOrWhiteSpace(revision))
            throw new InvalidOperationException("Пустая ревизия SQL Server: транзакция отменена.");
        commit();
        return revision;
    }
}
