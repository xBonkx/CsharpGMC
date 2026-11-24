using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class LetterContextTests
{
    [Fact]
    public void ContractScopeExcludesOtherContractsAndUnboundObjectLetters()
    {
        Assert.True(LetterContext.Belongs(new LetterDocument { ObjectId = 10, ContractId = 20 }, 10, 20));
        Assert.False(LetterContext.Belongs(new LetterDocument { ObjectId = 10, ContractId = 21 }, 10, 20));
        Assert.False(LetterContext.Belongs(new LetterDocument { ObjectId = 10 }, 10, 20));
    }

    [Fact]
    public void ObjectScopeIncludesLegacyContractOnlyLetter()
    {
        var contracts = new HashSet<long> { 20, 21 };
        Assert.True(LetterContext.Belongs(new LetterDocument { ContractId = 20 }, 10, null, contracts));
        Assert.False(LetterContext.Belongs(new LetterDocument { ObjectId = 11, ContractId = 20 }, 10, null, contracts));
        Assert.False(LetterContext.Belongs(new LetterDocument(), 10, null, contracts));
    }

    [Fact]
    public void AttachingDraftPreservesTextAndAssignsExactIds()
    {
        var letter = new LetterDocument { Subject = "Тема", Object = "Исходный текст", Body = "Текст" };
        LetterContext.AttachDraft(letter, 10, "Объект", 20, "Договор № 20");
        Assert.Equal(10L, letter.ObjectId);
        Assert.Equal(20L, letter.ContractId);
        Assert.Equal("Договор № 20", letter.ContractLabel);
        Assert.Equal("Исходный текст", letter.Object);
        Assert.Equal("Текст", letter.Body);
        Assert.False(LetterContext.CanAttach(letter));
        Assert.Throws<InvalidOperationException>(() => LetterContext.AttachDraft(letter, 11, "Другой", 21, "Другой"));
    }

    [Fact]
    public void SentAndReleasedLettersAreNotAttachable()
    {
        var sent = new LetterDocument { Status = "Отправлено", Sent = new LetterSending() };
        var released = new LetterDocument { Releases = new List<string> { "exports/old" } };
        Assert.False(LetterContext.CanAttach(sent));
        Assert.False(LetterContext.CanAttach(released));
        Assert.Throws<InvalidOperationException>(() => LetterContext.AttachDraft(released, 10, "Объект", null, ""));
    }
}
