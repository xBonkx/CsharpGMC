namespace GmcSmeta.Core.Domain;

/// <summary>Presentation only: search and stage filters reveal matching nested rows;
/// an explicitly expanded m² summary remains reachable after collapse all.</summary>
public static class EstimateNestedVisibility
{
    public static bool ShowComponents(bool isAreaSummary, bool expandedArea,
        bool collapseOrdinary, bool revealNested, bool honorAreaFold) =>
        revealNested || (isAreaSummary ? !honorAreaFold || expandedArea : !collapseOrdinary);
}
