namespace GmcSmeta.Core.Domain;

/// <summary>Two-level contract navigation, independent of the flat KP tabs.</summary>
public sealed class ContractWorkspaceTabs
{
    public string Page { get; private set; } = "estimate";
    public string CostSection { get; private set; } = "materials";
    public bool ShowCostNavigation => Page == "costs";
    public string VisibleMode => ShowCostNavigation ? CostSection : Page;

    public void SelectPage(string page)
    {
        if (page is not ("estimate" or "costs" or "procurement" or "shipments" or "acts"))
            throw new ArgumentOutOfRangeException(nameof(page));
        Page = page;
    }

    public bool SelectCostSection(string mode)
    {
        if (!ShowCostNavigation || mode is not ("materials" or "summary" or "totals"))
            return false;
        CostSection = mode;
        return true;
    }
}
