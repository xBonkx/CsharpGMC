using System.Windows;

namespace GmcSmeta.App;

/// <summary>Offline, read-only presentation sample; never constructs a database connection.</summary>
public partial class DemoWindow : Window
{
    public DemoWindow()
    {
        InitializeComponent();
        DemoObjects.ItemsSource = new[]
        {
            new DemoObject(1, "Учебный объект — офис", "В работе"),
            new DemoObject(2, "Учебный объект — магазин", "Черновик")
        };
        DemoEstimate.ItemsSource = new[]
        {
            new DemoPosition("Вентиляция", "Воздуховод оцинкованный", "м²", 75m, 1900m),
            new DemoPosition("Вентиляция", "Вентилятор канальный", "шт", 2m, 28500m),
            new DemoPosition("Монтаж", "Монтаж воздуховода", "м²", 75m, 750m)
        };
        DemoProcurement.ItemsSource = new[]
        {
            new DemoPurchase("Воздуховод", "75 м²", "Демо-поставщик 1", "Не заказано"),
            new DemoPurchase("Вентилятор", "2 шт", "Демо-поставщик 2", "В пути")
        };
        DemoCost.ItemsSource = new[]
        {
            new DemoCostRow("Материалы", 120000m, 199500m),
            new DemoCostRow("Работы", 22500m, 56250m)
        };
    }

    public sealed record DemoObject(int Number, string Name, string Status);
    public sealed record DemoPosition(string Section, string Name, string Unit,
        decimal Quantity, decimal Price)
    {
        public decimal Total => Quantity * Price;
    }
    public sealed record DemoPurchase(string Material, string Quantity, string Supplier, string Status);
    public sealed record DemoCostRow(string Name, decimal Purchase, decimal Retail)
    {
        public decimal Margin => Retail - Purchase;
    }
}
