namespace GmcSmeta.Core.Domain;

/// <summary>Pure view layout policy for the estimate grid. No document or SQL state.</summary>
public static class EstimateColumnLayout
{
    public enum Role { Fixed, Name, Note }
    public sealed record Column(string Key, double Preferred, double Minimum, Role Kind);

    /// <summary>
    /// Give up space in Note first, then Name, then fixed columns above their
    /// readable minima. Preserve minima even if the viewport is narrower:
    /// horizontal scrolling is preferable to unreadable amounts.
    /// Distribute extra space to Note, falling back to Name when Note is hidden.
    /// Preferred widths are inputs only; callers persist manual changes separately.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Fit(
        IReadOnlyList<Column> columns, double available)
    {
        if (!double.IsFinite(available)) throw new ArgumentOutOfRangeException(nameof(available));
        if (columns.Any(c => !double.IsFinite(c.Preferred) ||
                             !double.IsFinite(c.Minimum) || c.Minimum <= 0))
            throw new ArgumentException("Invalid column dimensions.", nameof(columns));

        var sizes = columns.ToDictionary(c => c.Key, c => Math.Max(c.Minimum, c.Preferred));
        var excess = sizes.Values.Sum() - Math.Max(0, available);
        if (excess > 0)
        {
            foreach (var c in columns.OrderBy(c => c.Kind switch
                     { Role.Note => 0, Role.Name => 1, _ => 2 })
                     .ThenByDescending(c => sizes[c.Key]))
            {
                var take = Math.Min(excess, sizes[c.Key] - c.Minimum);
                sizes[c.Key] -= take;
                excess -= take;
                if (excess <= 0.001) break;
            }
        }
        else if (excess < 0)
        {
            var target = columns.FirstOrDefault(c => c.Kind == Role.Note) ??
                         columns.FirstOrDefault(c => c.Kind == Role.Name);
            if (target is not null) sizes[target.Key] -= excess;
        }
        return sizes;
    }
}
