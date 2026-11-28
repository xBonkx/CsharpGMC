using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmcSmeta.Core.Domain;

/// <summary>SQL Server letter snapshot. File contents live in a separately configured archive.</summary>
public sealed class LetterDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public string Date { get; set; } = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public bool NumberAuto { get; set; } = true;
    public string Subject { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Greeting { get; set; } = "";
    public string Body { get; set; } = "";
    public string Signer { get; set; } = "";
    public string Object { get; set; } = "";
    public long? ObjectId { get; set; }
    public long? ContractId { get; set; }
    public string ContractLabel { get; set; } = "";
    public bool UseScan { get; set; }
    public string Status { get; set; } = "Черновик";
    public LetterSending? Sent { get; set; }
    public List<LetterAttachment> Files { get; set; } = new();
    public List<string> Releases { get; set; } = new();
    [JsonIgnore] public long Revision { get; set; }
}

public sealed class LetterAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "Приложение";
    public string Path { get; set; } = "";
    public int Pages { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Included { get; set; } = true;
}

public sealed class LetterSending
{
    public string Date { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Release { get; set; } = "";
}

public sealed record LetterRelease(string Path, string Fingerprint, bool Combined, string ManifestSha256);

/// <summary>Pure, offline-testable parity rules used by SQL and the WPF editor.</summary>
/// <summary>Same document filter as Python document_context.belongs, scoped to one SQL database.</summary>
public static class LetterContext
{
    public static bool Belongs(LetterDocument letter, long? objectId, long? contractId,
        IReadOnlySet<long>? objectContracts = null)
    {
        if (contractId is long contract) return letter.ContractId == contract;
        if (objectId is not long obj) return true;
        return letter.ObjectId is long existing
            ? existing == obj : letter.ContractId is long legacy && objectContracts?.Contains(legacy) == true;
    }

    // Snapshot only: do not infer a legal identity from equal numbers or update
    // existing saved letters after a change to the agreement. An empty customer
    // is deliberately left empty rather than falling back to another agreement.
    internal static void ApplyAgreementSnapshot(LetterDocument letter, long agreementId,
        string number, string date, string customerFull, string customerOrganization,
        string documentKind, string documentName, string annexNumber, string documentNumber,
        bool useAgreementRecipient)
    {
        if (agreementId <= 0 || letter.ObjectId is not > 0 || letter.ContractId is not > 0 ||
            letter.Status != "Черновик" || letter.Sent is not null || letter.Releases.Count != 0 ||
            documentKind is not ("main" or "dop"))
            throw new InvalidOperationException("Нельзя подставить реквизиты юридического договора.");
        var legal = string.IsNullOrWhiteSpace(number) ? $"Договор (ID {agreementId}, номер не заполнен)" :
            "Договор № " + number.Trim();
        if (!string.IsNullOrWhiteSpace(date)) legal += " от " + date.Trim();
        var ownNumber = documentKind == "main" ? annexNumber : documentNumber;
        var own = documentKind == "main" ? "Приложение" : "ДС";
        if (!string.IsNullOrWhiteSpace(ownNumber)) own += " № " + ownNumber.Trim();
        if (!string.IsNullOrWhiteSpace(documentName)) own += " · " + documentName.Trim();
        else if (string.IsNullOrWhiteSpace(ownNumber)) own += $" (ID {letter.ContractId})";
        letter.ContractLabel = legal + " · " + own;
        if (useAgreementRecipient)
            letter.Recipient = string.IsNullOrWhiteSpace(customerOrganization)
                ? customerFull.Trim() : customerOrganization.Trim();
    }

    // Do not change the fingerprint of sent letters or letters with published releases.
    public static bool CanAttach(LetterDocument letter) =>
        letter.ObjectId is null && letter.ContractId is null && letter.Sent is null &&
        letter.Status == "Черновик" && letter.Releases.Count == 0;

