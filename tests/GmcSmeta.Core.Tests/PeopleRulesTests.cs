using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleRulesTests
{
    [Theory]
    [InlineData("Иванов Иван Иванович", "Иванова Ивана Ивановича")]
    [InlineData("Красуцкий Александр Сергеевич", "Красуцкого Александра Сергеевича")]
    [InlineData("Петрова Мария Ивановна", "Петровой Марии Ивановны")]
    [InlineData("Ли Анна", "Ли Анны")]
    public void DeclinesFioLikePython(string original, string expected) =>
        Assert.Equal(expected, PeopleRules.FioGenitive(original));

    [Theory]
    [InlineData("прораб", "прораба")]
    [InlineData("главный инженер", "главного инженера")]
    [InlineData("инженер ПТО", "инженера ПТО")]
    [InlineData("производитель работ", "производителя работ")]
    public void DeclinesPostLikePython(string original, string expected) =>
        Assert.Equal(expected, PeopleRules.PostGenitive(original));

    [Theory]
    [InlineData("03/03-01", "03/03-02")]
    [InlineData("03/03-09", "03/03-10")]
    [InlineData("8/11-099", "8/11-100")]
    [InlineData("без номера", "без номера")]
    public void IncrementsLastNumberKeepingWidth(string original, string expected) =>
        Assert.Equal(expected, PeopleRules.NextNumber(original));

    [Fact]
    public void FioDuplicateKeyIgnoresWhitespaceAndYo() =>
        Assert.Equal(PeopleRules.PersonKey(" Ёлкин   Иван  "),
            PeopleRules.PersonKey("Елкин Иван"));

    [Fact]
    public void PlateKeyUnifiesRussianAndLatinLetters() =>
        Assert.Equal(PeopleRules.PlateKey(" А123ВС 25 "),
            PeopleRules.PlateKey("A123BC25"));

    [Theory]
    [InlineData("15111978", "15.11.1978")]
    [InlineData("15/11/1978", "15.11.1978")]
    [InlineData("15.11.78", "15.11.1978")]
    [InlineData("15.11.25", "15.11.2025")]
    [InlineData("ошибка", "ошибка")]
    public void NormalizesDate(string input, string expected) =>
        Assert.Equal(expected, PeopleRules.BirthNorm(input));

    [Fact]
    public void SortsManagersBeforeWorkersWhenNoManualOrder()
    {
        var rows = new[]
        {
            new PeoplePerson(1,"Рабочий А","монтажник","","","","",true,0),
            new PeoplePerson(2,"ИТР Б","главный инженер","","","","",true,0)
        };
        Assert.Equal(2L, PeopleRules.SortPeople(rows)[0].Id);
    }

    [Theory]
    [InlineData("генеральный директор", 0)]
    [InlineData("ген. директор", 0)]
    [InlineData("директор", 1)]
    [InlineData("главный инженер", 2)]
    [InlineData("начальник участка", 3)]
    [InlineData("производитель работ", 4)]
    [InlineData("прораб", 4)]
    [InlineData("мастер", 5)]
    [InlineData("инженер ПТО", 6)]
    [InlineData("ПТО", 6)]
    [InlineData("снабженец", 7)]
    [InlineData("кладовщик", 8)]
    [InlineData("монтажник", 20)]
    [InlineData("", 20)]
    [InlineData(null, 20)]
    public void PostRankPreservesPrecedenceAndFallback(string? post, int expected) =>
        Assert.Equal(expected, PeopleRules.PostRank(post));

    [Fact]
    public void SnapshotKeepsPersonAndCarSelectionsAsValueCopies()
    {
        var people = new[] { new AccessPerson(1, "Тест Первый", "монтажник", "01.01.2000") };
        var cars = new[] { new AccessCar(2, "Марка", "А123ВС") };
        var snapshot = new AccessSnapshot(people, cars, "Интро", "", null, "", "", Guid.NewGuid());
        Assert.Equal(1, snapshot.People.Count);
        Assert.Equal(2L, snapshot.Cars[0].Id);
        Assert.Equal("Интро", snapshot.Intro);
    }

    [Fact]
    public void SafeFilenameRemovesWindowsForbiddenCharacters()
    {
        var name = PeopleRules.SafeFileName("приказ №03/03:01*");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("*", name);
    }
}