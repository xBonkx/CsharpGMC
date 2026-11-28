namespace GmcSmeta.Core.Domain;

/// <summary>Value snapshot shared by directory menu and toolbar commands.</summary>
public static class PeopleEntrySelection
{
    public static bool Matches(
        IReadOnlyCollection<(long Id, bool Active, string Name)> captured,
        IReadOnlyCollection<(long Id, bool Active, string Name)> current,
        string revision, string currentRevision) =>
        !string.IsNullOrWhiteSpace(revision) &&
        string.Equals(revision, currentRevision, StringComparison.Ordinal) &&
        captured.Count > 0 && captured.Count == current.Count &&
        captured.All(x => x.Id > 0) &&
        captured.Select(x => x.Id).Distinct().Count() == captured.Count &&
        captured.OrderBy(x => x.Id).SequenceEqual(current.OrderBy(x => x.Id));

    public static long[] ChangedIds(
        IEnumerable<(long Id, bool Active, string Name)> captured, bool active) =>
        captured.Where(x => x.Active != active).Select(x => x.Id).ToArray();
}
