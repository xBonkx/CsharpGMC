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
        Assert.Contains("await Task.Run(()=>ReadRows(archive))",source);
        Assert.Contains("if(_closed)return;",source);
        Assert.Contains("_archive.Sql.Get(row.Id,_scopeObjectId)",source);
        Assert.Contains("if(!Belongs(current))",source);
        Assert.Contains("if(_closed || _loading || _initializing)return;",source);
        Assert.Contains("Closing+=(_,e)=>{if(_initializing)e.Cancel=true;};",source);
    }
}
