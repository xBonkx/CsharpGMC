using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PtoPackageListProjectionTests
{
    [Fact]
    public void SearchIncludesNestedPayloadAndPreservesScopeOrderAndStatus()
    {
        var first=PtoRules.New(1,"Объект А","",10,"Договор");
        first.Fields["notes"]="Проверить клапан";
        first.Positions.Add(new PtoWork {Name="Воздуховод"});
        var second=PtoRules.New(1,"Объект А","",10,"Договор");
        second.Status="На проверке";
        second.Fields["notes"]="Проверить клапан";
        var otherContract=PtoRules.New(1,"Объект А","",11);
        var otherObject=PtoRules.New(2,"Объект Б","",10);
        var rows=PtoPackageListProjection.Build(new[]{first,otherContract,second,otherObject},1,10);
        Assert.Equal(new[]{first.Id,second.Id},rows.Select(r=>r.Id));
        Assert.Equal(new[]{first.Id,second.Id},PtoPackageListProjection.Filter(rows,"КЛАПАН",null).Select(r=>r.Id));
        Assert.Equal(first.Id,Assert.Single(PtoPackageListProjection.Filter(rows,"воздуховод","Все статусы")).Id);
        Assert.Equal(second.Id,Assert.Single(PtoPackageListProjection.Filter(rows,"клапан","На проверке")).Id);
        var findings=PtoRules.Check(first);
        Assert.Equal($"{findings.Errors.Count} ошибок · {findings.Warnings.Count} предупрежд.",rows[0].Findings);
        Assert.Empty(PtoPackageListProjection.Filter(rows,"несуществующее",null));
    }

    [Fact]
    public void RepeatedFiltersUseOnlyProjectedRowsAndRefreshRebuildsSnapshot()
    {
        var p=PtoRules.New(1,"Объект","");
        p.Fields["notes"]="Первое";
        var rows=PtoPackageListProjection.Build(new[]{p});
        p.Fields["notes"]="Второе";
        Assert.Single(PtoPackageListProjection.Filter(rows,"Первое",null));
        Assert.Empty(PtoPackageListProjection.Filter(rows,"Второе",null));
        Assert.Single(PtoPackageListProjection.Filter(PtoPackageListProjection.Build(new[]{p}),"Второе",null));
    }

    [Fact]
    public void CancelledProjectionDoesNotEnumeratePackages()
    {
        static IEnumerable<PtoPackage> NeverEnumerate()
        {
            throw new InvalidOperationException("Enumeration must not start.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        using var cancellation=new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>
            PtoPackageListProjection.Build(NeverEnumerate(),token:cancellation.Token));
    }

    [Fact]
    public void WindowFiltersLocallyAndKeepsAuthoritativeReadBeforeActions()
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App","PtoWindow.xaml.cs")))
            root=root.Parent;
        Assert.NotNull(root);
        var source=File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App","PtoWindow.xaml.cs"));
        var filter=source[source.IndexOf("private void Filter_Changed",StringComparison.Ordinal)..
            source.IndexOf("private bool Belongs",StringComparison.Ordinal)];
        Assert.Contains("ApplyFilter(SelectedId());",filter);
        Assert.DoesNotContain("Refresh(",filter);
        Assert.Contains("await Task.Run(()=>ReadRows(archive,token),token)",source);
        Assert.Contains("if(_closed || token.IsCancellationRequested)return;",source);
        Assert.Contains("_archive.Sql.Get(row.Id,_scopeObjectId)",source);
        Assert.Contains("if(!Belongs(current))",source);
        Assert.Contains("if(_closed || _loading || _initializing)return;",source);
        Assert.Contains("Closing+=(_,e)=>{if(_initializingArchive)e.Cancel=true;};",source);
        Assert.Contains("_readCancellation?.Cancel();",source);
        Assert.Contains("archive.Sql.Packages(_scopeObjectId,_scopeContractId,token)",source);
        Assert.True(source.IndexOf("_initializingArchive=false;",StringComparison.Ordinal) <
            source.IndexOf("await Task.Run(()=>ReadRows(snapshot.Archive,token),token)",StringComparison.Ordinal));
    }
}
