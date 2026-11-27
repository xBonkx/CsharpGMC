using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ExcelImportAgreementPolicyTests
{
    private static ExcelImportChoice Choice(string kind, long? company = null, long? parent = null) =>
        new(new ExcelImportBlock { Sheet = "Смета" }, kind, "1", "27.11.2026", "Смета", parent, company);

    [Fact]
    public void AnnexUsesExplicitCompanyAndNotFirstDirectoryEntry()
    {
        var companies = new Dictionary<long, decimal> { [1] = 22m, [2] = 5m };
        Assert.Equal(((long?)2, 5m), ExcelImportAgreementPolicy.ResolveCompany(Choice("main", 2), 1, companies));
        Assert.Equal(((long?)1, 22m), ExcelImportAgreementPolicy.ResolveCompany(Choice("kp"), 1, companies));
        Assert.Equal(((long?)null, 22m), ExcelImportAgreementPolicy.ResolveCompany(Choice("kp"), null, companies));
        Assert.Throws<InvalidOperationException>(() => ExcelImportAgreementPolicy.ResolveCompany(Choice("main", 3), 1, companies));
    }

    [Theory]
    [InlineData("main", null, null)]
    [InlineData("main", 0L, null)]
    [InlineData("dop", 2L, null)]
    [InlineData("dop", null, 10L)]
    [InlineData("kp", null, 10L)]
    public void RejectsUnresolvedMetadata(string kind, long? company, long? parent) =>
        Assert.Throws<ArgumentException>(() => ExcelImportAgreementPolicy.Validate(Choice(kind, company, parent)));

    [Fact]
    public void DsCompanyMustStillMatchItsSelectedBase()
    {
        var choice = Choice("dop", 2, 10);
        ExcelImportAgreementPolicy.ValidateBaseCompany(choice, 2);
        Assert.Throws<InvalidOperationException>(() => ExcelImportAgreementPolicy.ValidateBaseCompany(choice, 1));
        Assert.Throws<InvalidOperationException>(() => ExcelImportAgreementPolicy.ValidateBaseCompany(choice, null));
    }
}
