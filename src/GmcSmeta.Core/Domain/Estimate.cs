using System.Globalization;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public enum RowKind { System, Position }

/// <summary>Строка таблицы сметы: заголовок системы или позиция.</summary>
/// <param name="Level">0 — система, 1 — позиция, 2 — подсистема / подпозиция…</param>
/// <param name="SortKey">Порядок в базе (как в Python): система → позиции по «order».</param>
/// <param name="IsSub">Подпозиция (комплектующая): у неё нет своей цены и суммы.</param>
public sealed record EstimateRow(
    RowKind Kind,
    int Level,
    int SortKey,
    int Id,
    bool IsSub,
    string Num,
    string Name,
    string Unit,
    decimal Qty,
    decimal Price,
    decimal Amount,
    decimal? PurchasePrice,
    bool OutOfKp,
    string Note)
{
    // The UI needs the owning section to insert a row without guessing from row numbers.
    public int SystemId { get; init; }
    public int? ParentSystemId { get; init; }
    public int? ParentPositionId { get; init; }
    public decimal? WorkPrice { get; init; }
    public string Formulas { get; init; } = "";
    public double? AreaManual { get; init; }
    public bool IsAreaSummary { get; init; }
    public string Basis { get; init; } = "";

    public bool IsSystem => Kind == RowKind.System;

    // Пустые значения у систем и у строк без цены — чтобы таблица не пестрела нулями.
    public decimal? DisplayQty => IsSystem ? null : Qty;
    public decimal? DisplayPrice => IsSystem || IsSub || Price == 0m ? null : Price;
    public decimal? DisplayAmount => Amount == 0m || IsSub ? null : Amount;

    /// <summary>Позиция без цены: в Python её цена подсвечена красным.</summary>
    public bool HasNoPrice => !IsSystem && !IsSub && Price == 0m;
}

/// <summary>Документ сметы целиком: шапка, строки и итоги.</summary>
public sealed record EstimateDocument(
    int ContractId,
    string Title,
    string KindTitle,
    string ObjectName,
    string Company,
    string Form,
    decimal VatRate,
    bool VatTop,
    bool Locked,
    string LockedBy,
    int Systems,
    int Positions,
    IReadOnlyList<EstimateRow> Rows,
    IReadOnlyList<EstimateRow> PositionsInOrder,
    ContractTotals Totals)
{
    public EstimateStatistics Statistics { get; init; } = new(0, 0, 0);
    public string SmrMode { get; init; } = "";
    public bool BasisAllowed { get; init; }
    // Этап подготовки КП (kp_workflow.PHASES эталона): volumes / prices / review / ready / sent.
    public string KpPhase { get; init; } = "";
    public decimal? DeclaredMaterial { get; init; }
    public decimal? DeclaredTotal { get; init; }
    public EstimateSourceCheck.Result SourceCheck => EstimateSourceCheck.Compare(DeclaredMaterial,DeclaredTotal,Totals);
}

/// <summary>Contract.stats в Python: монтаж строками показан в СМР, общий итог не меняется.</summary>
public sealed record EstimateStatistics(decimal Materials, decimal Smr, decimal WorkRows)
{
    public static bool IsWorkName(string name)
    {
        var text = name.Trim().ToLowerInvariant();
        if (new[] { "пусконалад", "пуско-налад", "пнр ", "работы по " }.Any(p => text.StartsWith(p, StringComparison.Ordinal))) return true;
        return new[] { "монтаж", "демонтаж", "наладка" }.Any(word => text == word ||
            text.StartsWith(word, StringComparison.Ordinal) && !char.IsLetterOrDigit(text[word.Length]));
    }
}

/// <summary>Поиск названия по всем словам, с сохранением родителей и совпавшей ветки.</summary>
public static class EstimateFilter
{
    public static IReadOnlyList<EstimateRow> Apply(IReadOnlyList<EstimateRow> rows, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return rows;
        var visible = new bool[rows.Count];
        var ancestors = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            while (ancestors.Count > 0 && rows[ancestors[^1]].Level >= rows[i].Level)
                ancestors.RemoveAt(ancestors.Count - 1);
            if (words.All(word => rows[i].Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                visible[i] = true;
                foreach (var parent in ancestors) visible[parent] = true;
                for (var j = i + 1; j < rows.Count && rows[j].Level > rows[i].Level; j++) visible[j] = true;
            }
            ancestors.Add(i);
        }
        return rows.Where((_, i) => visible[i]).ToList();
    }
}

