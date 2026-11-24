using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateDragPlanTests
{
    private static readonly EstimateDragPlan.Section[] Sections =
    {
        new(1, null, 0, "П1"), new(2, 1, 1, "П1-1"),
        new(3, null, 2, "В1"), new(4, 3, 3, "В1-1"),
    };
    private static readonly EstimateDragPlan.Position[] Positions =
    {
        new(10, 1, null, 0), new(11, 1, 10, 1),
        new(12, 1, null, 2), new(20, 3, null, 0),
        new(21, 3, 20, 1), new(22, 3, null, 2)
    };

    [Fact]
    public void ComponentSelectionMovesWholeRootAndEverySibling()
    {
        var p = EstimateDragPlan.PlacePositions(Positions, Sections,
            new long[] { 11 }, 3, 20);
        Assert.True(p.Changed);
        Assert.Equal(new long[] { 10 }, p.MovedRoots);
        Assert.Equal(new long[] { 20, 21, 10, 11, 22 },
            p.Assignments.Where(x => x.SystemId == 3)
                .OrderBy(x => x.Order).Select(x => x.Id));
        Assert.Equal(new long[] { 12 },
            p.Assignments.Where(x => x.SystemId == 1).Select(x => x.Id));
    }

    [Fact]
    public void MultipleRootsPreserveUserSelectionOrder()
    {
        var p = EstimateDragPlan.PlacePositions(Positions, Sections,
            new long[] { 22, 12 }, 3, null);
        Assert.Equal(new long[] { 22, 12, 20 },
            p.Assignments.Where(x => x.SystemId == 3 && x.Id is 22 or 12 or 20)
                .OrderBy(x => x.Order).Select(x => x.Id));
        Assert.Equal(Positions.Length, p.Assignments.Count);
    }

    [Fact]
    public void MovingAfterSelfIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EstimateDragPlan.PlacePositions(Positions, Sections,
                new long[] { 11 }, 1, 10));
    }

    [Fact]
    public void MovingSystemMovesItsSubsectionsTogether()
    {
        var moved = EstimateDragPlan.PlaceGroup(Sections, 1, 3);
        Assert.True(moved.Changed);
        Assert.Equal(new long[] { 3, 4, 1, 2 },
            moved.Assignments.OrderBy(x => x.Order).Select(x => x.Id));
        Assert.Equal(1, moved.Assignments.Single(x => x.Id == 2).ParentId);
    }

    [Fact]
    public void MovingSubsectionToDifferentSystemReparentsOnlySubsection()
    {
        var moved = EstimateDragPlan.PlaceSubsection(Sections, 2, 3, 4);
        Assert.True(moved.Changed);
        Assert.Equal(3, moved.Assignments.Single(x => x.Id == 2).ParentId);
        Assert.Equal(new long[] { 1, 3, 4, 2 },
            moved.Assignments.OrderBy(x => x.Order).Select(x => x.Id));
    }

    [Fact]
    public void ReparentingIsDifferentFromReorderingWithinOneSystem()
    {
        var reordered = EstimateDragPlan.PlaceSubsection(Sections, 2, 1, null);
        Assert.False(EstimateDragPlan.ReparentsSection(Sections, reordered, 2));

        var moved = EstimateDragPlan.PlaceSubsection(Sections, 2, 3, null);
        Assert.True(EstimateDragPlan.ReparentsSection(Sections, moved, 2));
        Assert.False(EstimateDragPlan.ReparentsSection(Sections, moved, 4));
    }

    [Fact]
    public void DraggingSubsectionAsSystemMovesWholeParentGroupLikePython()
    {
        var moved = EstimateDragPlan.PlaceGroup(Sections, 2, 4);
        Assert.Equal(new long[] { 3, 4, 1, 2 },
            moved.Assignments.OrderBy(x => x.Order).Select(x => x.Id));
        Assert.Equal(1, moved.Assignments.Single(x => x.Id == 2).ParentId);
    }

    [Fact]
    public void InvalidSectionTreeIsRejectedBeforeDrag()
    {
        var bad = new[]
        {
            new EstimateDragPlan.Section(1, 2, 0, "Цикл"),
            new EstimateDragPlan.Section(2, 1, 1, "Цикл")
        };
        Assert.Throws<InvalidOperationException>(() =>
            EstimateDragPlan.PlaceGroup(bad, 1, null));
    }

    [Fact]
    public void SubsectionCanGoFirstUnderNewSystem()
    {
        var moved = EstimateDragPlan.PlaceSubsection(Sections, 2, 3, null);
        Assert.Equal(new long[] { 1, 3, 2, 4 },
            moved.Assignments.OrderBy(x => x.Order).Select(x => x.Id));
    }

    [Fact]
    public void RejectForeignPositionAndUnrelatedAnchor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EstimateDragPlan.PlacePositions(Positions, Sections, new long[] { 999 }, 3, null));
        Assert.Throws<InvalidOperationException>(() =>
            EstimateDragPlan.PlacePositions(Positions, Sections, new long[] { 10 }, 3, 12));
        Assert.Throws<InvalidOperationException>(() =>
            EstimateDragPlan.PlaceGroup(Sections, 1, 2));
    }

    [Fact]
    public void SamePlaceDoesNotForceRevisionBump()
    {
        var plan = EstimateDragPlan.PlacePositions(Positions, Sections,
            new long[] { 10 }, 1, null);
        Assert.False(plan.Changed);
        var sections = EstimateDragPlan.PlaceGroup(Sections, 1, null);
        Assert.False(sections.Changed);
    }

    [Fact]
    public void LargeDocumentCanBeOrderedWithoutDroppingAnyIds()
    {
        var rows = Enumerable.Range(1, 2501)
            .Select(i => new EstimateDragPlan.Position(i, 1, null, i-1)).ToArray();
        var plan = EstimateDragPlan.PlacePositions(rows,
            new[] { new EstimateDragPlan.Section(1, null, 0, "П1") },
            new long[] { 2501 }, 1, null);
        Assert.Equal(2501, plan.Assignments.Count);
        Assert.Equal(2501L, plan.Assignments.OrderBy(x => x.Order).First().Id);
        Assert.Equal(2501, plan.Assignments.Select(x => x.Id).Distinct().Count());
    }
    [Fact]
    public void TenThousandRowsMoveInOneStableTreeWithoutChangingIdentities()
    {
        // Large real-world estimates mix top-level positions and components.
        // Every position ID must appear exactly once, no child can be orphaned,
        // and the plan must not rely on 2,100 SQL parameters.
        var sections = new[]
        {
            new EstimateDragPlan.Section(1, null, 0, "Приток"),
            new EstimateDragPlan.Section(2, null, 1, "Вытяжка")
        };
        var rows = new List<EstimateDragPlan.Position>(10_000);
        for (var i = 0; i < 5_000; i++)
        {
            var root = 2L * i + 1L;
            rows.Add(new EstimateDragPlan.Position(root, 1, null, 2 * i));
            rows.Add(new EstimateDragPlan.Position(root + 1, 1, root, 2 * i + 1));
        }
        var selected = new long[] { 1, 500, 9_999 };
        var moved = EstimateDragPlan.PlacePositions(rows, sections, selected, 2, null);

        Assert.True(moved.Changed);
        Assert.Equal(new long[] { 1, 499, 9_999 }, moved.MovedRoots);
        Assert.Equal(10_000, moved.Assignments.Count);
        Assert.Equal(10_000, moved.Assignments.Select(x => x.Id).Distinct().Count());
        var byId = moved.Assignments.ToDictionary(x => x.Id);
        foreach (var child in rows.Where(x => x.ParentId is not null))
        {
            Assert.Equal(byId[child.ParentId!.Value].SystemId, byId[child.Id].SystemId);
            Assert.Equal(byId[child.ParentId.Value].Order + 1, byId[child.Id].Order);
        }
        var target = moved.Assignments.Where(x => x.SystemId == 2)
            .OrderBy(x => x.Order).Select(x => x.Id).ToArray();
        Assert.Equal(new long[] { 1, 2, 499, 500, 9_999, 10_000 }, target);
        Assert.Equal(9_994, moved.Assignments.Count(x => x.SystemId == 1));
    }

    [Fact]
    public void MovingLastRootsToOtherSectionKeepsTheirSourceOrder()
    {
        var rows = new[]
        {
            new EstimateDragPlan.Position(1, 1, null, 0),
            new EstimateDragPlan.Position(2, 1, null, 1),
            new EstimateDragPlan.Position(3, 2, null, 0),
            new EstimateDragPlan.Position(4, 2, null, 1)
        };
        var sections = new[]
        {
            new EstimateDragPlan.Section(1, null, 0, "A"),
            new EstimateDragPlan.Section(2, null, 1, "B")
        };
        var result = EstimateDragPlan.PlacePositions(rows, sections, new long[] { 1, 2 }, 2, 4);
        Assert.Equal(new long[] { 3, 4, 1, 2 }, result.Assignments
            .Where(x => x.SystemId == 2).OrderBy(x => x.Order).Select(x => x.Id));
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Assignments
            .Where(x => x.SystemId == 2).OrderBy(x => x.Order).Select(x => x.Order));
    }

}
