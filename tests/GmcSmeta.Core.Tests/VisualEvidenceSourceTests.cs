namespace GmcSmeta.Core.Tests;

public sealed class VisualEvidenceSourceTests
{
    [Fact]
    public void PairedEvidenceInventoryCannotClaimUntestedAcceptance()
    {
        var s = Source("tools/local-qa/visual_evidence.py");
        foreach (var file in new[] { "icon-candidates.csv", "paired-captures.csv", "keyboard-cases.csv",
            "menu-scale-cases.csv", "dialog-cases.csv" })
            Assert.Contains(file, s);
        Assert.Contains("status=\"NOT_RUN\"", s);
        Assert.Contains("exist_ok=False", s);
        Assert.Contains("python_sha", s);
        Assert.Contains("csharp_sha", s);
        Assert.Contains("sql_before", s);
        Assert.Contains("sql_after", s);
    }

    [Fact]
    public void MenuEvidenceUsesRealStyleAndAutomationPeer()
    {
        var s = Source("tests/GmcSmeta.App.Checks/MenuBehaviorChecks.cs");
        Assert.Contains("ContextMenuIcons.Create", s);
        Assert.Contains("MenuItemAutomationPeer", s);
        Assert.Contains("ElementNotEnabledException", s);
        Assert.Contains("clicks != 0", s);
        Assert.Contains("MenuBehaviorChecks.Run();", Source("tests/GmcSmeta.App.Checks/Program.cs"));
        Assert.Contains("ExportMenuFixtures(output);", Source("tests/GmcSmeta.App.Checks/VisualAuditChecks.cs"));
    }

    private static string Source(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, relative));
    }
}
