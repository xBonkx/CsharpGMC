using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleInlineEditTests
{
    private static PeoplePerson Person() => new(12, "Иванов Иван Иванович", "инженер", "", "",
        "", "", true, 3) { Selected = true };

    [Fact]
    public void InlineChangeDoesNotMutateSourceOrTurnAutoGenitiveIntoManual()
    {
        var original = Person();
        var changed = (PeoplePerson)PeopleInlineEdit.Change(original, nameof(PeoplePerson.Fio), "Петров Петр Петрович");
        Assert.Equal("Иванов Иван Иванович", original.Fio);
        Assert.Equal("", changed.FioGen);
        Assert.Equal("Петрова Петра Петровича", changed.DisplayGenitive);
        Assert.True(changed.Selected);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.SortOrder, changed.SortOrder);
    }

    [Fact]
    public void ManualGenitiveCanBeClearedAndIsOtherwisePreserved()
    {
        var original = Person() with { FioGen = "Ручной вариант" };
        var changed = (PeoplePerson)PeopleInlineEdit.Change(original, nameof(PeoplePerson.Fio), "Петров Петр Петрович");
        Assert.Equal("Ручной вариант", changed.DisplayGenitive);
        var cleared = (PeoplePerson)PeopleInlineEdit.Change(changed, nameof(PeoplePerson.FioGen), "");
        Assert.Equal("", PeopleInlineEdit.Read(cleared, nameof(PeoplePerson.FioGen)));
        Assert.Equal("Петрова Петра Петровича", cleared.DisplayGenitive);
    }

    [Theory]
    [InlineData("31.02.1990")]
    [InlineData("invalid")]
    public void RejectsInvalidBirth(string value) =>
        Assert.Throws<ArgumentException>(() => PeopleInlineEdit.Change(Person(), nameof(PeoplePerson.Birth), value));

    [Fact]
    public void NormalizesBirthAndPlateAndRejectsEmptyRequiredFields()
    {
        var person = (PeoplePerson)PeopleInlineEdit.Change(Person(), nameof(PeoplePerson.Birth), "2/3/1990");
        Assert.Equal("02.03.1990", person.Birth);
        Assert.Throws<ArgumentException>(() => PeopleInlineEdit.Change(Person(), nameof(PeoplePerson.Fio), " "));
        var car = new PeopleCar(7, "", "a123bc", "", true, 2);
        Assert.Throws<ArgumentException>(() => PeopleInlineEdit.Change(car, nameof(PeopleCar.Plate), ""));
        var changed = (PeopleCar)PeopleInlineEdit.Change(car, nameof(PeopleCar.Plate), " b456cd ");
        Assert.Equal("B456CD", changed.Plate);
        Assert.Equal("a123bc", car.Plate);
    }
}
