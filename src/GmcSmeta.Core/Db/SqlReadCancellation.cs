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

    internal static SqlConnection Open(string connectionString, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
    }

    internal static string Revision(SqlConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var revision = token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(connection, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(connection);
        token.ThrowIfCancellationRequested();
        return revision;
    }

    internal static object? Scalar(DbCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = token.CanBeCanceled
            ? command.ExecuteScalarAsync(token).GetAwaiter().GetResult() : command.ExecuteScalar();
        token.ThrowIfCancellationRequested();
        return value;
    }
}
