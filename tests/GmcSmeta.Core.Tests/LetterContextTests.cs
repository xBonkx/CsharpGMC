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

    [Fact]
    public void NewAnnexLetterUsesLegalNumberAndCustomerNotLegacyNumber()
    {
        var letter = new LetterDocument { ObjectId = 10, ContractId = 20, Recipient = "Другой заказчик", Object = "Стройка" };
        LetterContext.ApplyAgreementSnapshot(letter, 100, "А-77", "28.11.2026",
            "Полные реквизиты", "ООО Заказчик", "main", "Вентиляция", "3", "старый номер", true);
        Assert.Equal("Договор № А-77 от 28.11.2026 · Приложение № 3 · Вентиляция", letter.ContractLabel);
        Assert.Equal("ООО Заказчик", letter.Recipient);
        Assert.Equal(20L, letter.ContractId);
        Assert.Equal("Стройка", letter.Object);
    }

    [Fact]
    public void ExistingDraftAttachmentRetainsRecipientBodyAndOwnDsNumber()
    {
        var letter = new LetterDocument { ObjectId = 10, ContractId = 20, Recipient = "Введённый адресат", Body = "Содержание" };
        LetterContext.ApplyAgreementSnapshot(letter, 100, "А-77", "", "Новые реквизиты",
            "Другой заказчик", "dop", "", "не номер ДС", "4", false);
        Assert.Equal("Договор № А-77 · ДС № 4", letter.ContractLabel);
        Assert.Equal("Введённый адресат", letter.Recipient);
        Assert.Equal("Содержание", letter.Body);
    }

    [Fact]
    public void MissingLegalRequisitesDoNotFallBackToOtherCustomerOrLegacyNumber()
    {
        var letter = new LetterDocument { ObjectId = 10, ContractId = 20, Recipient = "Старый заказчик" };
        LetterContext.ApplyAgreementSnapshot(letter, 100, "", "", "", "", "main", "", "", "старый номер", true);
        Assert.Equal("Договор (ID 100, номер не заполнен) · Приложение (ID 20)", letter.ContractLabel);
        Assert.Empty(letter.Recipient);
    }

    [Fact]
    public void FullCustomerIsFallbackOnlyWithinSameLegalAgreement()
    {
        var letter = new LetterDocument { ObjectId = 10, ContractId = 20 };
        LetterContext.ApplyAgreementSnapshot(letter, 100, "А-77", "", "Полные реквизиты",
            " ", "main", "Смета", "", "старый номер", true);
        Assert.Equal("Полные реквизиты", letter.Recipient);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PublishedLetterCannotReceiveNewAgreementSnapshot(bool sent, bool released)
    {
        var letter = new LetterDocument { ObjectId = 10, ContractId = 20, ContractLabel = "История" };
        if (sent) { letter.Status = "Отправлено"; letter.Sent = new LetterSending(); }
        if (released) letter.Releases.Add("exports/old");
        Assert.Throws<InvalidOperationException>(() => LetterContext.ApplyAgreementSnapshot(letter,
            100, "А-77", "", "", "", "main", "", "", "", false));
        Assert.Equal("История", letter.ContractLabel);
    }
}
