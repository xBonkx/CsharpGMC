using GmcSmeta.Core.Db;
using static GmcSmeta.Core.Db.SqlServerWorkspace;

namespace GmcSmeta.Core.Tests;

public sealed class ContractTransferPolicyTests
{
    [Fact]
    public void MainAndLinkedAgreementsAccepted() =>
        ValidateTransferGroup(1,new TransferDocument[]{new(1,10,"main",null),new(2,10,"dop",null)});

    [Theory]
    [InlineData(11,"dop",null)]
    [InlineData(10,"kp",null)]
    [InlineData(10,"dop",9L)]
    public void CrossObjectWrongKindOrExternalStageRejected(long obj,string kind,long? stage) =>
        Assert.Throws<InvalidOperationException>(()=>ValidateTransferGroup(1,
            new TransferDocument[]{new(1,10,"main",null),new(2,obj,kind,stage)}));

    [Fact]
    public void ProductionNeverOpened() =>
        Assert.Throws<InvalidOperationException>(()=>DescribeContractTransfer(
            "Server=localhost;Database=GmcSmetaPilot_Cutover_Production;Integrated Security=true",1));
}
