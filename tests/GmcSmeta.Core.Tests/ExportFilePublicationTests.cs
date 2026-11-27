using System.Text;
using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class ExportFilePublicationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("gmc-export-test-").FullName;

    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".pdf")]
    public void ReplacesOnlyAfterCompleteRender(string extension)
    {
        var path = Path.Combine(_folder, "report" + extension);
        File.WriteAllText(path, "old");
        ExportFilePublication.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("new"));
            Assert.Equal("old", File.ReadAllText(path));
        });
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialRenderFailurePreservesDestination(bool existing)
    {
        var path = Path.Combine(_folder, "report.xlsx");
        if (existing) File.WriteAllText(path, "old");
        Assert.Throws<IOException>(() => ExportFilePublication.Write(path, stream =>
        {
            stream.WriteByte(42);
            throw new IOException("injected renderer error");
        }));
        Assert.Equal(existing, File.Exists(path));
        if (existing) Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_folder, ".gmc-export-*"));
    }

    [Fact]
    public void EmptyRenderCannotReplaceFile()
    {
        var path = Path.Combine(_folder, "report.pdf");
        File.WriteAllText(path, "old");
        Assert.Throws<InvalidDataException>(() => ExportFilePublication.WriteBytes(path, Array.Empty<byte>()));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void CanPublishNewFile()
    {
        var path = Path.Combine(_folder, "новый отчёт.xlsx");
        byte[] expected = [1, 2, 3];
        ExportFilePublication.WriteBytes(path, expected);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void PublicationFailureDoesNotDeleteExistingDirectory()
    {
        var path = Directory.CreateDirectory(Path.Combine(_folder, "report.xlsx")).FullName;
        var marker = Path.Combine(path, "keep.txt");
        File.WriteAllText(marker, "keep");
        var error = Record.Exception(() => ExportFilePublication.WriteBytes(path, [1, 2, 3]));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(_folder, ".gmc-export-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedStageNeverPublishes(bool existing)
    {
        var path = Path.Combine(_folder, "report.xlsx");
        if (existing) File.WriteAllText(path, "old");
        using (var prepared = ExportFilePublication.Stage(path, stream => stream.WriteByte(42)))
        {
            Assert.Equal(existing, File.Exists(path));
            if (existing) Assert.Equal("old", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(_folder, ".gmc-export-*"));
        }
        Assert.Equal(existing, File.Exists(path));
        if (existing) Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_folder, ".gmc-export-*"));
    }

    [Fact]
    public void StagePublishesOnceAndDisposeIsIdempotent()
    {
        var path = Path.Combine(_folder, "report.pdf");
        File.WriteAllText(path, "old");
        using var prepared = ExportFilePublication.Stage(path, stream => stream.WriteByte(42));
        Assert.Equal("old", File.ReadAllText(path));
        prepared.Publish();
        Assert.Throws<ObjectDisposedException>(prepared.Publish);
        prepared.Dispose();
        prepared.Dispose();
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void DisposedStageCannotPublish()
    {
        var path = Path.Combine(_folder, "report.xlsx");
        using var prepared = ExportFilePublication.Stage(path, stream => stream.WriteByte(42));
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(prepared.Publish);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void CreateNewPublicationPreservesConcurrentDestination()
    {
        var path = Path.Combine(_folder, "backup.zip");
        using (var prepared = ExportFilePublication.Stage(path, stream => stream.WriteByte(42)))
        {
            File.WriteAllText(path, "other backup");
            Assert.Throws<IOException>(() => prepared.Publish(overwrite: false));
        }
        Assert.Equal("other backup", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