/// <summary>
/// Чтение сметы документа: системы (включая подсистемы) и позиции (включая подпозиции)
/// в том же порядке, в каком их показывает Python-версия.
/// </summary>
public static class EstimateReader
{
    // Один батч вместо последовательных LoadHead/TagRules/Systems/Positions.
    // Только простые SELECT: одинаково работает на SQL Server и SQLite (офлайн-тесты).
    private const string BatchSql =
        "SELECT c.id, c.kind, c.number, c.[date], c.name, o.name, COALESCE(co.name, ''), " +
        "       c.form, c.smr_mode, c.vat_rate, c.locked, c.locked_by, COALESCE(c.kp_phase, ''), " +
        "       c.smr_amount, c.smr_percent, c.transport_amount, c.pnr_amount, c.declared_material, c.declared_total " +
        "FROM contracts c JOIN objects o ON o.id = c.object_id " +
        "LEFT JOIN companies co ON co.id = c.company_id WHERE c.id = @id;" +
        "SELECT [key], rules FROM tags;" +
        "SELECT id, name, parent_id, \"order\", smr_amount, source_num FROM systems " +
        "WHERE contract_id = @id ORDER BY \"order\", id;" +
        "SELECT p.id, p.system_id, p.parent_id, p.num, p.name, p.unit, p.qty, p.price, " +
        "       p.purchase_price, p.out_of_kp, p.note, p.\"order\", p.work_price, " +
        "       p.area_manual, p.basis, p.extra_json, p.formulas " +
        "FROM positions p JOIN systems sy ON sy.id = p.system_id " +
        "WHERE sy.contract_id = @id ORDER BY p.\"order\", p.id";

    private sealed class SystemRow
    {
        public int Id;
        public string Name = "";
        public int? ParentId;
        public int Order;
        public decimal SmrAmount;
        public string SourceNum = "";
    }

    private sealed class PositionRow
    {
        public int Id;
        public int SystemId;
        public int? ParentId;
        public string Num = "";
        public string Name = "";
        public string Unit = "";
        public decimal Qty;
        public decimal Price;
        public decimal? PurchasePrice;
        public bool OutOfKp;
        public string Note = "";
        public int Order;
        public decimal? WorkPrice;
        public double? AreaManual;
        public string Basis = "";
        public string ExtraJson = "";
        public string Formulas = "";
    }

    public static EstimateDocument Load(DbConnection conn, int contractId)
    {
        // Один RTT на документ: реквизиты, правила тегов, системы и позиции
        // идут одним батчем. Итоги считаются из этих же строк.
        using var cmd = CreateLoadCommand(conn, contractId);
        using var r = cmd.ExecuteReader();
        var head = ReadHead(r)
                   ?? throw new InvalidOperationException($"нет документа {contractId}");
        if (!r.NextResult())
            throw new InvalidOperationException("Батч сметы: нет правил тегов.");
        var rules = TagRules.Read(r);
        if (!r.NextResult())
            throw new InvalidOperationException("Батч сметы: нет систем.");
        var systems = ReadSystems(r);
        if (!r.NextResult())
            throw new InvalidOperationException("Батч сметы: нет позиций.");
        var positions = ReadPositions(r);

        return BuildDocument(contractId, head, rules, systems, positions);
    }

