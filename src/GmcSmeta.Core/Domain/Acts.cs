using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

public sealed record ActSummary(int Id, int ContractId, string Number, DateOnly? Date,
    DateOnly? PeriodFrom, DateOnly? PeriodTo, bool Signed, decimal Materials, decimal Net,
    decimal Total, decimal Vat, decimal Smr, decimal Transport, decimal Pnr, int GrossAdjustmentCents, string Note)
{
    public string DateText => Date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "Не задана";
    public string PeriodText => (PeriodFrom?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "…") +
        " — " + (PeriodTo?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "…");
    public string Status => Signed ? "Подписан" : "Черновик";
}

public sealed record ActContractChoice(string Number)
{
    public string Caption => Number.Length > 0 ? "Договор №" + Number : "Договор без номера";
}
public sealed record ActSourceChoice(int? Id, string Caption);
public sealed record ActListRow(bool IsGroup, int Id, int ContractId, string GroupKey, string Name,
    string Source, string DateText, string PeriodText, decimal Total, string Status)
{
    public bool Expanded { get; init; } = true;
    public string Expander => Expanded ? "▾" : "▸";
    public bool Signed => Status == "Подписан";
}
public sealed record ActListing(IReadOnlyList<ActListRow> Rows, int Count, decimal Total);
public sealed record ActPosition(int Id, string Name, string Unit, decimal Qty, decimal Price, decimal Amount);

/// <summary>Read-only ContractActsPage: contract grouping, KS-3 rows and visible-act filters.</summary>
public static class Acts
{
    public static string ContractNumber(DocRow doc, IReadOnlyList<DocRow> allDocuments)
    {
        if (doc.Kind == "dop")
        {
            var parent = allDocuments.FirstOrDefault(d => d.Id == doc.BaseContractId);
            if (parent is not null) return parent.Number.Trim();
            var mains = allDocuments.Where(d => d.ObjectId == doc.ObjectId && d.Kind == "main")
                .Select(d => d.Number.Trim()).Distinct().ToList();
            if (mains.Count == 1) return mains[0];
        }
        return doc.Number.Trim();
    }
    public static string SourceCaption(DocRow doc) => doc.Kind == "dop"
        ? "ДС №" + doc.Number + (doc.Name.Length > 0 ? " · " + doc.Name : "")
        : doc.Name.Length > 0 ? doc.Name : doc.Number.Length > 0 ? "Смета №" + doc.Number : "Смета без названия";

    public static string GroupKey(ActSummary act)
    {
        var number = act.Number.Trim();
        var hyphen = number.Split('-', 2);
        if (act.GrossAdjustmentCents != 0 && hyphen.Length == 2 &&
            hyphen.All(s => s.Length > 0 && s.All(char.IsDigit))) return hyphen[0];
        return number.Split('.', 2)[0].Trim();
    }
    public static int Suffix(string number)
    {
        var hyphen = number.IndexOf('-');
        if (hyphen >= 0) number = number[..hyphen] + "." + number[(hyphen + 1)..];
        var parts = number.Split('.', 2);
        return parts.Length == 2 && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 1;
    }
    public static IReadOnlyList<ActContractChoice> Contracts(int objectId, IReadOnlyList<DocRow> documents) =>
        documents.Where(d => d.ObjectId == objectId && (d.Kind is "main" or "dop"))
            .Select(d => ContractNumber(d, documents)).Distinct().Select(n => new ActContractChoice(n)).ToList();

    public static IReadOnlyList<ActSourceChoice> Sources(int objectId, string number, IReadOnlyList<DocRow> documents) =>
        new[] { new ActSourceChoice(null, "Все приложения и ДС") }.Concat(
            documents.Where(d => d.ObjectId == objectId && (d.Kind is "main" or "dop") && ContractNumber(d, documents) == number)
                .OrderBy(d => d.Order).ThenBy(d => d.Id).Select(d => new ActSourceChoice(d.Id, SourceCaption(d)))).ToList();

    public static ActListing List(int objectId, string number, IReadOnlyList<DocRow> documents,
        IReadOnlyList<ActSummary> acts, string search = "", int? sourceId = null, bool? signed = null)
    {
        var docs = documents.Where(d => d.ObjectId == objectId && (d.Kind is "main" or "dop") && ContractNumber(d, documents) == number)
            .ToDictionary(d => d.Id);
        var words = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var groups = acts.Where(a => docs.ContainsKey(a.ContractId)).GroupBy(GroupKey)
            .OrderByDescending(g => g.Min(a => (a.Date ?? DateOnly.MaxValue, a.Id)));
        var rows = new List<ActListRow>();
        var count = 0; decimal total = 0;
        foreach (var group in groups)
        {
            var all = group.OrderBy(a => Suffix(a.Number)).ThenBy(a => a.Id).ToList();
            var children = new List<ActListRow>();
            foreach (var act in all)
            {
                var row = new ActListRow(false, act.Id, act.ContractId, group.Key, "КС-2 №" + act.Number,
                    SourceCaption(docs[act.ContractId]), act.DateText, act.PeriodText, act.Total, act.Status);
                var text = $"КС-3 №{group.Key} {row.Name} {row.Source} {row.DateText} {row.PeriodText} {Money.Format(row.Total)} {row.Status}";
                if (sourceId is not null && sourceId != act.ContractId || signed is not null && signed != act.Signed ||
                    !words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                children.Add(row); count++; total += act.Total;
            }
            if (children.Count == 0) continue;
            rows.Add(new ActListRow(true, 0, 0, group.Key, "КС-3 №" + group.Key,
                children.Count == all.Count ? $"{all.Count} КС-2" : $"{children.Count} из {all.Count} КС-2",
                "", "", all.Sum(a => a.Total), all.All(a => a.Signed) ? "Подписана" : "На подготовке"));
            rows.AddRange(children);
        }
        return new ActListing(rows, count, total);
    }

    public static IReadOnlyList<ActPosition> Positions(SqliteConnection conn, int actId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT al.id,p.name,p.unit,al.qty,al.price,c.form FROM act_lines al " +
            "JOIN positions p ON p.id=al.position_id JOIN acts a ON a.id=al.act_id " +
            "JOIN contracts c ON c.id=a.contract_id WHERE a.id=$id ORDER BY al.id";
        cmd.Parameters.AddWithValue("$id", actId);
        var rules = TagRules.Load(conn);
        using var r = cmd.ExecuteReader();
        var result = new List<ActPosition>();
        while (r.Read())
        {
            var qty = r.IsDBNull(3) ? 0 : r.GetDouble(3);
            var price = r.IsDBNull(4) ? 0 : r.GetDouble(4);
            var amount = rules.Has(r.IsDBNull(5) ? "" : r.GetString(5), "round_rows")
                ? FinanceReader.RoundHalfUp(qty * price) : FinanceReader.PythonRound(qty * price);
            result.Add(new ActPosition(r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2), (decimal)qty, (decimal)price, (decimal)amount));
        }
        return result;
    }
}

