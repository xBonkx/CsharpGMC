using GmcSmeta.Core.Domain;
using Xunit;

namespace GmcSmeta.Core.Tests;

public class ContractWorkspaceTabsTests
{
    [Fact]
    public void CostsOpensMaterialsAndRemembersSelectedSection()
    {
        var tabs = new ContractWorkspaceTabs();
        Assert.Equal("estimate", tabs.VisibleMode);
        Assert.False(tabs.ShowCostNavigation);
        tabs.SelectPage("costs");
        Assert.True(tabs.ShowCostNavigation);
        Assert.Equal("materials", tabs.VisibleMode);
        Assert.True(tabs.SelectCostSection("summary"));
        Assert.Equal("costs", tabs.Page);
        Assert.Equal("summary", tabs.VisibleMode);
        tabs.SelectPage("estimate");
        Assert.False(tabs.ShowCostNavigation);
        Assert.Equal("estimate", tabs.VisibleMode);
        tabs.SelectPage("costs");
        Assert.Equal("summary", tabs.VisibleMode);
        Assert.True(tabs.SelectCostSection("totals"));
        Assert.Equal("totals", tabs.VisibleMode);
    }

    [Theory]
    [InlineData("estimate")]
    [InlineData("procurement")]
    [InlineData("shipments")]
    [InlineData("acts")]
    public void OtherMainPagesNeverExposeOrChangeCostSection(string page)
    {
        var tabs = new ContractWorkspaceTabs();
        tabs.SelectPage("costs");
        tabs.SelectCostSection("totals");
        tabs.SelectPage(page);
        Assert.False(tabs.ShowCostNavigation);
        Assert.Equal(page, tabs.VisibleMode);
        Assert.False(tabs.SelectCostSection("summary"));
        tabs.SelectPage("costs");
        Assert.Equal("totals", tabs.VisibleMode);
        Assert.False(tabs.SelectCostSection("estimate"));
        Assert.False(tabs.SelectCostSection("smr"));
        Assert.Equal("totals", tabs.VisibleMode);
    }
}
