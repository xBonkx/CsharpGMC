using System.Data.Common;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public static partial class Totals
{
    // One entry per known system/contract, not per position. SQL order is
    // system_id, order, id; each contract's work sum therefore follows the
    // same sequence as the old nested loops over systems ordered by id.
    private sealed class SqlSums
    {
        internal readonly Dictionary<int, decimal> MaterialBySystem = new();
        internal readonly Dictionary<int, decimal> WorkByContract = new();
    }

    private static SqlSums ReadSqlSums(DbDataReader reader, TagRules rules,
        IReadOnlyList<ContractRow> contracts, IReadOnlyDictionary<int, List<SystemRow>> systems,
        CancellationToken token = default)
    {
        var ownership = new Dictionary<int, (int ContractId, bool RoundRows)>();
        foreach (var contract in contracts)
        {
            if (!systems.TryGetValue(contract.Id, out var sections)) continue;
            var roundRows = rules.Has(contract.Form, "round_rows");
            foreach (var section in sections)
                ownership.Add(section.Id, (contract.Id, roundRows));
        }

        var sums = new SqlSums();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var systemId = SqlInteger.ReadInt32(reader, 0);
            if (!ownership.TryGetValue(systemId, out var owner)) continue;
            var qty = Dec(reader, 1);
            var amount = qty * Dec(reader, 2);
            var work = qty * Dec(reader, 3);
            sums.MaterialBySystem[systemId] = sums.MaterialBySystem.GetValueOrDefault(systemId) +
                (owner.RoundRows ? Money.Round2(amount) : amount);
            // Do not sum work by system first: the original decimal work
            // accumulator was contract-wide, including signed quantities.
            sums.WorkByContract[owner.ContractId] = sums.WorkByContract.GetValueOrDefault(owner.ContractId) + work;
        }
        return sums;
    }

    private static IReadOnlyList<ContractTotals> FinishSqlSums(TagRules rules,
        IReadOnlyList<ContractRow> contracts, IReadOnlyDictionary<int, List<SystemRow>> systems,
        SqlSums sums, IReadOnlyDictionary<int, int> counts)
    {
        var result = new List<ContractTotals>(contracts.Count);
        foreach (var contract in contracts)
        {
            decimal materials = 0m;
            decimal systemsSmr = 0m;
            if (systems.TryGetValue(contract.Id, out var sections))
                foreach (var section in sections)
                {
                    systemsSmr += section.SmrAmount;
                    materials += sums.MaterialBySystem.GetValueOrDefault(section.Id);
                }
            result.Add(FinishContract(contract, rules.Has(contract.Form, "vat_top"),
                materials, sums.WorkByContract.GetValueOrDefault(contract.Id),
                systemsSmr, counts.GetValueOrDefault(contract.Id)));
        }
        return result;
    }
}
