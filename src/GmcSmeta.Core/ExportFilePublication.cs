namespace GmcSmeta.Core;

/// <summary>Publish complete export data from a uniquely owned sibling file.
/// The destination is never opened for writing by the renderer.</summary>
public static class ExportFilePublication
{
    // Separate preparation from publication so UI callers can verify their
    // document context after rendering, without holding the output in memory.
    public static PreparedExport Stage(string path, Action<Stream> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Не указана папка выгрузки.", nameof(path));
        var temporary = Path.Combine(directory, ".gmc-export-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(destination));
        bool owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None))
            {
                owned = true;
                render(stream);
                if (stream.Length == 0)
                    throw new InvalidDataException("Сформирован пустой файл выгрузки.");
                stream.Flush(flushToDisk: true);
            }
            return new PreparedExport(temporary, destination);
        }
        catch
        {
            if (owned) DeleteOwned(temporary);
            throw;
        }
    }

    public static void Write(string path, Action<Stream> render)
    {
        using var prepared = Stage(path, render);
        prepared.Publish();
    }

    public static void WriteBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(path, stream => stream.Write(bytes));
    }

    private static void DeleteOwned(string temporary)
    {
        try { File.Delete(temporary); }
        catch { /* Preserve the original rendering or publication error. */ }
    }

    // Single-owner, sequential use: Stage, then Publish or Dispose. After a
    // successful move Dispose cannot remove the published destination.
    public sealed class PreparedExport : IDisposable
    {
        private string? _temporary;
        private readonly string _destination;

        internal PreparedExport(string temporary, string destination)
        {
            _temporary = temporary;
            _destination = destination;
        }

        public void Publish()
        {
            var temporary = _temporary ?? throw new ObjectDisposedException(nameof(PreparedExport));
            File.Move(temporary, _destination, overwrite: true);
            _temporary = null;
        }

        public void Dispose()
        {
            if (_temporary is not { } temporary) return;
            _temporary = null;
            DeleteOwned(temporary);
        }
    }
}
