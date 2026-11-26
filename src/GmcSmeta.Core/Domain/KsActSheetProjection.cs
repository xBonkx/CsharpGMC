using System.Globalization;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Pure numbering projection for the SQL-loaded act sheet. Preserve
/// source row order, system order, manual numbering and extra-line anchors.</summary>
public static class KsActSheetProjection
{
    public static (IReadOnlyList<SqlServerActs.ActSheetRow> Rows,
        IReadOnlyDictionary<long, string> RefNumbers, IReadOnlyDictionary<long, string> RefDisplay)
        NumberRows(IReadOnlyList<(long Id, string Name, long Parent)> systemRows,
            IReadOnlyList<SqlServerActs.ActSheetRow> positions, bool numManual)
    {
        // «№» позиции сметы (kp_numbers): «Без системы» не считается, вне КП пропускается
        var positionsBySystem = positions.ToLookup(p => p.SystemId);
        var kpNumbers = new Dictionary<long, int>();
        var num = 1;
        foreach (var sy in systemRows)
        {
            if (sy.Name != "Без системы")
                num += 1;
            foreach (var p in positionsBySystem[sy.Id])
            {
                if (p.OutOfKp)
                    continue;
                kpNumbers[p.PositionId] = num;
                num += 1;
            }
        }

        // номера строк вне сметы этого акта (extra_numbers): «якорь.счётчик»
        var extraNumbers = new Dictionary<long, string>();
        var anchorNumber = "";
        var suffix = 0;
        foreach (var sy in systemRows)
        {
            foreach (var p in positionsBySystem[sy.Id])
            {
                if (!p.OutOfKp)
                {
                    anchorNumber = (numManual ? p.NumText.Trim() : "") is { Length: > 0 } m ? m
                        : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture);
                    suffix = 0;
                }
                else if (p.LineId is not null)
                {
                    suffix += 1;
                    extraNumbers[p.PositionId] = $"{anchorNumber}.{suffix}";
                }
            }
        }

        // Ссылки формул «КN»: только однозначные, целиком числовые номера
        var refNumbers = new Dictionary<long, string>();
        foreach (var p in positions)
        {
            if (p.OutOfKp)
                continue;
            var manual = numManual ? p.NumText.Trim() : "";
            refNumbers[p.PositionId] = (manual.Length > 0 ? manual
                    : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture))
                .Replace(',', '.').Trim();
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in refNumbers.Values)
            counts[v] = counts.GetValueOrDefault(v) + 1;
        var refDisplay = new Dictionary<long, string>();
        foreach (var (pid, v) in refNumbers)
            if (counts[v] == 1 && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+)*$"))
                refDisplay[pid] = v;

        var rows = new List<ActSheetRow>(positions.Count);
        foreach (var p in positions)
        {
            var numText = p.OutOfKp
                ? extraNumbers.GetValueOrDefault(p.PositionId, "")
                : (numManual ? p.NumText : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture));
            rows.Add(string.Equals(p.NumText, numText, StringComparison.Ordinal)
                ? p : p with { NumText = numText });
        }
        return (rows, refNumbers, refDisplay);
    }
}