    public static async Task<EstimateDocument> LoadSqlAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, int contractId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = CreateLoadCommand(conn, contractId);
        using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await r.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException($"нет документа {contractId}");
        var head = ReadHeadCurrent(r);
        if (!await r.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Батч сметы: нет правил тегов.");
        var rules = await TagRules.ReadSqlAsync(r, token).ConfigureAwait(false);
        if (!await r.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Батч сметы: нет систем.");
        var systems = new List<SystemRow>();
        while (await r.ReadAsync(token).ConfigureAwait(false))
            systems.Add(ReadSystemCurrent(r));
        if (!await r.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Батч сметы: нет позиций.");
        var positions = new List<PositionRow>();
        while (await r.ReadAsync(token).ConfigureAwait(false))
            positions.Add(ReadPositionCurrent(r));
        return BuildDocument(contractId, head, rules, systems, positions, token);
    }

    private static DbCommand CreateLoadCommand(DbConnection conn, int contractId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlSchemaCompatibility.HasPreviewColumns(conn)
            ? BatchSql.Replace("c.declared_total ", "c.declared_total, c.discount_percent ", StringComparison.Ordinal)
            : BatchSql;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = contractId;
        cmd.Parameters.Add(parameter);

        return cmd;
    }

    private static EstimateDocument BuildDocument(int contractId, Head head, TagRules rules,
        List<SystemRow> systems, List<PositionRow> positions, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var roundRows = rules.Has(head.Form, "round_rows");
        var vatTop = rules.Has(head.Form, "vat_top");
        // Форма «ДНС» (r4) и теги с num_manual: номер в колонке «№» — из документа заказчика.
        var numManual = string.Equals(head.Form, "r4", StringComparison.Ordinal) ||
                        rules.Has(head.Form, "num_manual");
        if (string.Equals(head.Form, "r4", StringComparison.OrdinalIgnoreCase))
            foreach (var p in positions)
                p.Basis = AreaCellPresentation.WorkCode(p.Basis, p.ExtraJson);
        var bySystem = positions.GroupBy(p => p.SystemId)
                                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ThenBy(p => p.Id).ToList());

        var rows = new List<EstimateRow>();
        var sort = 0;
        var num = 1;   // сквозная нумерация документа, как в Python: разделы и позиции подряд

        // «Без системы» — служебный раздел: в Python он не нумеруется.
        const string NoSystem = "Без системы";

        // Сумма материалов системы: только её верхние позиции (подпозиции входят в цену родителя),
        // без «вне КП». Как System.material_sum в Python.
        var sumBySystem = new Dictionary<int, decimal>();
        foreach (var (systemId, list) in bySystem)
        {
            decimal sum = 0m;
            foreach (var p in list.Where(p => p.ParentId is null && !p.OutOfKp))
            {
                var amount = p.Qty * p.Price;
                sum += roundRows ? Money.Round2(amount) : amount;
            }
            sumBySystem[systemId] = sum;
        }

        decimal GroupMaterial(SystemRow system) =>
            sumBySystem.GetValueOrDefault(system.Id) +
            systems.Where(x => x.ParentId == system.Id).Sum(x => sumBySystem.GetValueOrDefault(x.Id));

        // Примечание системы: её СМР (только когда СМР считается по системам). Как COL_NOTE в Python.
        string SystemNote(SystemRow system)
        {
            if (!string.Equals(head.SmrMode, "systems", StringComparison.Ordinal)) return "";
            var subs = systems.Where(x => x.ParentId == system.Id).ToList();
            var group = system.SmrAmount + subs.Sum(x => x.SmrAmount);
            if (subs.Count > 0 && system.SmrAmount == 0m)
                return group != 0m ? $"СМР раздела {Money.Format(group)}" : "СМР раздела —";
            return system.SmrAmount != 0m ? $"СМР {Money.Format(system.SmrAmount)}" : "СМР —";
        }

        void AddSystem(SystemRow system, int level)
        {
            var showHeader = !string.Equals(system.Name, NoSystem, StringComparison.Ordinal);
            var rowNum = numManual
                ? system.SourceNum
                : showHeader ? (num++).ToString() : "";
            rows.Add(new EstimateRow(RowKind.System, level, sort++, 0, false, rowNum, system.Name, "",
                0m, 0m, GroupMaterial(system), null, false, SystemNote(system))
            { SystemId = system.Id, ParentSystemId = system.ParentId });
        }

        void AddPositions(SystemRow system, int level)
        {
            if (!bySystem.TryGetValue(system.Id, out var list)) return;

            var children = list.Where(p => p.ParentId is not null)
                               .GroupBy(p => p.ParentId!.Value)
                               .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ThenBy(p => p.Id).ToList());

            foreach (var p in list.Where(p => p.ParentId is null))
            {
                token.ThrowIfCancellationRequested();
                var rowNum = numManual ? p.Num : p.OutOfKp ? "" : (num++).ToString();
                rows.Add(MakeRow(p, level, roundRows, sort++, rowNum));
                if (children.TryGetValue(p.Id, out var subs))
                    foreach (var s in subs)
                        rows.Add(MakeRow(s, level + 1, roundRows, sort++, numManual ? s.Num : ""));
            }
        }

        var top = systems.Where(s => s.ParentId is null).OrderBy(s => s.Order).ThenBy(s => s.Id).ToList();
        foreach (var system in top)
        {
            AddSystem(system, 0);
            AddPositions(system, 1);
            foreach (var sub in systems.Where(s => s.ParentId == system.Id).OrderBy(s => s.Order).ThenBy(s => s.Id))
            {
                AddSystem(sub, 1);
                AddPositions(sub, 2);
            }
        }

        token.ThrowIfCancellationRequested();
        var totals = Totals.CalculateOne(rules,
            new TotalsContract(head.ContractId, head.Kind, head.Form, head.VatRate,
                head.SmrMode, head.SmrAmount, head.SmrPercent, head.Transport, head.Pnr, head.DiscountPercent),
            systems.Select(s => new TotalsSystem(s.Id, s.SmrAmount)).ToList(),
            positions.Select(p => new TotalsPosition(p.SystemId, p.Qty, p.Price,
                p.WorkPrice ?? 0m, p.OutOfKp, p.ParentId is not null)).ToList(),
            positions.Count);

        var inOrder = rows.Where(r => r.Kind == RowKind.Position).OrderBy(r => r.SortKey).ToList();

        return new EstimateDocument(
            contractId, head.Title, head.KindTitle, head.ObjectName, head.Company,
            head.Form, head.VatRate, vatTop, head.Locked, head.LockedBy,
            rows.Count(r => r.Kind == RowKind.System), inOrder.Count,
            rows, inOrder, totals)
        {
            SmrMode = head.SmrMode,
            BasisAllowed = AreaCellPresentation.IsBasisAllowed(head.Form, rules.Has(head.Form, "basis")),
            Statistics = Statistics(totals, positions),
            KpPhase = head.KpPhase,
            DeclaredMaterial = head.DeclaredMaterial,
            DeclaredTotal = head.DeclaredTotal
        };
    }

