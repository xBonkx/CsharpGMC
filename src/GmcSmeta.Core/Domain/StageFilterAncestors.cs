namespace GmcSmeta.Core.Domain;

/// <summary>Keep matched rows and the same earlier lower-depth headers that
/// the former reverse scan selected. Ordinary rows never become ancestors.</summary>
public static class StageFilterAncestors
{
    public static bool[] Mark(int count, Func<int, int> depthOf,
        Func<int, bool> isHeader, Func<int, bool> matches)
    {
        var keep = new bool[count];
        var parents = new int[count];
        var headers = new List<(int Index, int Depth)>();
        for (int i = 0; i < count; i++)
        {
            var depth = depthOf(i);
            // Find the last preceding eligible header with smaller depth.
            // Header stack is strictly increasing in depth. Non-headers do
            // not prune it: this also preserves the old irregular-depth rule.
            int lo = 0, hi = headers.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (headers[mid].Depth < depth) lo = mid + 1;
                else hi = mid;
            }
            parents[i] = depth > 0 && lo > 0 ? headers[lo - 1].Index : -1;
            keep[i] = matches(i);
            if (isHeader(i))
            {
                if (lo < headers.Count) headers.RemoveRange(lo, headers.Count - lo);
                headers.Add((i, depth));
            }
        }
        // Every parent precedes its child; propagate once instead of
        // rewalking the same long prefix for each matched position.
        for (int i = count - 1; i >= 0; i--)
            if (keep[i] && parents[i] >= 0) keep[parents[i]] = true;
        return keep;
    }
}
