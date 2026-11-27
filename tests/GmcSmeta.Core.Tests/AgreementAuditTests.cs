using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AgreementAuditTests
{
    private static DocRow Main(int id, int objectId = 12, string number = "Д-1") =>
        new(id, objectId, "main", "Договор", "Договор", "Смета", number, "23.01.2025",
            false, 0m, 0m, 0m, 0) { CompanyId = 8 };

    [Fact]
    public void ConsistentGroupWithLinkedDsAndKpHasNoFindings()
    {
        var one = Main(1);
        var ds = Main(2) with { Kind = "dop", BaseContractId = 1, Number = "4" };
        var kp = Main(3) with { Kind = "kp", CompanyId = null, Number = "" };
        Assert.Empty(AgreementAudit.Inspect(new[] { one, ds, kp, Main(4, objectId: 99) }));
    }

    [Fact]
    public void SameNumberDifferentCompanyIsFlaggedForExistingKsScopeReview()
    {
        var result = AgreementAudit.Inspect(new[] { Main(1), Main(2) with { CompanyId = 9 } });
        Assert.Contains(result, f => f.Code == "KS_SCOPE_REVIEW" && f.DocumentIds.SequenceEqual(new[] { 1, 2 }));
        Assert.Contains(result, f => f.Code == "MULTIPLE_COMPANIES");
        Assert.Contains(result, f => f.Code == "UNRESOLVED_GROUP");
    }

    [Fact]
    public void MissingAndCrossCardLinksAreNotGuessed()
    {
        var rows = new[] { Main(1, number: ""), Main(2, objectId: 99),
            Main(3) with { Kind = "dop", BaseContractId = 2 },
            Main(4) with { Kind = "dop", CompanyId = null } };
        var result = AgreementAudit.Inspect(rows);
        Assert.Contains(result, f => f.Code == "EMPTY_NUMBER" && f.ObjectId == 12);
        Assert.Contains(result, f => f.Code == "INVALID_DS_BASE" && f.DocumentIds.SequenceEqual(new[] { 3 }));
        Assert.Contains(result, f => f.Code == "UNLINKED_DS" && f.DocumentIds.SequenceEqual(new[] { 4 }));
        Assert.Contains(result, f => f.Code == "MISSING_COMPANY");
        Assert.DoesNotContain(result, f => f.ObjectId == 99);
    }
}