    private static EstimateStatistics Statistics(ContractTotals totals, IReadOnlyList<PositionRow> positions)
    {
        // Python work_rows_sum rounds the sum once, independently of round_rows.
        var work = decimal.Round(positions.Where(p => p.ParentId is null && !p.OutOfKp && EstimateStatistics.IsWorkName(p.Name))
            .Sum(p => p.Qty * p.Price), 2, MidpointRounding.ToEven);
        return new EstimateStatistics(decimal.Round(totals.Materials - work, 2, MidpointRounding.ToEven),
            decimal.Round(totals.Smr + work, 2, MidpointRounding.ToEven), work);
    }

    private static EstimateRow MakeRow(PositionRow p, int level, bool roundRows, int sortKey, string num)
    {
        // Показ всегда до копеек (как колонка «Сумма» в Python); итоги считает Totals — там точная сумма.
        var amount = Money.Round2(p.Qty * p.Price);
        return new EstimateRow(RowKind.Position, level, sortKey, p.Id, p.ParentId is not null,
            num, p.Name, p.Unit, p.Qty, p.Price, amount, p.PurchasePrice, p.OutOfKp, p.Note)
        {
            SystemId = p.SystemId, ParentPositionId = p.ParentId, WorkPrice = p.WorkPrice,
            AreaManual = p.AreaManual, IsAreaSummary = Area.IsSummary(p.ExtraJson),
            Basis = p.Basis, Formulas = p.Formulas
        };
    }

