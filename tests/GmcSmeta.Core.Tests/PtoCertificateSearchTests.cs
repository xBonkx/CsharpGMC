using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PtoCertificateSearchTests
{
    [Theory]
    [InlineData("  ",2)]
    [InlineData("КЛАПАН",1)]
    [InlineData("R-42",1)]
    [InlineData("производитель",1)]
    [InlineData("не найдено",0)]
    public void LocalFilterMatchesExistingSerializedSearch(string query,int expected)
    {
        var first=new PtoCertificate{Product="Клапан",Number="R-42",Manufacturer="Производитель",Revision=19};
        var second=new PtoCertificate{Product="Труба",Number="R-43",Revision=20};
        var rows=PtoCertificateSearch.Build(new[]{first,second});
        var result=PtoCertificateSearch.Filter(rows,query);
        Assert.Equal(expected,result.Count);
        if(expected==1){Assert.Same(first,result[0]);Assert.Equal(19,result[0].Revision);}
    }
}