    public static void AttachDraft(LetterDocument letter, long objectId, string objectName,
        long? contractId, string contractLabel)
    {
        if (!CanAttach(letter) || objectId <= 0 || contractId is <= 0)
            throw new InvalidOperationException("Привязать можно только черновик без объекта, договора и опубликованных выпусков.");
        letter.ObjectId = objectId;
        letter.ContractId = contractId;
        letter.ContractLabel = contractId is null ? "" : contractLabel;
        if (string.IsNullOrWhiteSpace(letter.Object)) letter.Object = objectName;
    }
}

public static class LettersRules
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static DateTime ParseDate(string date)
    {
        if (!DateTime.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var result))
            throw new ArgumentException("Дата должна быть в формате ДД.ММ.ГГГГ.");
        return result;
    }

    public static string NextNumber(DateTime day, IEnumerable<string> existing)
    {
        var prefix = day.ToString("dd.MM-", CultureInfo.InvariantCulture);
        var max = 0;
        foreach (var number in existing)
        {
            if (number is null || !number.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var tail = number[prefix.Length..];
            if (tail.Length > 0 && tail.All(char.IsAsciiDigit) &&
                int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                max = Math.Max(max, value);
        }
        return prefix + (max + 1).ToString("D2", CultureInfo.InvariantCulture);
    }

    public static string NormalizeNumber(string number) => (number ?? "").Trim().ToUpperInvariant();

    public static void Validate(LetterDocument letter)
    {
        if (letter is null) throw new ArgumentNullException(nameof(letter));
        ParseDate(letter.Date);
        if (letter.Status is not ("Черновик" or "Отправлено")) throw new ArgumentException("Недопустимый статус.");
        if (letter.Status == "Отправлено" != (letter.Sent is not null))
            throw new ArgumentException("Статус и сведения об отправке не совпадают.");
        if (letter.Number.Length > 128 || letter.Subject.Length > 400 ||
            letter.Recipient.Length > 4000 || letter.Object.Length > 400 ||
            letter.Sender.Length > 4000 || letter.Greeting.Length > 1000 ||
            letter.Signer.Length > 1000 || letter.Body.Length > 1_000_000)
            throw new ArgumentException("Превышена допустимая длина реквизитов письма.");
        if (letter.Files.Count > 1000) throw new ArgumentException("Слишком много приложений.");
        if (letter.Files.Select(f => f.Id).Distinct().Count() != letter.Files.Count ||
            letter.Files.Select(f => f.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != letter.Files.Count)
            throw new ArgumentException("Повторяющийся файл.");
        foreach (var file in letter.Files)
        {
            if (file.Kind is not ("Приложение" or "Скан письма" or "Подтверждение отправки")
                || file.Pages < 1 || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Недопустимые сведения о вложении.");
            SafeArchiveRelative(file.Path, "files");
        }
        foreach (var release in letter.Releases) SafeArchiveRelative(release, "exports");
    }

    public static string SafeArchiveRelative(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith('/') || path.Contains('\0'))
            throw new InvalidDataException("Недопустимый путь архива.");
        var parts = path.Split('/');
        if (parts.Length < 2 || parts[0] != folder ||
            parts.Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("Недопустимый путь архива.");
        return string.Join('/', parts);
    }

    public static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        var cleaned = new string((title ?? "").Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray())
            .Trim(' ', '.');
        if (cleaned.Length > 90) cleaned = cleaned[..90];
        return string.IsNullOrWhiteSpace(cleaned) ? "Приложение" : cleaned;
    }

    public static LetterDocument CopyDraft(LetterDocument from, DateTime now)
    {
        return new LetterDocument
        {
            Date = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            Subject = from.Subject, Recipient = from.Recipient, Sender = from.Sender,
            Greeting = from.Greeting, Body = from.Body, Signer = from.Signer,
            Object = from.Object, ObjectId = from.ObjectId, ContractId = from.ContractId,
            ContractLabel = from.ContractLabel
        };
    }

    /// <summary>Excludes releases and sending metadata; detects any change to issued content.</summary>
    public static string Fingerprint(LetterDocument d)
    {
        var content = new
        {
            d.Number, d.Date, d.Subject, d.Recipient, d.Sender, d.Greeting,
            d.Body, d.Signer, d.Object, d.ObjectId, d.ContractId, d.UseScan,
            Files = d.Files.Where(f => f.Kind != "Подтверждение отправки").Select(f =>
                new { f.Id, f.Title, f.Kind, f.Path, f.Pages, f.Sha256, f.Included }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, Json))))
            .ToLowerInvariant();
    }

    public static string Digest(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