    private sealed class Head
    {
        public int ContractId;
        public string Title = "";
        public string KindTitle = "";
        public string ObjectName = "";
        public string Company = "";
        public string Form = "";
        public string SmrMode = "";
        public decimal VatRate;
        public bool Locked;
        public string LockedBy = "";
        public string Kind = "";
        public string KpPhase = "";
        public decimal SmrAmount;
        public decimal SmrPercent;
        public decimal Transport;
        public decimal Pnr;
        public decimal DiscountPercent;
        public decimal? DeclaredMaterial;
        public decimal? DeclaredTotal;
    }

    private static Head? ReadHead(DbDataReader r)
    {
        return r.Read() ? ReadHeadCurrent(r) : null;
    }

    private static Head ReadHeadCurrent(DbDataReader r)
    {
        var kind = Str(r, 1);
        var kindTitle = kind switch { "main" => "Договор", "dop" => "Допсоглашение", _ => "КП" };
        return new Head
        {
            ContractId = Convert.ToInt32(r.GetValue(0)),
            Kind = kind,
            KindTitle = kindTitle,
            Title = Title(kindTitle, Str(r, 2), Str(r, 3), Str(r, 4)),
            ObjectName = Str(r, 5),
            Company = Str(r, 6),
            Form = Str(r, 7),
            SmrMode = Str(r, 8),
            VatRate = Dec(r, 9),
            Locked = !r.IsDBNull(10) && Convert.ToBoolean(r.GetValue(10)),
            LockedBy = Str(r, 11),
            KpPhase = Str(r, 12),
            SmrAmount = Dec(r, 13),
            SmrPercent = Dec(r, 14),
            Transport = Dec(r, 15),
            Pnr = Dec(r, 16),
            DeclaredMaterial = r.IsDBNull(17)?null:Dec(r,17),
            DeclaredTotal = r.IsDBNull(18)?null:Dec(r,18),
            DiscountPercent = r.FieldCount > 19 ? DiscountPolicy.ReadPercent(r, 19) : 0m,
        };
    }

    private static List<SystemRow> ReadSystems(DbDataReader r)
    {
        var list = new List<SystemRow>();
        while (r.Read()) list.Add(ReadSystemCurrent(r));
        return list;
    }

    private static SystemRow ReadSystemCurrent(DbDataReader r) => new SystemRow
    {
        Id = Convert.ToInt32(r.GetValue(0)),
        Name = Str(r, 1),
        ParentId = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2)),
        Order = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3)),
        SmrAmount = Dec(r, 4),
        SourceNum = Str(r, 5),
    };

    private static List<PositionRow> ReadPositions(DbDataReader r)
    {
        var list = new List<PositionRow>();
        while (r.Read()) list.Add(ReadPositionCurrent(r));
        return list;
    }

    private static PositionRow ReadPositionCurrent(DbDataReader r) => new PositionRow
    {
        Id = Convert.ToInt32(r.GetValue(0)),
        SystemId = Convert.ToInt32(r.GetValue(1)),
        ParentId = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2)),
        Num = Str(r, 3),
        Name = Str(r, 4),
        Unit = Str(r, 5),
        Qty = Dec(r, 6),
        Price = Dec(r, 7),
        PurchasePrice = r.IsDBNull(8) ? null : Dec(r, 8),
        OutOfKp = !r.IsDBNull(9) && Convert.ToBoolean(r.GetValue(9)),
        Note = Str(r, 10),
        Order = r.IsDBNull(11) ? 0 : Convert.ToInt32(r.GetValue(11)),
        WorkPrice = r.IsDBNull(12) ? null : Dec(r, 12),
        AreaManual = r.IsDBNull(13) ? null : Convert.ToDouble(r.GetValue(13), CultureInfo.InvariantCulture),
        Basis = Str(r, 14),
        ExtraJson = Str(r, 15),
        Formulas = Str(r, 16),
    };

    private static string Title(string kindTitle, string number, string date, string name)
    {
        var t = kindTitle;
        if (number.Length > 0) t += $" № {number}";
        if (date.Length > 0) t += $" от {date}";
        if (name.Length > 0) t += $" — {name}";
        return t;
    }

    private static string Str(DbDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";

    private static decimal Dec(DbDataReader r, int i) =>
        r.IsDBNull(i) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(i)));
}
