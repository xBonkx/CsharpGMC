using System.IO;
using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>Finds an existing file or folder in order history without opening it.
/// Run on a worker: filesystem probes may block on a disconnected network share.</summary>
public static class OrderHistoryFileResolver
{
    public static string Resolve(AccessHistory history, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var paths = new List<string>();
        using var json = JsonDocument.Parse(history.Data);
        var root = json.RootElement;
        if (root.TryGetProperty("Files", out var files) || root.TryGetProperty("files", out files))
            foreach (var file in files.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(file.GetString()))
                    paths.Add(file.GetString()!);
            }
        if (paths.Count == 0 && Exists(history.File, Directory.Exists, token))
            paths.Add(history.File);
        if (paths.Count == 0) throw new FileNotFoundException("В истории нет файлов этого приказа.");

        // Preserve the original priority: all files first, then all directories,
        // in the original history order within each group.
        foreach (var path in paths)
            if (Exists(path, File.Exists, token)) return path;
        foreach (var path in paths)
            if (Exists(path, Directory.Exists, token)) return path;
        throw new FileNotFoundException("Файлы из истории не найдены.");
    }

    private static bool Exists(string path, Func<string, bool> probe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var exists = probe(path);
        token.ThrowIfCancellationRequested();
        return exists;
    }
}
