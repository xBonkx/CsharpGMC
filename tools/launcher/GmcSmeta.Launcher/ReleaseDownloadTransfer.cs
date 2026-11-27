using System.IO;
using System.Security.Cryptography;

namespace GmcSmeta.Launcher;

internal static class ReleaseDownloadTransfer
{
    internal static string CopyVerified(Stream source, Stream target, long expectedSize,
        string? expectedSha256, CancellationToken cancellation, TimeSpan readIdleTimeout,
        Action<long>? progress = null)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long done = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            idle.CancelAfter(readIdleTimeout);
            var read = source.ReadAsync(buffer.AsMemory(), idle.Token).AsTask().GetAwaiter().GetResult();
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            idle.Token.ThrowIfCancellationRequested();
            if (read == 0) break;
            done += read;
            if (expectedSize > 0 && done > expectedSize)
                throw new InvalidOperationException("Размер скачанного вложения превышает опубликованный.");
            target.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            progress?.Invoke(done);
        }
        if (expectedSize > 0 && done != expectedSize)
            throw new InvalidOperationException("Скачивание не завершено: размер вложения не совпал.");
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма не совпала: скачанный файл повреждён.");
        cancellation.ThrowIfCancellationRequested();
        return actual;
    }
}
