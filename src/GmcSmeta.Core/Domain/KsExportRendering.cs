using System.Diagnostics;

namespace GmcSmeta.Core.Domain;

/// <summary>Build and render an already scoped export to its caller-owned staged stream.
/// No UI, SQL, file publication, or command-context decisions belong here.</summary>
public static class KsExportRendering
{
    public static (long BuildMs, long SaveMs) SaveWorkbook<T>(Stream stream, Func<T> create,
        Func<T, bool> hasWorksheets, Action<T, Stream> save, CancellationToken token) where T : IDisposable
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using var book = create();
        var build = timer.ElapsedMilliseconds;
        if (!hasWorksheets(book)) throw new InvalidDataException("В книге нет листов.");
        token.ThrowIfCancellationRequested();
        timer.Restart();
        save(book, stream);
        token.ThrowIfCancellationRequested();
        return (build, timer.ElapsedMilliseconds);
    }

    public static (long BuildMs, long SaveMs) SavePdf(Stream stream, Func<byte[]> create,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var bytes = create();
        var build = timer.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();
        timer.Restart();
        stream.Write(bytes);
        token.ThrowIfCancellationRequested();
        return (build, timer.ElapsedMilliseconds);
    }
}
