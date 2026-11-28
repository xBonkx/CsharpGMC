using System.Data;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentGroupedReadTests
{
    [Fact]
    public void DeltaRequiresExactCandidatesAndUniqueSubsetOfLines()
    {
        Assert.True(SqlServerShipments.CanPublishChangedRows([1, 2], [2, 1], [1]));
        Assert.False(SqlServerShipments.CanPublishChangedRows([1, 2], [1, 1], [1]));
        Assert.False(SqlServerShipments.CanPublishChangedRows([1, 2], [1, 3], [1]));
        Assert.False(SqlServerShipments.CanPublishChangedRows([1, 2], [1, 2], [3]));
        Assert.False(SqlServerShipments.CanPublishChangedRows([1, 2], [1, 2], [1, 1]));
        Assert.False(SqlServerShipments.CanPublishChangedRows([1, 1], [1, 1], []));
    }

    [Fact]
    public void ExactProductionCommandsHaveScopedGroupedMovementsAndTypedIds()
    {
        using var conn = new SqlConnection();
        using var candidates = SqlServerShipments.CreateIssuableReadCommand(conn, 42, [12, 13]);
        using var lines = SqlServerShipments.CreateLinesReadCommand(conn, 99, [12, 13]);
        foreach (var cmd in new[] { candidates, lines })
        {
            Assert.Contains("GROUP BY m.position_id", cmd.CommandText);
            Assert.Contains("p.id IN (@p0,@p1)", cmd.CommandText);
            Assert.Equal(3, cmd.Parameters.Count);
            Assert.Equal(SqlDbType.BigInt, cmd.Parameters["@p0"].SqlDbType);
            Assert.Equal(12L, cmd.Parameters["@p0"].Value);
        }
        Assert.Contains("c.kind IN (N'main',N'dop')", candidates.CommandText);
        Assert.Contains("p.root_order,p.root_id", candidates.CommandText);
        Assert.Contains("sl.shipment_id=@id", lines.CommandText);
        Assert.DoesNotContain("c.kind IN", lines.CommandText);
    }
}
