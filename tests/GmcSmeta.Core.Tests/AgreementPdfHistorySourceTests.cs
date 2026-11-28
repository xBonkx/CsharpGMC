namespace GmcSmeta.Core.Tests;

public sealed class AgreementPdfHistorySourceTests
{
    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,"GmcSmeta.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName,relative));
    }

    [Fact]
    public void HistoryReadAndSelectedOpenStayWithinSameLegalObjectAndRevision()
    {
        var sql = ReadSource("src/GmcSmeta.Core/Db/SqlServerAgreementFiles.cs");
        Assert.Contains("a.id=@id AND a.object_id=@object AND a.archived=0 AND o.archived=0",sql);
        Assert.Contains("AND f.role=N'signed_contract' AND f.signed=1 AND (@file IS NULL OR f.id=@file)",sql);
        Assert.Contains("AgreementPdfIntegrity.Verify(path,hash);",sql);
        Assert.Contains("before != SqlServerDatabase.Revision(conn)",sql);
        var card = ReadSource("src/GmcSmeta.Core/Db/SqlServerWorkspace.AgreementCard.cs");
        Assert.Contains("ReadSigned(conn, objectId, selected.Id)",card);
        Assert.Contains("SignedLegalFiles = signedFiles",card);
        var ui = ReadSource("src/GmcSmeta.App/AgreementCardView.Legal.cs");
        Assert.Contains("AgreementPdfVersionDialog.Choose",ui);
        Assert.Contains("VerifySelected(_connection",ui);
        Assert.Contains("selected is null || !IsLoaded || !ReferenceEquals(snapshot, _snapshot)",ui);
    }

    [Fact]
    public void MultipleFileSelectionHasNoImplicitOpenOrWrites()
    {
        var dialog = ReadSource("src/GmcSmeta.App/AgreementPdfVersionDialog.cs");
        Assert.Contains("IsCancel = true",dialog);
        Assert.Contains("IsEnabled = false",dialog);
        Assert.DoesNotContain("SelectedIndex = 0",dialog);
        Assert.DoesNotContain("IsDefault = true",dialog);
        Assert.DoesNotContain("SqlServerDatabase.Open",dialog);
        Assert.DoesNotContain("Process.Start",dialog);
    }
}
