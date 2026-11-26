using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageFilterAncestorsTests
{
    private static bool[] Previous(int[] depth, bool[] header, bool[] matches)
    {
        var keep = new bool[depth.Length];
        for (int i = 0; i < depth.Length; i++)
        {
            if (!matches[i]) continue;
            keep[i] = true;
            var d = depth[i];
            for (int j = i - 1; j >= 0 && d > 0; j--)
            {
                if (depth[j] >= d || !header[j]) continue;
                keep[j] = true;
                d = depth[j];
            }
        }
        return keep;
    }

    [Fact]
    public void RandomIrregularHierarchiesMatchFormerReverseScan()
    {
        var random = new Random(284);
        for (int sample = 0; sample < 100; sample++)
        {
            var depths = Enumerable.Range(0, 500).Select(_ => random.Next(-1, 7)).ToArray();
            var headers = depths.Select(_ => random.Next(4) == 0).ToArray();
            var matches = depths.Select(_ => random.Next(3) == 0).ToArray();
            Assert.Equal(Previous(depths, headers, matches),
                StageFilterAncestors.Mark(depths.Length, i => depths[i], i => headers[i], i => matches[i]));
        }
    }

    [Fact]
    public void LargeSingleSystemKeepsHeaderAndAllMatchedPositions()
    {
        const int count = 25001;
        int reads = 0;
        var keep = StageFilterAncestors.Mark(count, i => { reads++; return i == 0 ? 0 : 1; },
            i => i == 0, i => i > 0 && i % 2 == 0);
        Assert.True(keep[0]);
        Assert.Equal(12501, keep.Count(x => x));
        Assert.Equal(count, reads);
        Assert.Empty(StageFilterAncestors.Mark(0, _ => 0, _ => false, _ => false));
    }
}
