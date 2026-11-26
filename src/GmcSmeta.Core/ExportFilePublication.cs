namespace GmcSmeta.Core;

/// <summary>Publish complete export data from a uniquely owned sibling file.
/// The destination is never opened for writing by the renderer.</summary>
public static class ExportFilePublication
{
    public static void Write(string path, Action<Stream> render)
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
            // CreateNew avoids taking ownership of an unexpected existing file.
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None))
            {
                owned = true;
                render(stream);
                if (stream.Length == 0)
                    throw new InvalidDataException("Сформирован пустой файл выгрузки.");
                stream.Flush(flushToDisk: true);
            }
            // Same directory avoids a cross-volume copy over the original.
            File.Move(temporary, destination, overwrite: true);
            owned = false;
        }
        finally
        {
            if (owned)
            {
                try { File.Delete(temporary); }
                catch { /* Preserve the original rendering or publication error. */ }
            }
        }
    }

    public static void WriteBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(path, stream => stream.Write(bytes));
    }
}
