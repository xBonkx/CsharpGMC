using System.Globalization;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Demo;

/// <summary>Temporary in-process estimate fixture. Not a database or SQL emulator.
/// The real editor is not connected to this store yet.
/// </summary>
public sealed class DemoEstimateStore
{
    private static readonly string[] Names =
    [
        "Воздуховод оцинкованный", "Отвод 90°", "Решётка вентиляционная",
        "Клапан противопожарный", "Изоляция воздуховода", "Монтаж вентиляционной системы"
    ];
    private static readonly string[] Units = ["м²", "шт", "шт", "шт", "м²", "компл"];
    private readonly object _gate = new();
    private List<EstimateRow> _rows = [];
    private int _nextId;
    private long _revision;

    public DemoEstimateStore(int positions = 100) => Reset(positions);
    public long Revision { get { lock (_gate) return _revision; } }

    public IReadOnlyList<EstimateRow> Snapshot()
    {
        lock (_gate) return _rows.ToArray();
    }

    public void Reset(int positions = 100)
    {
        if (positions is < 1 or > 50_000)
            throw new ArgumentOutOfRangeException(nameof(positions));
        var rows = new List<EstimateRow>(positions + 4);
        var previousSection = 0;
        for (var i = 0; i < positions; i++)
        {
            var section = 1 + i * 4 / positions;
            if (section != previousSection)
            {
                rows.Add(new EstimateRow(RowKind.System, 0, rows.Count, 0, false, "",
                    $"Система П{section}", "", 0, 0, 0, null, false, "") { SystemId = section });
                previousSection = section;
            }
            var qty = 1m + i % 20 / 4m;
            var price = i % 11 == 0 ? 0m : 125m + i % 97 * 17.50m;
            decimal? purchase = i % 7 == 0 ? null : Money.Amount(price, 0.6m);
            var row = Position(i + 1, section,
                $"{Names[i % Names.Length]} {i + 1}", Units[i % Units.Length],
                qty, price, purchase, i % 17 == 0 ? "Тестовое примечание" : "");
            rows.Add(row with { SortKey = rows.Count, Num = (i + 1).ToString(CultureInfo.InvariantCulture) });
        }
        lock (_gate)
        {
            _rows = rows;
            _nextId = positions + 1;
            _revision++;
        }
    }

    public int AddPosition(int section, string name, string unit, decimal qty, decimal price,
        decimal? purchasePrice = null, string note = "")
    {
        lock (_gate)
        {
            var index = _rows.FindLastIndex(r => r.SystemId == section);
            if (index < 0) throw new ArgumentException("Система не найдена.", nameof(section));
            var id = _nextId;
            var row = Position(id, section, name, unit, qty, price, purchasePrice, note);
            _rows.Insert(index + 1, row);
            _nextId++;
            Reindex();
            _revision++;
            return id;
        }
    }

    public void UpdatePosition(int id, string name, string unit, decimal qty, decimal price,
        decimal? purchasePrice = null, string note = "")
    {
        lock (_gate)
        {
            var index = FindPosition(id);
            var previous = _rows[index];
            var row = Position(id, previous.SystemId, name, unit, qty, price, purchasePrice, note);
            _rows[index] = row with { SortKey = previous.SortKey, Num = previous.Num };
            _revision++;
        }
    }

    public void DeletePosition(int id)
    {
        lock (_gate)
        {
            _rows.RemoveAt(FindPosition(id));
            Reindex();
            _revision++;
        }
    }

    private int FindPosition(int id)
    {
        var index = _rows.FindIndex(row => row.Kind == RowKind.Position && row.Id == id);
        return index >= 0 ? index : throw new ArgumentException("Позиция не найдена.", nameof(id));
    }

    private void Reindex()
    {
        var number = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            _rows[i] = row with { SortKey = i, Num = row.IsSystem ? "" : (++number).ToString(CultureInfo.InvariantCulture) };
        }
    }

    private static EstimateRow Position(int id, int section, string name, string unit,
        decimal qty, decimal price, decimal? purchasePrice, string note)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Введите наименование.", nameof(name));
        if (qty < 0 || price < 0 || purchasePrice is < 0) throw new ArgumentOutOfRangeException(nameof(qty));
        return new EstimateRow(RowKind.Position, 1, 0, id, false, "", name.Trim(), unit,
            qty, price, Money.Amount(qty, price), purchasePrice, false, note) { SystemId = section };
    }
}
