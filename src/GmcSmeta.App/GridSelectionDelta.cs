using System.Collections;

namespace GmcSmeta.App;

/// <summary>Restore exactly the desired visible row instances without
/// needlessly deselecting and selecting retained rows. Selection identity
/// mapping stays with each caller; this helper does not use SQL IDs.</summary>
internal static class GridSelectionDelta
{
    internal static void Restore<T>(IList selected, IEnumerable<T> desired) where T : class
    {
        var next = desired.Distinct<T>(ReferenceEqualityComparer.Instance).ToArray();
        var wanted = new HashSet<T>(next, ReferenceEqualityComparer.Instance);
        var existing = selected.OfType<T>().ToArray();
        var retained = new HashSet<T>(existing, ReferenceEqualityComparer.Instance);
        // If no row survives, clearing once is cheaper than thousands of
        // individual removals. Otherwise keep the existing selection intact.
        if (existing.Length != selected.Count || !existing.Any(wanted.Contains))
        {
            if (selected.Count != 0) selected.Clear();
            retained.Clear();
        }
        else
        {
            foreach (var row in existing)
                if (!wanted.Contains(row))
                {
                    selected.Remove(row);
                    retained.Remove(row);
                }
        }
        foreach (var row in next)
            if (retained.Add(row)) selected.Add(row);
    }
}
