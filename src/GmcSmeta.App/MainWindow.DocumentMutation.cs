using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // Navigation totals are intentionally absent from metadata-only SQL reads.
    private static DocRow DocumentMetadata(DocRow row) =>
        row with { Materials = 0m, Smr = 0m, Total = 0m, Positions = 0 };

    private bool DocumentMatchesServer(DocRow expected, string revision)
    {
        if (_serverConn is null || string.IsNullOrWhiteSpace(revision) ||
            SqlServerDatabase.Revision(_serverConn) != revision) return false;
        var actual = Workspace.ReadDocumentMetadata(_serverConn, expected.Id);
        return actual is not null && Equals(DocumentMetadata(expected), DocumentMetadata(actual)) &&
            SqlServerDatabase.Revision(_serverConn) == revision;
    }

    private bool DocumentLocallyCurrent(DocRow expected) =>
        _docs.Any(row => Equals(DocumentMetadata(row), DocumentMetadata(expected)));

    private bool CanStartDocumentMutation(DocRow expected) =>
        !_estimateCache.TryGetValue(expected.Id, out var cached) || cached.CanTransferDocument;
}
