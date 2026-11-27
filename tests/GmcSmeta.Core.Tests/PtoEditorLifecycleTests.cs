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
    public void KsVorSelectionAndWorkRowsShareTheSameRevisionBeforeArchiveWrite()
    {
        var source=Source("PtoSqlSource.cs");
        Assert.Contains("new KsChoice(SqlInteger.ReadInt64(r, 0)",source);
        Assert.Contains("revision==SqlServerDatabase.Revision(conn)",source);
        var read=source.IndexOf("public (IReadOnlyList<PtoWork> Rows,SqlServerKsPrintData.Package Print) ReadKsVor(",StringComparison.Ordinal);
        Assert.True(read>=0);
        var before=source.IndexOf("RequireKsRevision(conn,choice.Revision);",read,StringComparison.Ordinal);
        var rows=source.IndexOf("KsWorks(conn,contractId,choice.Id)",read,StringComparison.Ordinal);
        var print=source.IndexOf("SqlServerKsPrintData.ReadSingle(_connection,contractId,choice.Id,choice.Revision)",read,StringComparison.Ordinal);
        var after=source.IndexOf("RequireKsRevision(conn,choice.Revision);",print,StringComparison.Ordinal);
        Assert.True(before>read&&rows>before&&print>rows&&after>print);
        Assert.Contains("a.contract_id=@contract AND sy.contract_id=a.contract_id",source);
        Assert.Contains("SourceActId=actId,SourcePositionId=SqlInteger.ReadInt64(r, 4)",source);

        var editor=Source("PtoEditorWindow.xaml.cs");
        var start=editor.IndexOf("private async void KsVor_Click(",StringComparison.Ordinal);
        var end=editor.IndexOf("private async void ExternalVor_Click(",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var vor=editor[start..end];
        var snapshot=vor.IndexOf("_source.ReadKsVor(cid,chosen)",StringComparison.Ordinal);
        var render=vor.IndexOf("KsVorForms.Create(snapshot.Print,chosen.Id)",StringComparison.Ordinal);
        var fence=vor.IndexOf("_source.RequireKsRevision(chosen.Revision)",StringComparison.Ordinal);
        var attach=vor.IndexOf("_archive.AddAttachment(_data,temp,",StringComparison.Ordinal);
        var add=vor.IndexOf("_data.Positions.AddRange(rows)",StringComparison.Ordinal);
        Assert.True(snapshot>=0&&render>snapshot&&fence>render&&attach>fence&&add>attach);
        Assert.DoesNotContain("KsWorkspaceProjection.Build",vor);
        Assert.DoesNotContain("SqlServerKsPrintData.Read(",vor);
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
