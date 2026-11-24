using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerAttachmentIsolationTests
{
    private const string Production = "Server=invalid;Database=GmcSmeta;Integrated Security=true;Connect Timeout=1";
    private const string Pilot = "Server=invalid;Database=GmcSmetaPilot_Attachments;Integrated Security=true;Connect Timeout=1";

    [Fact]
    public void ProductionCannotRegisterOrChangeMetadataOrDelete()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.Register(Production, 1,
            new[] { new SqlServerAttachments.NewFile("scan.pdf", "scan.pdf", 100) }, true, "rev"));
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.SetSigned(Production, 1, 2, "scan.pdf", true, true, "rev"));
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.Delete(Production, 1, new long[] { 2 }, "rev"));
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.StorageFolder(Production, 1, @"\\server\share"));
    }

    [Fact]
    public void NoLocalArchiveFallback()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.StorageFolder(Pilot, 1, ""));
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.StorageFolder(Pilot, 1, @"C:\Temp"));
        Assert.Throws<InvalidOperationException>(() => SqlServerAttachments.StorageFolder(Pilot, 1, "relative"));
    }

    [Fact]
    public void UncArchiveIsScopedToPilotAndContract()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"\\server\share\object\Договоры\GmcSmetaPilot_Attachments\42",
            SqlServerAttachments.StorageFolder(Pilot, 42, @"\\server\share\object"));
    }

    [Fact]
    public void InvalidSelectionsAreRejectedBeforeConnection()
    {
        Assert.Throws<ArgumentException>(() => SqlServerAttachments.Delete(Pilot, 1, new long[] { 2, 2 }, "rev"));
        Assert.Throws<ArgumentException>(() => SqlServerAttachments.SetSigned(Pilot, 1, 2, "scan.pdf", false, true, "rev"));
    }
}
