using System.Security.Cryptography;

namespace GmcSmeta.Core.Db;

/// <summary>Local file checks; no signature authenticity claim and no shell execution.</summary>
public static class AgreementPdfIntegrity
{
    public static void RequirePdfPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Требуется абсолютный путь к PDF.");
    }

    internal static byte[] ReadHash(Stream stream)
    {
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[5];
        try { stream.ReadExactly(signature); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Файл не является PDF.", ex); }
        if (!signature.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Файл не является PDF.");
        stream.Position = 0;
        return SHA256.HashData(stream);
    }

    public static void Verify(string path, byte[] expected)
    {
        RequirePdfPath(path);
        if (expected is not { Length: 32 }) throw new InvalidDataException("Отсутствует контрольная сумма PDF.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(ReadHash(stream), expected))
            throw new InvalidDataException("Содержимое PDF изменилось после регистрации. Открытие отменено.");
    }
}
