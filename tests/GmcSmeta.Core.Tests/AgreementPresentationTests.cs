using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AgreementPresentationTests
{
    private static DocRow Main(int id, string number = "Д-1", string date = "23.01.2025") =>
        new(id, 12, "main", "Договор", "Договор № " + number, "Смета " + id, number, date,
            false, 0m, 0m, 0m, 0);

    [Fact]
    public void SameDisplayedRequisitesYieldHeadingWithoutChangingOriginalTitle()
    {
        var one = Main(1);
        var two = Main(2);
        var ds = Main(3) with { Kind = "dop", Number = "4", BaseContractId = 2 };
        Assert.Equal("Договор № Д-1 от 23.01.2025", AgreementPresentation.Heading(
            new[] { (one, "ООО А"), (two, "ООО А"), (ds, "ООО А") }));
        Assert.Equal("Смета 1", AgreementPresentation.AnnexTitle(one));
        Assert.Contains("Д-1", AgreementPresentation.NavigationName(one));
        Assert.Equal("Договор № Д-1", one.Title);
    }

    [Fact]
    public void ConflictsMissingRequisitesAndUnlinkedDsDoNotInventHeading()
    {
        var one = Main(1);
        Assert.Null(AgreementPresentation.Heading(new[] { (one, "А"), (Main(2, "Д-2"), "А") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (one, "А"), (Main(2, date: "24.01.2025"), "А") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (one, "А"), (Main(2), "Б") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (Main(1, ""), "А") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (one, "") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (one, "А"), (Main(3) with { Kind = "dop" }, "А") }));
        Assert.Null(AgreementPresentation.Heading(new[] { (Main(3) with { Kind = "kp" }, "А") }));
        Assert.Contains("ID 1", AgreementPresentation.AnnexTitle(one with { Name = "" }));
    }
}
