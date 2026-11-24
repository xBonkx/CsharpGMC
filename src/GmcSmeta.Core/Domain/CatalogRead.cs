using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>Читающие операции справочника (перенос builder.catalog_entries / price_history).</summary>
public static partial class Catalog
{
    public sealed record Entry(long Id, string Name, string Unit, decimal? Price,
        decimal? PurchasePrice, int Uses, decimal? WorkPrice);

    public sealed record PriceHistoryRow(string Object, string Contract, string Date,
        string System, decimal Qty, decimal Price, decimal? PurchasePrice, long ContractId);

    /// <summary>
    /// Полный каталог, количество использований и последние РАСЦЕНЁННЫЕ позиции.
    /// Цена и закупка всегда из одной последней строки с price &gt; 0;
    /// расценка работ — из своей последней строки с work_price &gt; 0.
    /// Считает за один проход по позициям (вместо запроса на каждый элемент).
    /// </summary>
    public static IReadOnlyList<Entry> Entries(SqliteConnection conn)
    {
        var uses = new Dictionary<long, int>();
        var latestPrice = new Dictionary<long, (decimal Price, decimal? Purchase)>();
        var latestWork = new Dictionary<long, decimal>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT catalog_item_id, price, purchase_price, work_price FROM positions " +
                              "WHERE catalog_item_id IS NOT NULL ORDER BY id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                uses[id] = uses.GetValueOrDefault(id) + 1;
                if (!r.IsDBNull(1) && r.GetDouble(1) > 0 && !latestPrice.ContainsKey(id))
                    latestPrice.Add(id, (Money.FromDouble(r.GetDouble(1)),
                        r.IsDBNull(2) ? null : Money.FromDouble(r.GetDouble(2))));
                if (!r.IsDBNull(3) && r.GetDouble(3) > 0 && !latestWork.ContainsKey(id))
                    latestWork.Add(id, Money.FromDouble(r.GetDouble(3)));
            }
        }

        var result = new List<Entry>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, unit FROM catalog_items ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var found = latestPrice.TryGetValue(id, out var last);
                result.Add(new Entry(id, r.GetString(1), r.GetString(2),
                    found ? last.Price : null, found ? last.Purchase : null,
                    uses.GetValueOrDefault(id), latestWork.TryGetValue(id, out var work) ? work : null));
            }
        }
        return result;
    }

    /// <summary>История цен элемента по убыванию id позиции, включая нулевые цены.</summary>
    public static IReadOnlyList<PriceHistoryRow> PriceHistory(SqliteConnection conn, long itemId)
    {
        var rows = new List<PriceHistoryRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT o.name, c.kind, c.number, c.date, c.name, c.created_at, " +
            "s.name, p.qty, p.price, p.purchase_price, c.id " +
            "FROM positions p JOIN systems s ON s.id = p.system_id " +
            "JOIN contracts c ON c.id = s.contract_id " +
            "JOIN objects o ON o.id = c.object_id " +
            "WHERE p.catalog_item_id = $id ORDER BY p.id DESC";
        cmd.Parameters.AddWithValue("$id", itemId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string Text(int index) => r.IsDBNull(index) ? "" : r.GetString(index);
            var date = Text(3);
            if (date.Length == 0 && DateTime.TryParse(Text(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created))
                date = created.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            rows.Add(new PriceHistoryRow(Text(0),
                Documents.ContractTitleText(Text(1), Text(2), Text(3), Text(4)),
                date, Text(6), Money.FromDouble(r.GetDouble(7)),
                Money.FromDouble(r.GetDouble(8)),
                r.IsDBNull(9) ? null : Money.FromDouble(r.GetDouble(9)), r.GetInt64(10)));
        }
        return rows;
    }
}
