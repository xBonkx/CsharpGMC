using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class WorkspaceDeletePresentationTests
{
    [Fact]
    public void EmptyPreviewHasNoDetails()
    {
        Assert.Empty(WorkspaceDeletePresentation.ContractDetails(0,0));
        Assert.Null(WorkspaceDeletePresentation.DocumentDetails(Array.Empty<string>()));
    }

    [Fact]
    public void PreviewRetainsOrderAndLimitsListToFourTitles()
    {
        Assert.Equal("документы: 5 (A, B, C, D …)",
            WorkspaceDeletePresentation.DocumentDetails(new[]{"A","B","C","D","E"}));
        Assert.Equal("документы: 2 (A, B)",WorkspaceDeletePresentation.DocumentDetails(new[]{"A","B"}));
    }

    [Fact]
    public void SignedAndMovementWarningsRetainCounts()
    {
        Assert.Equal(new[]{"акты КС-2/КС-3: 2","история закупок и выдачи: 3 записей"},
            WorkspaceDeletePresentation.ContractDetails(2,3));
        Assert.Equal("акты КС-2/КС-3: 2, из них подписанных: 1",WorkspaceDeletePresentation.ActDetails(2,1));
        Assert.Equal("акты КС-2/КС-3: 2",WorkspaceDeletePresentation.ActDetails(2,0));
    }
}
