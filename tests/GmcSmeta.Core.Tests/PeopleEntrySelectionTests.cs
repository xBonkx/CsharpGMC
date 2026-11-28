using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleEntrySelectionTests
{
    private static readonly (long Id, bool Active, string Name)[] Entries =
        [(1, true, "Первый"), (2, false, "Второй"), (3, true, "Третий")];

    [Fact]
    public void SameSelectionInDifferentOrderIsAccepted() =>
        Assert.True(PeopleEntrySelection.Matches(Entries, Entries.Reverse().ToArray(), "r", "r"));

    [Fact]
    public void ChangedSelectionOrStatusOrNameIsRejected()
    {
        Assert.False(PeopleEntrySelection.Matches(Entries, Entries[..2], "r", "r"));
        Assert.False(PeopleEntrySelection.Matches(Entries,
            [(1, false, "Первый"), Entries[1], Entries[2]], "r", "r"));
        Assert.False(PeopleEntrySelection.Matches(Entries,
            [(1, true, "Другой"), Entries[1], Entries[2]], "r", "r"));
    }

    [Fact]
    public void StaleEmptyDuplicateAndInvalidSnapshotsAreRejected()
    {
        Assert.False(PeopleEntrySelection.Matches(Entries, Entries, "r", "s"));
        Assert.False(PeopleEntrySelection.Matches(Entries, Entries, "", ""));
        Assert.False(PeopleEntrySelection.Matches([], [], "r", "r"));
        Assert.False(PeopleEntrySelection.Matches([Entries[0], Entries[0]], [Entries[0], Entries[0]], "r", "r"));
        Assert.False(PeopleEntrySelection.Matches([(0, true, "X")], [(0, true, "X")], "r", "r"));
    }

    [Fact]
    public void MixedSelectionOnlyChangesRowsNeedingRequestedStatus()
    {
        Assert.Equal(new long[] { 1, 3 }, PeopleEntrySelection.ChangedIds(Entries, false));
        Assert.Equal(new long[] { 2 }, PeopleEntrySelection.ChangedIds(Entries, true));
        Assert.Empty(PeopleEntrySelection.ChangedIds([Entries[0]], true));
    }
}
