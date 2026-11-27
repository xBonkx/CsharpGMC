using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Read-only clipboard projection in authoritative document order.
/// The caller supplies selection identity; no SQL or UI dependencies.</summary>
public static class EstimateClipboardProjection
{
    public static List<CopiedRow> Build<T>(IReadOnlyList<T> rows,
        Func<T, EstimateRow> source, Func<T, bool> selected)
    {
        // Only selected section IDs need an index of their top-level rows.
        var sections = new Dictionary<int, List<int>>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = source(rows[i]);
            if (row.IsSystem && selected(rows[i]))
                sections.TryAdd(row.SystemId, new List<int>());
        }
        if (sections.Count > 0)
            for (var i = 0; i < rows.Count; i++)
            {
                var row = source(rows[i]);
                if (!row.IsSystem && row.ParentPositionId is null &&
                    sections.TryGetValue(row.SystemId, out var tops))
                    tops.Add(i);
            }

        var result = new List<CopiedRow>();
        var emittedTops = new HashSet<int>();

        void EmitTop(int index)
        {
            var top = source(rows[index]);
            if (!emittedTops.Add(top.Id)) return;
            var children = new List<CopiedChild>();
            for (var i = index + 1; i < rows.Count; i++)
            {
                var child = source(rows[i]);
                if (child.IsSystem || child.ParentPositionId != top.Id) break;
                children.Add(new CopiedChild(child.Name, child.Unit, child.Qty, child.Note));
            }
            result.Add(new CopiedRow(top.Name, top.Unit, top.Qty, top.Price,
                top.PurchasePrice, top.Note, top.Formulas, children) { WorkPrice = top.WorkPrice });
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = source(rows[i]);
            if (row.IsSystem)
            {
                if (selected(rows[i]) && sections.TryGetValue(row.SystemId, out var tops))
                    foreach (var top in tops) EmitTop(top);
                continue;
            }

            if (row.ParentPositionId is int parent)
            {
                if (!emittedTops.Contains(parent) && selected(rows[i]))
                    result.Add(new CopiedRow(row.Name, row.Unit, row.Qty,
                        0m, null, row.Note, row.Formulas, null));
            }
            else if (selected(rows[i])) EmitTop(i);
        }
        return result;
    }
}
