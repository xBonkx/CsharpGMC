using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private EstimateSearchIndex? _estimateSearchIndex;

    private EstimateSearchIndex SearchIndexFor(IReadOnlyList<EstimateRow> rows)
    {
        if (_estimateSearchIndex is null || !ReferenceEquals(_estimateSearchIndex.Rows, rows))
            _estimateSearchIndex = new EstimateSearchIndex(rows);
        return _estimateSearchIndex;
    }
}
