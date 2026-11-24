using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Строка отчёта замены: было в КП → станет (replace_dialog.refresh эталона).</summary>
public sealed class ReplacePreviewRow
{
    public string WasName { get; init; } = "";
    public string WasUnit { get; init; } = "";
    public string WasQty { get; init; } = "";
    public string WasPrice { get; init; } = "";
    public string WasPurchase { get; init; } = "";
    public string NewName { get; init; } = "";
    public string NewUnit { get; init; } = "";
    public string NewQty { get; init; } = "";
    public string NewPrice { get; init; } = "";
}

/// <summary>
/// «Заменить выделенные из буфера» (replace_dialog.py): строки счёта, скопированные
/// в Excel, становятся на место выделенных строк КП. Пары идут по порядку; при равном
/// числе строки правятся на месте, при разном — удаляются и вставляются скопированные.
/// По умолчанию цена счёта идёт в «Закупку», количество КП остаётся; всё переключается.
/// </summary>
public partial class ReplaceDialog : Window
{
    /// <summary>Что менять в строке КП; null — поле остаётся прежним.</summary>
    public sealed record ReplaceField(string? Name, string? Unit, decimal? Qty, decimal? Price, decimal? PurchasePrice);

    /// <summary>Настройки замены — память процесса, как settings в Python (окно настроек ещё не перенесено).</summary>
    internal static string LastPriceMode = "purch";
    internal static decimal LastMarkup = 1.44m;
    internal static bool LastReplaceQty;

    private readonly IReadOnlyList<ReplaceRowData> _old;
    private readonly IReadOnlyList<ReplaceRowData> _new;
    private readonly IReadOnlyList<string> _excluded;

    /// <summary>Строка для замены: то, что было в КП, и то, что пришло из буфера.</summary>
    public record ReplaceRowData(string Name, string Unit, decimal Qty, decimal Price,
        decimal? PurchasePrice, bool HasPrice);

    public bool SameCount => _old.Count == _new.Count;

    public ReplaceDialog(IReadOnlyList<ReplaceRowData> old, IReadOnlyList<ReplaceRowData> newRows,
        IReadOnlyList<string> excluded)
    {
        _old = old;
        _new = newRows;
        _excluded = excluded;
        InitializeComponent();
        CaptionText.Text = $"Заменить строки КП ({_old.Count}) → строками из буфера ({_new.Count})";
        HintText.Text = SameCount
            ? "Пары идут по порядку: первая выделенная строка ↔ первая скопированная. Примечание, комплектующие, " +
              "закупки и место в системе остаются."
            : "⚠ Число строк разное: выделенные строки будут удалены, а скопированные встанут на их место " +
              "(количество — из буфера).";
        TakeQty.IsChecked = !SameCount || LastReplaceQty;
        TakeQty.IsEnabled = SameCount;
        (LastPriceMode switch
        {
            "price" => ModePrice, "markup" => ModeMarkup,
            "none" => ModeNone, _ => ModePurch,
        }).IsChecked = true;
        if (!_new.Any(n => n.HasPrice)) ModeNone.IsChecked = true;
        MarkupBox.Text = LastMarkup.ToString("0.##", CultureInfo.CurrentCulture);
        ExcludedText.Text = _excluded.Count > 0
            ? $"Вне сметы: {_excluded.Count}. Эти строки показаны внизу. См. КС-2 или «Закупки»."
            : "";
        ExcludedText.Visibility = _excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Для каждой пары (или каждой строки буфера) — какие поля проставить.</summary>
    internal IReadOnlyList<ReplaceField> ComputeResult()
    {
        var same = SameCount;
        var takeName = TakeName.IsChecked == true;
        var takeUnit = TakeUnit.IsChecked == true;
        var takeQty = TakeQty.IsChecked == true;
        var markup = ParseMarkup();
        var list = new List<ReplaceField>();
        foreach (var n in _new)
        {
            string? name = null, unit = null;
            decimal? qty = null, price = null, purchase = null;
            if (takeName || !same) name = n.Name;
            if ((takeUnit || !same) && n.Unit.Length > 0) unit = n.Unit;
            if (takeQty || !same) qty = n.Qty;
            if (n.HasPrice)
            {
                var v = n.Price;
                if (ModePurch.IsChecked == true) purchase = v;
                else if (ModePrice.IsChecked == true) price = v;
                else if (ModeMarkup.IsChecked == true)
                {
                    purchase = v;
                    price = decimal.Round(v * markup, 2, MidpointRounding.AwayFromZero);
                }
            }
            list.Add(new ReplaceField(name, unit, qty, price, purchase));
        }
        return list;
    }

    private decimal ParseMarkup() =>
        decimal.TryParse(MarkupBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture,
            out var value) && value is >= 0.5m and <= 10m ? value : LastMarkup;

    private void Refresh()
    {
        var same = SameCount;
        var res = ComputeResult();
        var rows = new ObservableCollection<ReplacePreviewRow>();
        var before = _old.Sum(o => o.Qty * o.Price);
        decimal after = 0m;
        var n = Math.Max(_old.Count, _new.Count);
        for (var i = 0; i < n; i++)
        {
            var o = i < _old.Count ? _old[i] : null;
            var r = res[i];
            ReplaceField? baseRow = same && o is not null
                ? new ReplaceField(o.Name, o.Unit, o.Qty, o.Price, o.PurchasePrice)
                : new ReplaceField("", "", 0m, 0m, null);
            string QtyText(decimal v) => v == decimal.Truncate(v)
                ? v.ToString("0", CultureInfo.CurrentCulture)
                : v.ToString("0.###", CultureInfo.CurrentCulture);
            var name = r.Name ?? baseRow.Name ?? "";
            var unit = r.Unit ?? baseRow.Unit ?? "";
            var qty = r.Qty ?? baseRow.Qty ?? 0m;
            var price = r.Price ?? baseRow.Price ?? 0m;
            var purch = r.PurchasePrice ?? baseRow.PurchasePrice;
            after += qty * price;
            rows.Add(new ReplacePreviewRow
            {
                WasName = o?.Name ?? "",
                WasUnit = o?.Unit ?? "",
                WasQty = o is null ? "" : QtyText(o.Qty),
                WasPrice = o is null || o.Price == 0m ? "—" : Money.Format(o.Price),
                WasPurchase = o?.PurchasePrice is null ? "" : Money.Format(o.PurchasePrice.Value),
                NewName = name,
                NewUnit = unit,
                NewQty = QtyText(qty),
                NewPrice = $"{(price == 0m ? "—" : Money.Format(price))} / {(purch is null ? "—" : Money.Format(purch.Value))}",
            });
        }
        foreach (var name in _excluded)
            rows.Add(new ReplacePreviewRow { WasName = name, NewName = "Вне сметы" });
        PreviewGrid.ItemsSource = rows;
        TotalText.Text = $"Сумма этих строк для клиента: {Money.Format(before)} → {Money.Format(after)} ₽. " +
            "Отменить замену — Ctrl+Z";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        LastPriceMode = ModePurch.IsChecked == true ? "purch"
            : ModePrice.IsChecked == true ? "price"
            : ModeMarkup.IsChecked == true ? "markup" : "none";
        LastMarkup = ParseMarkup();
        if (SameCount) LastReplaceQty = TakeQty.IsChecked == true;
        DialogResult = true;
    }
}
