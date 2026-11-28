using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class OrderAgreementPolicyTests
{
    private static AccessSite Site() => new(10, "Стройка", "Адрес", "Полное название",
        "Монтаж", "Папка", "Старый заказчик", "Старый номер", "01.01.2020");
    private static AccessCompany Company(long id, string name, string signer = "Директор") =>
        new(id, name, "", "", "", signer);
    private static OrderAgreementPolicy.Agreement Agreement(long id, long? company = 2) =>
        new(id, "А-77", "28.11.2026", company, "Полные реквизиты", "Новый заказчик");

    [Fact]
    public void OneActiveAgreementSuppliesExactLegalContextButNotTechnicalSite()
    {
        var resolved = OrderAgreementPolicy.Resolve(Site(), new[] { Agreement(100) });
        Assert.Equal(100L, resolved.AgreementId);
        Assert.Equal("А-77", resolved.ContractNumber);
        Assert.Equal("28.11.2026", resolved.ContractDate);
        Assert.Equal("Новый заказчик", resolved.Customer);
        Assert.Equal("Стройка", resolved.Name);
        Assert.Equal("Адрес", resolved.Address);
        Assert.Equal("Полное название", resolved.SiteFull);
        Assert.Equal("Монтаж", resolved.WorkTitle);
        Assert.Equal("Папка", resolved.Folder);
        Assert.Equal(2L, OrderAgreementPolicy.Company(resolved,
            new[] { Company(1, "ГМЦ"), Company(2, "Другой подрядчик") })!.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrAmbiguousAgreementNeverUsesFirstDocument(int count)
    {
        var resolved = OrderAgreementPolicy.Resolve(Site(),
            Enumerable.Range(1, count).Select(i => Agreement(i)).ToArray());
        Assert.Null(resolved.AgreementId);
        Assert.Empty(resolved.ContractNumber);
        Assert.Empty(resolved.ContractDate);
        Assert.Empty(resolved.Customer);
        Assert.NotNull(OrderAgreementPolicy.BlockReason(resolved, new[] { Company(2, "ГМЦ") }));
        Assert.Throws<InvalidOperationException>(() => OrderAgreementPolicy.Company(resolved,
            new[] { Company(2, "ГМЦ") }));
    }

    [Fact]
    public void MissingCompanyOrSignerDoesNotFallBackToAnotherCompany()
    {
        var resolved = OrderAgreementPolicy.Resolve(Site(), new[] { Agreement(100) });
        Assert.Throws<InvalidOperationException>(() => OrderAgreementPolicy.Company(resolved,
            new[] { Company(1, "ГМЦ") }));
        Assert.Throws<InvalidOperationException>(() => OrderAgreementPolicy.Company(resolved,
            new[] { Company(1, "ГМЦ"), Company(2, "Своя", "") }));
        Assert.NotNull(OrderAgreementPolicy.BlockReason(
            OrderAgreementPolicy.Resolve(Site(), new[] { Agreement(100, null) }),
            new[] { Company(1, "ГМЦ") }));
    }

    [Fact]
    public void LegacySelectionAndTechnicalFieldsStayUnchanged()
    {
        var site = Site();
        Assert.Null(OrderAgreementPolicy.BlockReason(site, Array.Empty<AccessCompany>()));
        Assert.Equal(2L, OrderAgreementPolicy.Company(site,
            new[] { Company(1, "Другая"), Company(2, "ГМЦ") })!.Id);
        Assert.Equal("Старый номер", site.ContractNumber);
    }

    [Fact]
    public void EmptyLegalCustomerAndNumberNeverUseOldObjectDefaults()
    {
        var resolved = OrderAgreementPolicy.Resolve(Site(), new[] {
            Agreement(100) with { Number = "", Date = "", CustomerFull = "", CustomerOrganization = "" }
        });
        Assert.Empty(resolved.Customer);
        Assert.Empty(resolved.ContractNumber);
        Assert.Empty(resolved.ContractDate);
    }
}
