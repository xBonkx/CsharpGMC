using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SignedPdfSelectionTests
{
    private static SignedPdfSelection.Entry Signed(long id, bool primary = false) =>
        new(id, "signed_contract", primary);

    [Fact]
    public void UnclassifiedPdfIsNotSignedEvenIfPrimaryFlagIsSet()
    {
        Assert.Null(SignedPdfSelection.DirectId(new[] { new SignedPdfSelection.Entry(1, "", true) }));
    }

    [Fact]
    public void UniqueSignedPdfOpensDirectly()
    {
        Assert.Equal(2L, SignedPdfSelection.DirectId(new[] { Signed(2) }));
    }

    [Fact]
    public void UniquePrimaryWinsAmongSignedPdfs()
    {
        Assert.Equal(3L, SignedPdfSelection.DirectId(new[] { Signed(2), Signed(3, true), Signed(4) }));
    }

    [Fact]
    public void AmbiguousSelectionRequiresDialog()
    {
        Assert.Null(SignedPdfSelection.DirectId(new[] { Signed(2), Signed(3) }));
        Assert.Null(SignedPdfSelection.DirectId(new[] { Signed(2, true), Signed(3, true) }));
        Assert.Null(SignedPdfSelection.DirectId(Array.Empty<SignedPdfSelection.Entry>()));
    }
}
