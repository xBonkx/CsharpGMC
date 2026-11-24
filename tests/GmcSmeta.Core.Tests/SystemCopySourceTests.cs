using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SystemCopySourceTests
{
    private static EstimateRow Row(RowKind kind, int id, int system, int? parent = null) =>
        new(kind, 1, id, id, parent is not null, "", "Позиция", "шт",
            1m, 0m, 0m, null, false, "")
        { SystemId = system, ParentPositionId = parent };

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
