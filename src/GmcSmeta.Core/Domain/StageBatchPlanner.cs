namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure stage-mode selection planner (Python stage_mode.picked / put_rest /
/// take_out_all). It plans changes without mutating the source KP or SQL.
/// The SQL writer must independently recheck stage membership and all
/// quantities under one SERIALIZABLE transaction before using a plan.
/// </summary>
public static class StageBatchPlanner
{
    public enum Action { PutRemaining, ReturnSelected }

    public sealed record Snapshot(long PositionId, decimal FullQty, decimal Remaining,
        IReadOnlyList<decimal> PerStage);

    public sealed record Change(long PositionId, int StageIndex,
        decimal Before, decimal After);

    private const decimal Eps = StageAllocationPresentation.Epsilon;
    public const int MaxPositions = 10000;

    /// <summary>
    /// Only positive residual is transferred. A negative residual represents
    /// an existing over-allocation and must not silently *remove* a quantity
    /// when the user explicitly asked to put the remaining volume into a stage.
    /// </summary>
    public static IReadOnlyList<Change> Plan(
        IReadOnlyCollection<Snapshot> rows, IReadOnlyCollection<long> selected,
        int stageCount, Action action, int targetIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(selected);
        if (stageCount < 0 || stageCount > 1000)
            throw new ArgumentOutOfRangeException(nameof(stageCount));
        if (selected.Count == 0 || selected.Count > MaxPositions ||
            selected.Any(x => x <= 0) || selected.Distinct().Count() != selected.Count)
            throw new ArgumentException("Выбери реальные позиции без повторений (не больше 10 000).",
                nameof(selected));
        if (action is not (Action.PutRemaining or Action.ReturnSelected))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (action == Action.PutRemaining && (targetIndex < 0 || targetIndex >= stageCount))
            throw new ArgumentOutOfRangeException(nameof(targetIndex));

        var all = rows.ToDictionary(r => r.PositionId);
        if (selected.Any(id => !all.ContainsKey(id)))
            throw new InvalidOperationException("Состав выделенных строк изменился. Обнови смету.");

        var changes = new List<Change>();
        foreach (var id in selected.OrderBy(x => x))
        {
            var row = all[id];
            if (row.PerStage is null || row.PerStage.Count != stageCount ||
                row.FullQty < 0 || row.PerStage.Any(q => q < 0))
                throw new InvalidOperationException("Ошибка количества или состава этапов.");

            if (action == Action.PutRemaining)
            {
                if (row.Remaining <= Eps) continue;
                var current = row.PerStage[targetIndex];
                var updated = decimal.Round(current + row.Remaining, 6,
                    MidpointRounding.ToEven);
                if (updated > NumberText.MaxNumber)
                    throw new InvalidOperationException("Количество выходит за допустимый предел.");
                if (updated - current > Eps)
                    changes.Add(new Change(id, targetIndex, current, updated));
            }
            else
            {
                for (var i = 0; i < stageCount; i++)
                {
                    var current = row.PerStage[i];
                    if (current > Eps)
                        changes.Add(new Change(id, i, current, 0m));
                }
            }
        }
        return changes;
    }

    /// <summary>
    /// All selected positions must exist and match the client's full-precision
    /// snapshot, including allocations in *other* stages and the remainder.
    /// A mismatch blocks the whole batch rather than saving a subset.
    /// </summary>
    public static bool Matches(IReadOnlyCollection<Snapshot> expected,
        IReadOnlyCollection<Snapshot> actual, IReadOnlyCollection<long> selected,
        int stageCount)
    {
        if (expected is null || actual is null || selected is null ||
            selected.Count == 0 || selected.Count > MaxPositions ||
            selected.Distinct().Count() != selected.Count)
            return false;
        var wanted = selected.ToHashSet();
        if (wanted.Any(x => x <= 0) ||
            expected.Count != wanted.Count ||
            expected.Select(x => x.PositionId).Distinct().Count() != expected.Count ||
            expected.Any(x => !wanted.Contains(x.PositionId)))
            return false;

        var present = actual.Where(x => wanted.Contains(x.PositionId)).ToArray();
        if (present.Length != wanted.Count ||
            present.Select(x => x.PositionId).Distinct().Count() != present.Length)
            return false;
        var known = expected.ToDictionary(x => x.PositionId);
        foreach (var current in present)
        {
            var prev = known[current.PositionId];
            if (prev.PerStage is null || current.PerStage is null ||
                prev.PerStage.Count != stageCount ||
                current.PerStage.Count != stageCount ||
                Math.Abs(prev.FullQty - current.FullQty) > Eps ||
                Math.Abs(prev.Remaining - current.Remaining) > Eps)
                return false;
            for (var i = 0; i < stageCount; i++)
                if (Math.Abs(prev.PerStage[i] - current.PerStage[i]) > Eps)
                    return false;
        }
        return true;
    }
}
