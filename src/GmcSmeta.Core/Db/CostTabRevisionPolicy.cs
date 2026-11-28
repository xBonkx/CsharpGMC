namespace GmcSmeta.Core.Db;

public static class CostTabRevisionPolicy
{
    public static bool CanReuse(string expected, string cached, string observed, bool forced) =>
        !forced && !string.IsNullOrWhiteSpace(expected) && expected == cached && expected == observed;

    public static bool CanPublish(string expected, string current, string observed, int pendingWrites) =>
        pendingWrites == 0 && !string.IsNullOrWhiteSpace(expected) && expected == current && expected == observed;

    public static bool NeedsParentReadback(string expected, string current, string observed, int pendingWrites) =>
        pendingWrites == 0 && expected == current && !string.IsNullOrWhiteSpace(observed) && observed != current;
}
