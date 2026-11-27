namespace GmcSmeta.App;

/// <summary>Pure selection reconciliation across estimate projections.
/// Virtual group/header IDs can be reassigned; use semantic keys instead.</summary>
internal static class EstimateSelectionRestore
{
    private readonly record struct Key(int Kind, int Id, string Text);
    private static Key Identity(EditableEstimateRow row) =>
        row.IsGroup ? new(2, 0, row.GroupKey) :
        row.IsTypeHeader ? new(3, 0, row.TypeKey) :
        row.IsPendingInsert ? new(4, row.Id, "") :
        row.IsSystem ? new(1, row.Source.SystemId, "") :
        new(0, row.Id, "");

    internal sealed record Result(EditableEstimateRow[] Selected,
        EditableEstimateRow? Primary, EditableEstimateRow? Current);

    internal static Result Find(IReadOnlyList<EditableEstimateRow> next,
        IReadOnlyList<EditableEstimateRow> selected, EditableEstimateRow? primary,
        EditableEstimateRow? current)
    {
        var wanted = selected.Select(Identity).ToHashSet();
        if (primary is not null) wanted.Add(Identity(primary));
        if (current is not null) wanted.Add(Identity(current));
        var found = new Dictionary<Key, EditableEstimateRow>();
        if (wanted.Count != 0)
            foreach (var row in next)
            {
                var key = Identity(row);
                if (wanted.Remove(key)) found.Add(key, row);
                if (wanted.Count == 0) break;
            }
        EditableEstimateRow? Map(EditableEstimateRow? row) =>
            row is null ? null : found.GetValueOrDefault(Identity(row));
        return new Result(selected.Select(Map).OfType<EditableEstimateRow>().ToArray(),
            Map(primary), Map(current));
    }
}
