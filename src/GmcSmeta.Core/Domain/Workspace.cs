using System.Data.Common;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Объект (стройка) для списка.</summary>
public sealed record ObjectRow(int Id, string Name, string Customer, string Address, bool Archived, string ArchivedOn)
{
    public string DisplayName => Archived ? Name + "   (архив)" : Name;
    public string CreatedAt { get; init; } = "";
    public string Folder { get; init; } = "";
}

/// <summary>Документ объекта для списка — с итогами.</summary>
public sealed record DocRow(
    int Id,
    int ObjectId,
    string Kind,
    string KindTitle,
    string Title,
    string Name,
    string Number,
    string Date,
    bool Locked,
    decimal Materials,
    decimal Smr,
    decimal Total,
    int Positions)
{
    public int? StageOf { get; init; }
    public int StageNo { get; init; }
    public bool StageRest { get; init; }
    public string KpPhase { get; init; } = "";
    public bool IsStage => StageOf is not null;
    public int? BaseContractId { get; init; }
    public long? CompanyId { get; init; }
    public int Order { get; init; }
    public string NavigationName => Navigation.DocumentName(this);
    public string NavigationDate => Navigation.ShortDate(Date, DateTime.Today.Year);
    public string PhaseNumber => Kind == "kp" && !IsStage ? Navigation.Phase(KpPhase).Number : "";
    public string PhaseColor => Navigation.Phase(KpPhase).Color;
    public string NavigationTip => Title + (Kind == "kp" && !IsStage
        ? "\nПодготовка КП: " + Navigation.Phase(KpPhase).Title : "") + $"\nИТОГО: {Total:N2} ₽";
}

public sealed record ObjectDocumentCard(DocRow Document, EstimateCardData Estimate, string BaseTitle, string TagName)
{
    public ObjectDocumentCard(DocRow document, EstimateDocument estimate, string baseTitle, string tagName)
        : this(document, EstimateCardData.FromEstimate(estimate), baseTitle, tagName) { }

    public int Id => Document.Id;
    public string KindTitle => Document.KindTitle;
    public string Title => Document.Title;
    public string DisplayKind => Document.Kind == "main" ? "Приложение" : KindTitle;
    public string DisplayTitle => ShortAgreementTitle ? AgreementPresentation.AnnexTitle(Document) :
        Document.Kind == "main" ? AgreementPresentation.FullAnnexTitle(Document) : Title;
    public bool ShortAgreementTitle { get; init; }
    public string BaseCaption => string.IsNullOrWhiteSpace(BaseTitle) ? "" : BasePrefix + BaseTitle;
    public string BasePrefix { get; init; } = "к документу: ";
    public string Meta => $"{Estimate.Systems} сист. · {Estimate.Positions} поз." +
        (Estimate.Company.Length > 0 ? " · " + Estimate.Company : "");
    public decimal Total => Estimate.Totals.Total;
    public string SplitText => Estimate.VatTop
        ? $"без НДС {Money.Format(Estimate.Totals.Net)} · НДС {Estimate.VatRate:0.##}% {Money.Format(Estimate.Totals.Vat)}"
        : $"материалы {Money.Format(Estimate.Statistics.Materials)} · СМР {Money.Format(Estimate.Statistics.Smr)}";
    public bool IsProposal => Document.Kind == "kp";
    public ContractFinance Finance { get; init; } = new();
    public ContractProgress Progress => new(Total, Finance.Signed, Finance.Pending);
    public bool ShowProgress => !IsProposal && Total != 0;
}

public sealed record ObjectDocuments(IReadOnlyList<ObjectDocumentCard> Cards,
    decimal Total, decimal Materials, decimal Smr, decimal VatTop)
{
    public ContractFinance Finance { get; init; } = new();
    public string? AgreementHeading { get; init; }
    public bool ShowProgress { get; init; }
    public ContractProgress Progress => new(Total, Finance.Signed, Finance.Pending);
    public string EarnedText => Finance.Earned(Smr) is decimal value ? Money.Format(value) : "нет данных";
    public string EarnedCaption => "Заработано" + (Finance.Notes(Smr).Length > 0 ? "  ⚠" : "");
    public string EarnedNotes => Finance.Notes(Smr);
}

