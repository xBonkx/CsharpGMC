using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SystemCopySourceTests
{
    private static EstimateRow Row(RowKind kind, int id, int system, int? parent = null) =>
        new(kind, 1, id, id, parent is not null, "", "Позиция", "шт",
            1m, 0m, 0m, null, false, "")
        { SystemId = system, ParentPositionId = parent };

    [Fact]
    public void SameDocumentCopyPreservesGroupsAndCrossDocumentAppends()
    {
        var rows = new[] { new SystemCopySource.Section(1, null), new SystemCopySource.Section(2, 1),
            new SystemCopySource.Section(3, null), new SystemCopySource.Section(4, 3) };
        Assert.Equal(new long[] { 1, 2, 10, 11, 3, 4 },
            SystemCopySource.InsertCopyOrder(rows, 1, 10, new long[] { 11 }, false));
        Assert.Equal(new long[] { 1, 2, 10, 3, 4 },
            SystemCopySource.InsertCopyOrder(rows, 2, 10, Array.Empty<long>(), true));
        Assert.Equal(new long[] { 1, 2, 3, 4, 10, 11 },
            SystemCopySource.InsertCopyOrder(rows, null, 10, new long[] { 11 }, false));
        Assert.Throws<InvalidOperationException>(() =>
            SystemCopySource.InsertCopyOrder(rows, 99, 10, Array.Empty<long>(), false));
    }

    [Fact]
    public void CopyDestinationMustBeSameObjectAndOrdinaryUnlockedDocument()
    {
        var doc = new DocRow(1, 8, "kp", "КП", "КП", "", "", "", false, 0m, 0m, 0m, 0);
        Assert.True(SystemCopySource.CanReceive(doc, 8));
        Assert.False(SystemCopySource.CanReceive(doc, 9));
        Assert.False(SystemCopySource.CanReceive(doc with { Locked = true }, 8));
        Assert.False(SystemCopySource.CanReceive(doc with { StageOf = 2 }, 8));
        Assert.False(SystemCopySource.CanReceive(doc with { StageRest = true }, 8));
    }

    [Fact]
    public void HeaderPositionComponentAndSubsectionResolveToOwningSystem()
    {
        var systems = new[] { Row(RowKind.System, 11, 11), Row(RowKind.System, 22, 22) };
        Assert.Equal(11, SystemCopySource.Resolve(systems[0], systems));
        Assert.Equal(11, SystemCopySource.Resolve(Row(RowKind.Position, 30, 11), systems));
        Assert.Equal(11, SystemCopySource.Resolve(Row(RowKind.Position, 31, 11, 30), systems));
        Assert.Equal(22, SystemCopySource.Resolve(Row(RowKind.Position, 40, 22), systems));
        Assert.Null(SystemCopySource.Resolve(Row(RowKind.Position, 41, 99), systems));
        Assert.Null(SystemCopySource.Resolve(null, systems));
        Assert.Null(SystemCopySource.Resolve(Row(RowKind.Position, -1, -1), systems));
    }
}
