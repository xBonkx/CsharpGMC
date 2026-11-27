using GmcSmeta.Core.Db;
using SaveResult = GmcSmeta.Core.Db.SqlServerDatabase.SaveResult;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // Use the same SQL transaction/guards as mouse placement. Only sibling
    // order changes; a root section keeps all its subsections and positions.
    private async Task<SaveResult> MoveSelectedSystemAsync(EditableEstimateRow row, int direction)
    {
        var siblings = _rows.Where(r => r.IsSystem && !r.IsVirtual &&
            r.ParentSystemId == row.ParentSystemId).ToArray();
        var index = Array.FindIndex(siblings, r => r.Source.SystemId == row.Source.SystemId);
        if (index < 0 || index + direction < 0 || index + direction >= siblings.Length)
            return new SaveResult(true, null, _serverRevision);
        var anchorIndex = direction < 0 ? index - 2 : index + 1;
        long? after = anchorIndex < 0 ? null : siblings[anchorIndex].Source.SystemId;
        return await Task.Run(() => row.ParentSystemId is int parent
            ? SqlServerDatabase.DragSubsection(_sqlConnectionString, _contractId,
                row.Source.SystemId, parent, after, _serverRevision)
            : SqlServerDatabase.DragSystem(_sqlConnectionString, _contractId,
                row.Source.SystemId, after, _serverRevision));
    }
}
