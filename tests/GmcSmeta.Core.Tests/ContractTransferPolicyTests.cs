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
    public void SameActiveLegalIdentityAccepted() =>
        Assert.Equal(42L,ValidateTransferLegalGroup(new long?[]{42,42},new long[]{42,43}));

    [Theory]
    [InlineData(null,42L)]
    [InlineData(42L,null)]
    [InlineData(42L,43L)]
    [InlineData(99L,99L)]
    public void MissingMixedOrForeignLegalIdentityRejected(long? first,long? second) =>
        Assert.Throws<InvalidOperationException>(()=>ValidateTransferLegalGroup(new[]{first,second},new long[]{42,43}));

    [Theory]
    [InlineData(1,false)]
    [InlineData(2,false)]
    [InlineData(0,true)]
    public void ExistingDestinationOrLegalFilesCannotBeSilentlyReassigned(int count,bool files) =>
        Assert.Throws<InvalidOperationException>(()=>SqlServerAgreements.ValidateTransferDestination(count,files));

    [Fact]
    public void EmptyDestinationWithoutLegalFilesAccepted() =>
        SqlServerAgreements.ValidateTransferDestination(0,false);

    [Fact]
    public void ProductionNeverOpened() =>
        Assert.Throws<InvalidOperationException>(()=>DescribeContractTransfer(
            "Server=localhost;Database=GmcSmetaPilot_Cutover_Production;Integrated Security=true",1));
}
