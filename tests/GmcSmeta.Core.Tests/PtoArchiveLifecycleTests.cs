namespace GmcSmeta.Core.Tests;

public sealed class PtoArchiveLifecycleTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",file)))root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",file));
    }

    [Fact]
    public void CertificateSearchUsesLocalSnapshotAndPdfOperationsUseWorker()
    {
        var code=Source("PtoCertificatesWindow.cs");
        Assert.Contains("PtoCertificateSearch.Build(_archive.Sql.Certificates())",code);
        Assert.Contains("PtoCertificateSearch.Filter(_rows,_search.Text)",code);
        Assert.DoesNotContain("Certificates(_search.Text",code);
        Assert.Contains("await Task.Run(()=>_archive.ImportCertificate(path,cert))",code);
        Assert.Contains("await Task.Run(()=>_archive.Sql.SaveCertificate(data))",code);
        Assert.Contains("if(!_closed)Process.Start",code);
        Assert.Contains("if(_busy)e.Cancel=true",code);
    }

    [Fact]
    public void ArchiveOperationsAreGuardedAndBackupUsesCreateNewPublication()
    {
        var code=Source("PtoWindow.xaml.cs");
        Assert.Contains("if(_initializingArchive||_archiveOperation)e.Cancel=true",code);
        Assert.Contains("await Task.Run(()=>PtoBackup.Create(archive,path,_connection))",code);
        Assert.Contains("await Task.Run(()=>PtoBackup.ValidateZip(path))",code);
        Assert.Contains("PtoBackup.Restore(_connection,path,target)",code);
        Assert.Contains("await ReloadAfterArchiveChangeAsync()",code);
        var backup=Source("PtoBackup.cs");
        Assert.Contains("ExportFilePublication.Stage(output",backup);
        Assert.Contains("prepared.Publish(overwrite:false)",backup);
        Assert.Contains("leaveOpen:true",backup);
        Assert.Contains("PtoArchive.Sha(path).Equals(pair.Sha",backup);
    }
}