/// <summary>Чтение того, что нужно для первого экрана: объекты и их документы.</summary>
public static class Workspace
{
    private static readonly Dictionary<string, string> KindTitles = new(StringComparer.Ordinal)
    {
        ["kp"] = "КП",
        ["main"] = "Договор",
        ["dop"] = "Допсоглашение",
    };

    public static List<ObjectRow> Objects(DbConnection conn) => ReadObjects(conn, null);

    private static string ObjectQuery(int? objectId) =>
        "SELECT id, name, customer, address, archived, archived_on, created_at, folder " +
        "FROM objects " + (objectId.HasValue ? "WHERE id=@object " : "") + "ORDER BY archived, name";

    private static List<ObjectRow> ReadObjects(DbConnection conn, int? objectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ObjectQuery(objectId);
        if (objectId is int id) AddObjectParameter(cmd, id);
        using var r = cmd.ExecuteReader();
        return ReadObjectRows(r);
    }

    private static List<ObjectRow> ReadObjectRows(DbDataReader r)
    {
        var list = new List<ObjectRow>();
        while (r.Read())
        {
            list.Add(new ObjectRow(
                SqlInteger.ReadInt32(r, 0),
                Str(r, 1),
                Str(r, 2),
                Str(r, 3),
                !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4)),
                Str(r, 5)) { CreatedAt = Str(r, 6), Folder = Str(r, 7) });
        }
        return list;
    }

    public static List<DocRow> Documents(DbConnection conn) => ReadDocuments(conn, null, true);

    public static List<DocRow> ReadObjectDocumentMetadata(DbConnection conn, int objectId) =>
        ReadDocuments(conn, objectId, false);

    private static string DocumentQuery(int? objectId) =>
        "SELECT id, object_id, kind, number, [date], name, locked, stage_of, stage_no, stage_rest, kp_phase, base_contract_id, \"order\", company_id FROM contracts " +
        (objectId.HasValue ? "WHERE object_id=@object " : "") + "ORDER BY object_id, \"order\", id";

    private static List<DocRow> ReadDocuments(DbConnection conn, int? objectId, bool includeTotals)
    {
        var totals = includeTotals ? Totals.ReadAll(conn).ToDictionary(t => t.Id) : null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = DocumentQuery(objectId);
        if (objectId is int scopeId) AddObjectParameter(cmd, scopeId);
        using var reader = cmd.ExecuteReader();
        return ReadDocumentRows(reader, totals);
    }

    private static List<DocRow> ReadDocumentRows(DbDataReader reader, IReadOnlyDictionary<int, ContractTotals>? totals)
    {
        var list = new List<DocRow>();
        while (reader.Read())
        {
            var id = SqlInteger.ReadInt32(reader, 0);
            var kind = Str(reader, 2);
            var kindTitle = KindTitles.GetValueOrDefault(kind, "КП");
            var t = totals?.GetValueOrDefault(id);
            list.Add(new DocRow(
                id,
                SqlInteger.ReadInt32(reader, 1),
                kind,
                kindTitle,
                Title(kindTitle, Str(reader, 3), Str(reader, 4), Str(reader, 5)),
                Str(reader, 5),
                Str(reader, 3),
                Str(reader, 4),
                !reader.IsDBNull(6) && Convert.ToBoolean(reader.GetValue(6)),
                t?.Materials ?? 0m,
                t?.Smr ?? 0m,
                t?.Total ?? 0m,
                t?.Positions ?? 0)
            {
                StageOf = reader.IsDBNull(7) ? null : SqlInteger.ReadInt32(reader, 7),
                StageNo = reader.IsDBNull(8) ? 0 : SqlInteger.ReadInt32(reader, 8),
                StageRest = !reader.IsDBNull(9) && Convert.ToBoolean(reader.GetValue(9)),
                KpPhase = Str(reader, 10),
                BaseContractId = reader.IsDBNull(11) ? null : SqlInteger.ReadInt32(reader, 11),
                Order = reader.IsDBNull(12) ? 0 : SqlInteger.ReadInt32(reader, 12),
                CompanyId = reader.IsDBNull(13) ? null : SqlInteger.ReadInt64(reader, 13)
            });
        }
        return list;
    }

    /// <summary>One metadata batch plus the existing financial batch; no MARS required.</summary>
    public static (List<ObjectRow> Objects, List<DocRow> Documents) ReadNavigation(
        Microsoft.Data.SqlClient.SqlConnection conn)
    {
        var totals = Totals.ReadAll(conn).ToDictionary(t => t.Id);
        return ReadNavigationMetadata(conn, null, totals);
    }

    // Manual refresh uses an independent read connection. Do not mix list
    // metadata and monetary totals from two different server revisions.
    public static (List<ObjectRow> Objects, List<DocRow> Documents) ReadConsistentNavigation(
        Microsoft.Data.SqlClient.SqlConnection conn)
    {
        var before = Db.SqlServerDatabase.Revision(conn);
        if (string.IsNullOrWhiteSpace(before))
            throw new InvalidOperationException("Не удалось прочитать ревизию SQL Server.");
        var data = ReadNavigation(conn);
        if (!string.Equals(before, Db.SqlServerDatabase.Revision(conn), StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменились во время обновления списка.");
        return data;
    }

    private static (List<ObjectRow> Objects, List<DocRow> Documents) ReadNavigationMetadata(
        Microsoft.Data.SqlClient.SqlConnection conn, int? objectId,
        IReadOnlyDictionary<int, ContractTotals>? totals)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ObjectQuery(objectId) + ";" + DocumentQuery(objectId);
        if (objectId is int id) AddObjectParameter(cmd, id);
        using var reader = cmd.ExecuteReader();
        var objects = ReadObjectRows(reader);
        if (!reader.NextResult())
            throw new InvalidOperationException("Неполный пакет навигации SQL Server.");
        return (objects, ReadDocumentRows(reader, totals));
    }

    private static void AddObjectParameter(DbCommand command, int id)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@object";
        parameter.DbType = System.Data.DbType.Int32;
        parameter.Value = id;
        command.Parameters.Add(parameter);
    }

    // Compare navigation metadata without re-reading totals for the whole workspace.
    // Money and row counts are read afresh by the card projection.
    public static bool ObjectCardMetadataMatches(DbConnection conn, ObjectRow obj,
        IReadOnlyList<DocRow> navigationDocuments)
    {
        // The surrounding caller still checks the revision before and after the read.
        var metadata = conn is Microsoft.Data.SqlClient.SqlConnection sql
            ? ReadNavigationMetadata(sql, obj.Id, null)
            : (Objects: ReadObjects(conn, obj.Id), Documents: ReadDocuments(conn, obj.Id, false));
        if (metadata.Objects.SingleOrDefault() != obj) return false;
        static DocRow Metadata(DocRow d) => d with { Materials = 0m, Smr = 0m, Total = 0m, Positions = 0 };
        var expected = navigationDocuments.Where(d => d.ObjectId == obj.Id)
            .OrderBy(d => d.Id).Select(Metadata);
        var actual = metadata.Documents.OrderBy(d => d.Id).Select(Metadata);
        return expected.SequenceEqual(actual);
    }

    public static ObjectDocuments ReadObjectDocuments(DbConnection conn, ObjectRow obj,
        IReadOnlyList<DocRow> allDocuments, NavigationMode mode)
    {
        var all = allDocuments.Where(d => d.ObjectId == obj.Id).ToList();
        var docs = all.Where(d => mode == NavigationMode.Archive ||
            (mode == NavigationMode.Kp ? d.Kind == "kp" : d.Kind is "main" or "dop")).ToList();
        var counted = CountedDocumentIds(all, mode);
        IReadOnlyDictionary<int, ContractFinance> finances;
        IReadOnlyDictionary<int, EstimateCardData>? estimates = null;
        Dictionary<string, string> tags;
        if (conn is Microsoft.Data.SqlClient.SqlConnection sql)
        {
            var rules = TagRules.LoadWithNames(sql, out tags);
            estimates = EstimateReader.LoadCardsSql(sql, docs.Select(doc => doc.Id), rules);
            finances = FinanceReader.ReadForCards(sql, estimates.Values.Select(e => e.Totals).ToArray(), rules);
        }
        else
        {
            // Preserve the existing offline fixture route.
            finances = FinanceReader.ReadAll(conn);
            tags = new Dictionary<string, string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT [key], name FROM tags";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tags[Str(reader, 0)] = Str(reader, 1);
        }
        var docsById = docs.ToDictionary(d => d.Id);
        var cards = new List<ObjectDocumentCard>(docs.Count);
        foreach (var doc in OrderDocumentCards(docs))
        {
            var estimate = estimates is null
                ? EstimateCardData.FromEstimate(EstimateReader.Load(conn, doc.Id))
                : estimates[doc.Id];
            var baseDoc = doc.BaseContractId is int baseId ? docsById.GetValueOrDefault(baseId) : null;
            var baseTitle = baseDoc is null ? "" : baseDoc.Title.Split(" — ", 2)[^1];
            cards.Add(new ObjectDocumentCard(doc, estimate, doc.Kind == "dop" ? baseTitle : "",
                tags.GetValueOrDefault(estimate.Form, estimate.Form)) { Finance = finances[doc.Id],
                    BasePrefix = baseDoc?.Kind == "main" ? "к приложению: " : "к документу: " });
        }
        var heading = mode == NavigationMode.Kp ? null : AgreementPresentation.Heading(
            cards.Select(c => (c.Document, c.Estimate.Company)));
        if (heading is not null)
            cards = cards.Select(c => c with { ShortAgreementTitle = c.Document.Kind == "main" }).ToList();
        var totals = cards.Where(c => counted.Contains(c.Id)).ToList();
        return new ObjectDocuments(cards, totals.Sum(c => c.Total), totals.Sum(c => c.Estimate.Statistics.Materials),
            totals.Sum(c => c.Estimate.Statistics.Smr), totals.Where(c => c.Estimate.VatTop).Sum(c => c.Estimate.Totals.Vat))
        {
            Finance = ContractFinance.Sum(totals.Select(c => c.Finance)),
            AgreementHeading = heading,
            ShowProgress = mode != NavigationMode.Kp && all.Any(d => d.Kind is "main" or "dop")
        };
    }

    public static HashSet<int> CountedDocumentIds(IReadOnlyList<DocRow> documents, NavigationMode mode)
    {
        if (mode == NavigationMode.Kp)
            return documents.Where(d => d.Kind == "kp" && d.StageOf is null).OrderBy(d => d.Order).ThenBy(d => d.Id)
                .TakeLast(1).Select(d => d.Id).ToHashSet();
        var contracts = documents.Where(d => d.Kind is "main" or "dop").ToList();
        return (contracts.Count > 0 ? contracts : documents.OrderBy(d => d.Order).ThenBy(d => d.Id).TakeLast(1))
            .Select(d => d.Id).ToHashSet();
    }

    public static IReadOnlyList<DocRow> OrderDocumentCards(IReadOnlyList<DocRow> documents)
    {
        var ids = documents.Select(d => d.Id).ToHashSet();
        bool Supplement(DocRow d) => d.Kind == "dop" && d.BaseContractId is int id && ids.Contains(id);
        var ordered = documents.Where(d => !Supplement(d)).ToList();
        foreach (var doc in documents.Where(Supplement))
        {
            var index = ordered.FindIndex(d => d.Id == doc.BaseContractId) + 1;
            while (index < ordered.Count && ordered[index].Kind == "dop" && ordered[index].BaseContractId == doc.BaseContractId) index++;
            ordered.Insert(index, doc);
        }
        return ordered;
    }

    /// <summary>Как Contract.title в Python: «КП № 12 от 01.02.2026 — название».</summary>
    private static string Title(string kindTitle, string number, string date, string name)
    {
        var t = kindTitle;
        if (number.Length > 0) t += $" № {number}";
        if (date.Length > 0) t += $" от {date}";
        if (name.Length > 0) t += $" — {name}";
        return t;
    }

    private static string Str(DbDataReader r, int i) => r.IsDBNull(i) ? "" :
        r.GetValue(i) is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : Convert.ToString(r.GetValue(i)) ?? "";
}
