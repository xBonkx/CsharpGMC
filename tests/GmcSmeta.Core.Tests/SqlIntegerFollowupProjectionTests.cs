namespace GmcSmeta.Core.Tests;

public sealed class SqlIntegerFollowupProjectionTests
{
    [Theory]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerDirector.cs")]
    [InlineData("src/GmcSmeta.App/RateTransferDialog.xaml.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerSections.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerSpecUpdate.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerDatabase.AreaCollapse.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerProcurement.BulkExtras.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerExcelExistingUpdate.cs")]
    [InlineData("src/GmcSmeta.Core/Db/SqlServerProcurement.SheetImport.cs")]
    public void ReviewedSqlProjectionsDoNotCoerceIntegerOrFlagValues(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,"GmcSmeta.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName,relative));
        Assert.DoesNotMatch(@"Convert\.To(?:Int32|Int64|Boolean)\(r\.GetValue\(",source);
        Assert.Matches(@"Sql(?:Integer\.ReadInt(?:32|64)|Flag\.Read)\(r,",source);
    }
}
