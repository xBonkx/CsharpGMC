using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Cancellation bridge for existing synchronous, read-only SQL
/// projections running on a worker. Initial execution uses the provider token;
/// registration covers subsequent synchronous reader operations.</summary>
internal static class SqlReadCancellation
{
    internal static CancellationTokenRegistration Register(DbCommand command, CancellationToken token) =>
        token.Register(static state =>
        {
            try { ((DbCommand)state!).Cancel(); }
            catch (SqlException) { /* Cancellation is best effort; caller checks token. */ }
            catch (InvalidOperationException) { /* Command may already have completed. */ }
        }, command);

    internal static DbDataReader ExecuteReader(DbCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return token.CanBeCanceled
            ? command.ExecuteReaderAsync(token).GetAwaiter().GetResult()
            : command.ExecuteReader();
    }
}
