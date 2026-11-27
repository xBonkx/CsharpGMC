namespace GmcSmeta.Core.Tests;

public sealed class PtoEditorLifecycleTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",file)))root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",file));
    }

    [Fact]
    public void SaveAndReleaseKeepValidationAndExclusiveModelAccess()
    {
        var code=Source("PtoEditorWindow.xaml.cs");
        Assert.Contains("EditorTabs.IsEnabled=!busy&&!_writeUncertain;",code);
        Assert.Contains("ReferenceEquals(button,CloseButton)",code);
        Assert.Contains("if(_busy||_writeUncertain||_closingSave)return;",code);
        Assert.Contains("var findings=_archive.Inspect(_data);",code);
        Assert.Contains("findings.Errors.Count>0 || !_data.ManualReview",code);
        Assert.Contains("await Task.Run(()=>_archive.BuildDelivery(_data))",code);
        Assert.Contains("if(error is PtoCommitUncertainException)",code);
        Assert.DoesNotContain("private bool Save()",code);
    }

    [Fact]
    public void ClosingWaitsForConfirmedSaveAndNeverAutomaticallyRetriesUnknownCommit()
    {
        var code=Source("PtoEditorWindow.xaml.cs");
        Assert.Contains("if(_busy||_closingSave){e.Cancel=true;return;}",code);
        Assert.Contains("if(_writeUncertain)",code);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(()=>_ = SaveAndCloseAsync()))",code);
        Assert.Contains("await SaveAsync();",code);
Assert.Contains("_allowClose=true;",code);
        Assert.Contains("if(_allowClose)Close();",code);
    }

    [Fact]
    public void AttachmentReadAndImportUseWorkerAndPreserveSelection()
    {
        var code=Source("PtoEditorWindow.xaml.cs");
        Assert.Contains("await Task.Run(()=>_archive.LinkCertificate(_data,cert))",code);
        Assert.Contains("var state=_archive.FileInfo(file);",code);
        Assert.Contains("if(!closed)Process.Start",code);
        Assert.Contains("FilesGrid.SelectedItem=rows.FirstOrDefault(x=>x.Id==selected);",code);
        Assert.Contains("finally{WorksGrid.Items.Refresh();RefreshFiles();InvalidateReview();UpdateRevisionLabel();}",code);
        Assert.Contains("var acts=await Task.Run(()=>_source.Ks(cid));",code);
    }
}
