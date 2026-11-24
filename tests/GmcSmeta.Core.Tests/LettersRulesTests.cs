using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Offline parity tests. No connection to any SQL Server or user archive.</summary>
public sealed class LettersRulesTests
{
    [Theory]
    [InlineData("02.11.2026", 2026, 11, 2)]
    [InlineData("29.02.2024", 2024, 2, 29)]
    public void ParsesStrictDay(string input, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), LettersRules.ParseDate(input));
    }

    [Theory]
    [InlineData("31.02.2026")]
    [InlineData("2.11.2026")]
    [InlineData("2026-11-02")]
    public void RejectsInvalidDay(string value) =>
        Assert.Throws<ArgumentException>(() => LettersRules.ParseDate(value));

    [Fact]
    public void NextNumberCountsDraftsAndUsesDailyMax()
    {
        var date = new DateTime(2026, 11, 2);
        Assert.Equal("02.11-04", LettersRules.NextNumber(date,
            new[] { "02.11-01", "02.11-03", "02.11-02", "03.11-72", "manual" }));
    }

    [Fact]
    public void NextNumberStartsNewDayAndYear()
    {
        Assert.Equal("03.11-01",
            LettersRules.NextNumber(new DateTime(2026, 11, 3), new[] { "02.11-99" }));
        Assert.Equal("02.11-01",
            LettersRules.NextNumber(new DateTime(2027, 11, 2), Array.Empty<string>()));
    }

    [Fact]
    public void ManualNumberIsCaseInsensitive()
    {
        Assert.Equal("ABC-01", LettersRules.NormalizeNumber(" abc-01 "));
    }

    [Fact]
    public void CopyNeverInheritsNumberFilesSentStatusOrReleases()
    {
        var original = Sample();
        original.Number = "02.11-55";
        original.Sent = new LetterSending { Date = "02.11.2026", Channel = "ЭДО",
            Destination = "office", Release = "exports/abc/old" };
        original.Status = "Отправлено";
        original.Files.Add(new LetterAttachment { Sha256 = new string('a', 64),
            Path = "files/scan.pdf", Pages = 1 });
        original.Releases.Add("exports/abc/old");
        var copy = LettersRules.CopyDraft(original, new DateTime(2026, 11, 3));
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("", copy.Number);
        Assert.True(copy.NumberAuto);
        Assert.Equal("03.11.2026", copy.Date);
        Assert.Equal(original.Body, copy.Body);
        Assert.Equal("Черновик", copy.Status);
        Assert.Null(copy.Sent);
        Assert.Empty(copy.Files);
        Assert.Empty(copy.Releases);
    }

    [Fact]
    public void ChangingHeaderOrAttachmentOrderChangesFingerprint()
    {
        var d = Sample();
        d.Files.Add(new LetterAttachment { Sha256 = new string('a',64), Pages = 1, Path = "files/1.pdf" });
        d.Files.Add(new LetterAttachment { Sha256 = new string('b',64), Pages = 1, Path = "files/2.pdf" });
        var before = LettersRules.Fingerprint(d);
        d.Files.Reverse();
        Assert.NotEqual(before, LettersRules.Fingerprint(d));
        before = LettersRules.Fingerprint(d);
        d.Subject = "Новая тема";
        Assert.NotEqual(before, LettersRules.Fingerprint(d));
    }

    [Fact]
    public void ReceiptDoesNotChangeReleasedContentFingerprint()
    {
        var d = Sample();
        var before = LettersRules.Fingerprint(d);
        d.Files.Add(new LetterAttachment
        {
            Kind = "Подтверждение отправки", Path = "files/r.pdf",
            Pages = 1, Sha256 = new string('a', 64)
        });
        Assert.Equal(before, LettersRules.Fingerprint(d));
    }

    [Theory]
    [InlineData("files/../escape.pdf")]
    [InlineData("files/..\\escape.pdf")]
    [InlineData("C:/Windows/secret")]
    [InlineData("/files/secret.pdf")]
    [InlineData("exports/letter/file.pdf")]
    [InlineData("files/./file.pdf")]
    public void RejectsPathTraversal(string value) =>
        Assert.Throws<InvalidDataException>(() => LettersRules.SafeArchiveRelative(value, "files"));

    [Fact]
    public void SafeFilenameStripsSpecialCharacters()
    {
        Assert.Equal("ab_c_d", LettersRules.SafeName("ab:c/d"));
        Assert.Equal("Приложение", LettersRules.SafeName("..."));
    }

    [Fact]
    public void SentStatusMustMatchSendingMetadata()
    {
        var d = Sample();
        d.Status = "Отправлено";
        Assert.Throws<ArgumentException>(() => LettersRules.Validate(d));
    }

    [Fact]
    public void RejectsDuplicateAttachments()
    {
        var d = Sample();
        d.Files.Add(new LetterAttachment { Sha256 = new string('a', 64),
            Pages = 1, Path = "files/1.pdf" });
        d.Files.Add(new LetterAttachment { Sha256 = new string('a', 64),
            Pages = 1, Path = "files/2.pdf" });
        Assert.Throws<ArgumentException>(() => LettersRules.Validate(d));
    }

    [Fact]
    public void SentMetadataDoesNotInvalidatePublishedContent()
    {
        var d = Sample();
        var original = LettersRules.Fingerprint(d);
        d.Sent = new LetterSending { Date = "02.11.2026", Channel = "ЭДО",
            Destination = "office", Release = "exports/123/abc" };
        d.Status = "Отправлено";
        Assert.Equal(original, LettersRules.Fingerprint(d));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmeta;Integrated Security=true")]
    public void SchemaCreationCannotTouchNonPilot(string connectionString) =>
        Assert.Throws<InvalidOperationException>(() => new SqlServerLetters(connectionString));

    private static LetterDocument Sample() => new()
    {
        Date = "02.11.2026", Number = "02.11-01",
        Subject = "Согласование", Body = "<p>Просим согласовать</p>",
        Sender = "Компания", Recipient = "Заказчик", Signer = "Директор"
    };
}
