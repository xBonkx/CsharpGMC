using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class NewAnnexInputTests
{
    [Theory]
    [InlineData("kp",1L,"")]
    [InlineData("dop",1L,"")]
    [InlineData("main",0L,"")]
    [InlineData("kp",null,"1")]
    [InlineData("dop",null,"1")]
    public void RejectsHiddenOrInvalidLegalFields(string kind,long? legal,string annex)
    {
        var input=new SqlServerWorkspace.NewContractData(kind,"1","01.01.2026","test",null,kind=="dop"?2L:null)
        {AgreementId=legal,AnnexNumber=annex};
        Assert.Throws<ArgumentException>(()=>SqlServerWorkspace.ValidateContract(input));
    }

    [Fact]
    public void KeepsLegalIdAndTrimsAnnexNumber()
    {
        var input=new SqlServerWorkspace.NewContractData("main","1","01.01.2026","test",null,null)
        {AgreementId=9,AnnexNumber=" A-1 "};
        var result=SqlServerWorkspace.ValidateContract(input);
        Assert.Equal(9L,result.AgreementId);
        Assert.Equal("A-1",result.AnnexNumber);
    }
}
