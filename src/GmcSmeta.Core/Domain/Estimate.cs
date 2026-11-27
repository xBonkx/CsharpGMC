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
    // Display-only title from the same SQL snapshot as the full document title.
    public string? AnnexDisplayTitle { get; init; }
    public EstimateStatistics Statistics { get; init; } = new(0, 0, 0);
    public string SmrMode { get; init; } = "";
    public decimal SmrPercent { get; init; }
    public bool MoneyFromStages { get; init; }
    public bool BasisAllowed { get; init; }
    // Этап подготовки КП (kp_workflow.PHASES эталона): volumes / prices / review / ready / sent.
    public string KpPhase { get; init; } = "";
    public bool SupportsFinalDiscount { get; init; }
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
        if (text.StartsWith("пусконалад", StringComparison.Ordinal) ||
            text.StartsWith("пуско-налад", StringComparison.Ordinal) ||
            text.StartsWith("пнр ", StringComparison.Ordinal) ||
            text.StartsWith("работы по ", StringComparison.Ordinal)) return true;
        bool Word(string word) => text == word ||
            text.StartsWith(word, StringComparison.Ordinal) && !char.IsLetterOrDigit(text[word.Length]);
        return Word("монтаж") || Word("демонтаж") || Word("наладка");
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
        int? matchedLevel = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            while (ancestors.Count > 0 && rows[ancestors[^1]].Level >= row.Level)
                ancestors.RemoveAt(ancestors.Count - 1);

            // A matching parent reveals its entire subtree. Do not search or
            // walk that subtree again for every matching descendant.
            if (matchedLevel is int level && row.Level <= level)
                matchedLevel = null;
            if (matchedLevel.HasValue)
                visible[i] = true;
            else if (words.All(word => row.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                visible[i] = true;
                matchedLevel = row.Level;
                // Once an ancestor is visible, all of its ancestors are
                // already visible as well. Each hidden ancestor is marked once.
                for (var j = ancestors.Count - 1; j >= 0; j--)
                {
                    var parent = ancestors[j];
                    if (visible[parent]) break;
                    visible[parent] = true;
                }
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
public static partial class EstimateReader
{
    // Один батч вместо последовательных LoadHead/TagRules/Systems/Positions.
    // Только простые SELECT: одинаково работает на SQL Server и SQLite (офлайн-тесты).
    private const string HeadSql =
        "SELECT c.id, c.kind, c.number, c.[date], c.name, o.name, COALESCE(co.name, ''), " +
        "       c.form, c.smr_mode, c.vat_rate, c.locked, c.locked_by, COALESCE(c.kp_phase, ''), " +
        "       c.smr_amount, c.smr_percent, c.transport_amount, c.pnr_amount, c.declared_material, c.declared_total " +
        "FROM contracts c JOIN objects o ON o.id = c.object_id " +
        "LEFT JOIN companies co ON co.id = c.company_id WHERE c.id = @id;";

    private const string BatchSql = HeadSql +
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
        cmd.CommandText = WithDiscountColumn(conn, BatchSql);
        if (conn is Microsoft.Data.SqlClient.SqlConnection)
            cmd.CommandText = cmd.CommandText.Replace("FROM contracts c JOIN objects",
                ", CASE WHEN c.stage_of IS NULL AND EXISTS (SELECT 1 FROM contracts st " +
                "WHERE st.stage_of=c.id AND st.stage_sum IS NOT NULL AND ISNULL(st.stage_rest,0)=0) " +
                "THEN 1 ELSE 0 END AS money_from_stages FROM contracts c JOIN objects",
                StringComparison.Ordinal);
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = contractId;
        cmd.Parameters.Add(parameter);

        return cmd;
    }

    private static string WithDiscountColumn(DbConnection conn, string sql) =>
        SqlSchemaCompatibility.HasPreviewColumns(conn)
            ? sql.Replace("c.declared_total ", "c.declared_total, c.discount_percent ", StringComparison.Ordinal)
            : sql;

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

        // One parent index for the whole snapshot. Lookup groups preserve
        // source order for monetary sums; display order is sorted explicitly.
        var systemsByParent = systems.ToLookup(system => system.ParentId);

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
            systemsByParent[system.Id].Sum(x => sumBySystem.GetValueOrDefault(x.Id));

        // Примечание системы: её СМР (только когда СМР считается по системам). Как COL_NOTE в Python.
        string SystemNote(SystemRow system)
        {
            if (!string.Equals(head.SmrMode, "systems", StringComparison.Ordinal)) return "";
            var subs = systemsByParent[system.Id];
            var group = system.SmrAmount + subs.Sum(x => x.SmrAmount);
            if (subs.Any() && system.SmrAmount == 0m)
                return group != 0m ? $"СМР раздела {Money.Format(group)}" : "СМР раздела —";
            return system.SmrAmount != 0m ? $"СМР {Money.Format(system.SmrAmount)}" : "СМР —";
        }

        void AddSystem(SystemRow system, int level)
        {
            token.ThrowIfCancellationRequested();
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

            // bySystem is already ordered by Order, then Id. ToLookup
            // retains that order inside each group; no second sort needed.
            var children = list.Where(p => p.ParentId is not null)
                               .ToLookup(p => p.ParentId!.Value);

            foreach (var p in list.Where(p => p.ParentId is null))
            {
                token.ThrowIfCancellationRequested();
                var rowNum = numManual ? p.Num : p.OutOfKp ? "" : (num++).ToString();
                rows.Add(MakeRow(p, level, roundRows, sort++, rowNum));
                foreach (var s in children[p.Id])
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(MakeRow(s, level + 1, roundRows, sort++, numManual ? s.Num : ""));
                }
            }
        }

        var top = systemsByParent[null].OrderBy(s => s.Order).ThenBy(s => s.Id);
        foreach (var system in top)
        {
            AddSystem(system, 0);
            AddPositions(system, 1);
            foreach (var sub in systemsByParent[system.Id].OrderBy(s => s.Order).ThenBy(s => s.Id))
            {
                AddSystem(sub, 1);
                AddPositions(sub, 2);
            }
        }

        token.ThrowIfCancellationRequested();
        var totals = CalculateTotals(head, rules, systems, positions, token);

        // SortKey is assigned monotonically as rows are appended.
        var inOrder = rows.Where(r => r.Kind == RowKind.Position).ToList();

        return new EstimateDocument(
            contractId, head.Title, head.KindTitle, head.ObjectName, head.Company,
            head.Form, head.VatRate, vatTop, head.Locked, head.LockedBy,
            rows.Count(r => r.Kind == RowKind.System), inOrder.Count,
            rows, inOrder, totals)
        {
            AnnexDisplayTitle = head.Kind == "main" ? AgreementPresentation.AnnexTitle(contractId, head.Name) : null,
            SmrMode = head.SmrMode,
            SmrPercent = head.SmrPercent,
            MoneyFromStages = head.MoneyFromStages,
            BasisAllowed = AreaCellPresentation.IsBasisAllowed(head.Form, rules.Has(head.Form, "basis")),
            Statistics = Statistics(totals, positions),
            KpPhase = head.KpPhase,
            SupportsFinalDiscount=DiscountPolicy.SupportsDiscount(vatTop,rules.Has(head.Form,"ks_dns"),head.Form,head.ObjectName),
            DeclaredMaterial = head.DeclaredMaterial,
            DeclaredTotal = head.DeclaredTotal
        };
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
        public string Name = "";
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
        public bool MoneyFromStages;
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
            ContractId = SqlInteger.ReadInt32(r, 0),
            Kind = kind,
            KindTitle = kindTitle,
            Title = Title(kindTitle, Str(r, 2), Str(r, 3), Str(r, 4)),
            Name = Str(r, 4),
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
            DiscountPercent = r.FieldCount > 19 && r.GetName(19) != "money_from_stages"
                ? DiscountPolicy.ReadPercent(r, 19) : 0m,
            MoneyFromStages = r.GetName(r.FieldCount - 1) == "money_from_stages" &&
                Convert.ToBoolean(r.GetValue(r.FieldCount - 1), CultureInfo.InvariantCulture),
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
        Id = SqlInteger.ReadInt32(r, 0),
        Name = Str(r, 1),
        ParentId = r.IsDBNull(2) ? null : SqlInteger.ReadInt32(r, 2),
        Order = r.IsDBNull(3) ? 0 : SqlInteger.ReadInt32(r, 3),
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
        Id = SqlInteger.ReadInt32(r, 0),
        SystemId = SqlInteger.ReadInt32(r, 1),
        ParentId = r.IsDBNull(2) ? null : SqlInteger.ReadInt32(r, 2),
        Num = Str(r, 3),
        Name = Str(r, 4),
        Unit = Str(r, 5),
        Qty = Dec(r, 6),
        Price = Dec(r, 7),
        PurchasePrice = r.IsDBNull(8) ? null : Dec(r, 8),
        OutOfKp = !r.IsDBNull(9) && Convert.ToBoolean(r.GetValue(9)),
        Note = Str(r, 10),
        Order = r.IsDBNull(11) ? 0 : SqlInteger.ReadInt32(r, 11),
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
