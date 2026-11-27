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
            IReadOnlyList<SqlServerActs.ActSheetRow> positions, bool numManual, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // «№» позиции сметы (kp_numbers): «Без системы» не считается, вне КП пропускается
        var positionsBySystem = positions.ToLookup(p => p.SystemId);
        var kpNumbers = new Dictionary<long, int>();
        var num = 1;
        foreach (var sy in systemRows)
        {
            token.ThrowIfCancellationRequested();
            if (sy.Name != "Без системы")
                num += 1;
            foreach (var p in positionsBySystem[sy.Id])
            {
                token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
            foreach (var p in positionsBySystem[sy.Id])
            {
                token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
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

        var rows = new List<SqlServerActs.ActSheetRow>(positions.Count);
        foreach (var p in positions)
        {
            token.ThrowIfCancellationRequested();
            var numText = p.OutOfKp
                ? extraNumbers.GetValueOrDefault(p.PositionId, "")
                : (numManual ? p.NumText : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture));
            rows.Add(string.Equals(p.NumText, numText, StringComparison.Ordinal)
                ? p : p with { NumText = numText });
        }
        token.ThrowIfCancellationRequested();
        return (rows, refNumbers, refDisplay);
    }

    public sealed record SectionDisplay(long SystemId, string Label, bool IsSub,
        decimal Material, decimal Work, decimal Transport, bool Unallocated = false)
    {
        public decimal Amount => Material + Work + Transport;
        public bool InAct => Work != 0m || Transport != 0m;
        public string Name => Unallocated
            ? $"Без привязки к разделу · СМР {Money.Format(Work)} · тр. {Money.Format(Transport)}"
            : Label + (InAct ? $"   ·   СМР {Money.Format(Work)}   ·   тр. {Money.Format(Transport)}" : "");
    }

    /// <summary>Same section presentation for opening and quantity readback.
    /// Include manual allocations and negative material amounts, never alter SQL totals.</summary>
    public static IReadOnlyList<SectionDisplay> Sections(SqlServerActs.ActSheetData sheet,
        KsActAllocation.SectionAmounts? manual, bool perPosition, bool roundRows)
    {
        var material = new Dictionary<long, decimal>();
        foreach (var row in sheet.Positions)
        {
            material.TryAdd(row.SystemId, 0m);
            if (row.LineId is not null)
                material[row.SystemId] += decimal.Round(row.LineQty * row.LinePrice, 2,
                    roundRows ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven);
        }
        var result = new List<SectionDisplay>(sheet.Systems.Count);
        foreach (var system in sheet.Systems)
        {
            var work = perPosition ? 0m : manual?.Smr.GetValueOrDefault(system.SystemId) ?? 0m;
            var transport = manual?.Transport.GetValueOrDefault(system.SystemId) ?? 0m;
            if (!material.ContainsKey(system.SystemId) && work == 0m && transport == 0m) continue;
            result.Add(new SectionDisplay(system.SystemId, system.Label, system.IsSub,
                material.GetValueOrDefault(system.SystemId), work, transport));
        }
        if (manual is not null)
        {
            var work = perPosition ? 0m : manual.Smr.GetValueOrDefault(0);
            var transport = manual.Transport.GetValueOrDefault(0);
            if (work != 0m || transport != 0m)
                result.Add(new SectionDisplay(0, "", false, 0m, work, transport, true));
        }
        return result;
    }

    /// <summary>Mutable quantity fields can be patched in place only while
    /// row membership, numbering and the other original presentation fields agree.</summary>
    public static bool SameRowLayout(SqlServerActs.ActSheetData previous, SqlServerActs.ActSheetData next)
    {
        if (previous.Form != next.Form || previous.NumManual != next.NumManual ||
            previous.Positions.Count != next.Positions.Count || !previous.Systems.SequenceEqual(next.Systems))
            return false;
        for (var i = 0; i < next.Positions.Count; i++)
        {
            var a = previous.Positions[i];
            var b = next.Positions[i];
            if (a.PositionId != b.PositionId || a.SystemId != b.SystemId || a.NumText != b.NumText ||
                a.Name != b.Name || a.Unit != b.Unit || a.QtyByKp != b.QtyByKp ||
                a.OutOfKp != b.OutOfKp || a.WorkCode != b.WorkCode) return false;
        }
        return true;
    }

}
