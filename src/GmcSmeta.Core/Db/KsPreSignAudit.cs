using System.Globalization;
using System.Text;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only checks before marking KS-2 signed. These are warnings, not automatic
/// permission to change signed quantities or prices.
/// </summary>
public static class KsPreSignAudit
{
    private const decimal Tolerance = 0.000001m;

    public enum IssueType
    {
        OverKp,
        MissingPrice,
        OutsideEstimate,
        MoreThanIssued,
        EmptyAct
    }

    public sealed record Issue(IssueType Kind, long PositionId, string Number,
        string Name, string Unit, string Details);

    public sealed record Report(int PositionsInAct, IReadOnlyList<Issue> Issues)
    {
        public bool HasWarnings => Issues.Count != 0;
        public int Count(IssueType kind) => Issues.Count(i => i.Kind == kind);

        public string Format(int examplesPerType = 5)
        {
            var b = new StringBuilder();
            b.AppendLine("ПРОВЕРКА КС-2 ПЕРЕД ПОДПИСАНИЕМ");
            b.AppendLine($"Позиций с объёмом: {PositionsInAct}");
            if (Issues.Count == 0)
            {
                b.AppendLine("Замечаний по объёмам, ценам и выдаче не обнаружено.");
                b.AppendLine("Проверка не заменяет сверку с договором и первичными документами.");
                return b.ToString().TrimEnd();
            }

            foreach (var kind in Enum.GetValues<IssueType>())
            {
                var group = Issues.Where(i => i.Kind == kind).ToArray();
                if (group.Length == 0) continue;
                b.AppendLine();
                b.AppendLine($"{Title(kind)}: {group.Length}");
                foreach (var issue in group.Take(Math.Max(0, examplesPerType)))
                    b.AppendLine("• " + (issue.Number.Length > 0 ? "№" + issue.Number + " " : "") +
                        Shorten(issue.Name, 56) + " — " + issue.Details);
                if (group.Length > examplesPerType)
                    b.AppendLine($"…и ещё {group.Length - Math.Max(0, examplesPerType)} строк");
            }
            b.AppendLine();
            b.AppendLine("Это предупреждения: исправьте данные или подтвердите осознанное подписание.");
            return b.ToString().TrimEnd();
        }

        private static string Title(IssueType type) => type switch
        {
            IssueType.OverKp => "Превышен объём по КП",
            IssueType.MissingPrice => "Нет цены в строке акта",
            IssueType.OutsideEstimate => "Позиции вне КП",
            IssueType.MoreThanIssued => "Сдано больше, чем выдано на объект",
            IssueType.EmptyAct => "Нет строк с положительным объёмом",
            _ => "Замечание"
        };

        private static string Shorten(string? value, int limit) =>
            (value ?? "").Length <= limit ? value ?? "" : value![..(limit - 1)] + "…";
    }

    public static Report Check(IReadOnlyList<SqlServerActs.ActSheetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var issues = new List<Issue>();
        var count = 0;
        foreach (var row in rows)
        {
            if (row.LineId is null || row.LineQty <= Tolerance) continue;
            count++;
            var closed = row.ClosedByOthers + row.LineQty;
            void Warn(IssueType kind, string details) =>
                issues.Add(new Issue(kind, row.PositionId, row.NumText ?? "",
                    row.Name ?? "", row.Unit ?? "", details));

            if (row.OutOfKp)
                Warn(IssueType.OutsideEstimate,
                    $"в этом акте {Num(row.LineQty)} {row.Unit} (не входит в КП)");
            else if (closed > row.QtyByKp + Tolerance)
                Warn(IssueType.OverKp,
                    $"по КП {Num(row.QtyByKp)}, сдано всего {Num(closed)} {row.Unit}");

            if (row.LinePrice <= 0m)
                Warn(IssueType.MissingPrice, $"объём {Num(row.LineQty)} {row.Unit}, цена 0");

            if (row.TrackIssues && closed > row.Issued + Tolerance)
                Warn(IssueType.MoreThanIssued,
                    $"сдано всего {Num(closed)}, выдано {Num(row.Issued)} {row.Unit}");
        }
        if (count == 0)
            issues.Add(new Issue(IssueType.EmptyAct, 0, "", "Акт без объёмов", "",
                "нет позиций с положительным объёмом"));
        return new Report(count, issues);
    }

    private static string Num(decimal number) =>
        number.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"));
}
