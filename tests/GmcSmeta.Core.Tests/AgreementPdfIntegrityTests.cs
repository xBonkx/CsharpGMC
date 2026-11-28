using System.Security.Cryptography;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class AgreementPdfIntegrityTests
{
    [Theory]
    [InlineData("relative.pdf")]
    [InlineData("https://example.invalid/a.pdf")]
    [InlineData("")]
    public void RejectsRelativeOrNonFilePaths(string path) =>
        Assert.Throws<ArgumentException>(() => AgreementPdfIntegrity.RequirePdfPath(path));

    [Fact]
    public void VerifyRejectsChangedContentAndWrongExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".PDF");
        try
        {
            var bytes = "%PDF-1.7\noriginal"u8.ToArray();
            File.WriteAllBytes(path, bytes);
            var expected = SHA256.HashData(bytes);
            AgreementPdfIntegrity.Verify(path, expected);
            Assert.Throws<InvalidDataException>(() => AgreementPdfIntegrity.Verify(path, []));
            File.WriteAllBytes(path, "%PDF-1.7\nmodified"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => AgreementPdfIntegrity.Verify(path, expected));
            Assert.Throws<ArgumentException>(() => AgreementPdfIntegrity.RequirePdfPath(Path.ChangeExtension(path, ".exe")));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF")]
    [InlineData("plain text")]
    public void RejectsInvalidSignature(string input)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Throws<InvalidDataException>(() => AgreementPdfIntegrity.ReadHash(stream));
    }
}
