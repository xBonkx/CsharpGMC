using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Standalone manual entry point: the shared warehouse window can open this window
/// through a one-line hook after coordination with #70. Constructing/previewing this
/// window never creates tables or performs migration. No automatic startup hook.
/// </summary>
internal sealed class MetalLegacyImportPreviewWindow : Window
{
    private readonly SqlServerMetalLegacyImport _import;
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas")
    };
    private readonly TextBox _databaseConfirmation = new() { MinWidth = 170 };
    private readonly TextBox _sourceUtcOffset = new() { MinWidth = 65 };
    private readonly Button _importButton = new()
    {
        Content = "Подтвердить однократный импорт", IsEnabled = false,
        Padding = new Thickness(12, 7, 12, 7)
    };
    private MetalLegacySqlPreview? _preview;

    public MetalLegacyImportPreviewWindow(string connectionString)
    {
        _import = new SqlServerMetalLegacyImport(connectionString);
        Title = "История металла — проверка перед миграцией";
        Width = 1040;
        Height = 720;
        MinWidth = 740;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        WorkspaceWindowUi.StyleButton(_importButton, primary: true, icon: "IconLabelUpload");

        var root = new DockPanel { Margin = new Thickness(16) };
        var actions = new StackPanel { Orientation = Orientation.Vertical };
        DockPanel.SetDock(actions, Dock.Top);
        root.Children.Add(actions);
        var preview = new Button { Content = "Проверить историю (только чтение)",
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left };
        WorkspaceWindowUi.StyleButton(preview, icon: "IconLabelSearch");
        preview.Click += (_, _) => LoadPreview();
        actions.Children.Add(preview);

        actions.Children.Add(new TextBlock
        {
            Text = "Импорт возможен только после полного read-only preview. " +
                   "Введите точное имя тестовой базы и смещение времени исходной Python-БД.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var confirmation = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        confirmation.Children.Add(new TextBlock
        {
            Text = "Тестовая база: ", VerticalAlignment = VerticalAlignment.Center
        });
        confirmation.Children.Add(_databaseConfirmation);
        confirmation.Children.Add(new TextBlock
        {
            Text = "Часовой пояс источника (например 10:00): ",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        confirmation.Children.Add(_sourceUtcOffset);
        confirmation.Children.Add(_importButton);
        _importButton.Margin = new Thickness(12, 0, 0, 0);
        _importButton.Click += (_, _) => ConfirmImport();
        actions.Children.Add(confirmation);
        root.Children.Add(_details);
        Content = root;
    }

    private void LoadPreview()
    {
        _preview = null;
        _importButton.IsEnabled = false;
        try
        {
            var p = _import.Preview();
            _preview = p;
            var h = p.History;
            var sb = new StringBuilder();
            sb.AppendLine($"SQL Server: {p.Server} / {p.Database}");
            sb.AppendLine($"meta.rev: {p.Revision}");
            sb.AppendLine($"Исходный SHA-256: {h.SourceSha256}");
            sb.AppendLine($"Событий: {h.EventCount}; с {h.FirstDate:yyyy-MM-dd HH:mm:ss} по {h.LastDate:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Python opening marker: {p.PythonOpening}");
            sb.AppendLine($"C# opening: {p.ExistingOpening}; движений: {p.ExistingMovements}; " +
                          $"партий: {p.ExistingLots}; материалов: {p.ExistingMaterials}; " +
                          $"предыдущих миграций: {p.PriorLegacyImports}; provenance: {p.PriorMappedEvents}; " +
                          $"схема готова: {p.TargetSchemaPresent}");
            sb.AppendLine($"Итоги, тысячные м² — физически: {h.Physical}; " +
                          $"у поставщика: {h.Supplier}; резерв: {h.Reserved}");
            sb.AppendLine();
            sb.AppendLine("События по типам:");
            foreach (var e in h.EventCounts)
                sb.AppendLine($"  {e.Key}: {e.Value}");
            sb.AppendLine();
            sb.AppendLine("Остатки по материалам и местам:");
            foreach (var e in h.Places)
                sb.AppendLine($"  {e.Category} / {e.Material} / {e.Place}: " +
                              $"{e.Area} milli-m², {e.Pieces} шт., резерв {e.Reserved}");
            sb.AppendLine();
            sb.AppendLine("Партии / оплаты / получение:");
            foreach (var e in h.Lots)
                sb.AppendLine($"  #{e.Id} {e.Supplier} / {e.Invoice}: " +
                    $"заказано {e.Ordered}/{e.OrderedPieces}, оплачено={e.Paid}, " +
                    $"получено {e.Received}/{e.ReceivedPieces}");
            sb.AppendLine();
            sb.AppendLine("Объекты / потребность / выдача / резерв:");
            foreach (var e in h.Objects)
                sb.AppendLine($"  {e.Category}/{e.Material} / {e.ObjectId} {e.ObjectName}: " +
                              $"{e.Demand} / {e.Issued} / {e.Reserved}");
            foreach (var e in h.Warnings) sb.AppendLine("WARNING: " + e);
            foreach (var e in h.Errors) sb.AppendLine("ERROR: " + e);
            sb.AppendLine(p.Ready
                ? "READY FOR EXPLICIT PILOT-ONLY IMPORT; это ещё не финальная приёмка."
                : "BLOCKED: есть ошибки, прежняя миграция или отсутствует opening marker.");
            _details.Text = sb.ToString();
            _importButton.IsEnabled = p.Ready;
        }
        catch (Exception ex)
        {
            _details.Text = "Preview FAILED (без записи): " + ex.Message;
        }
    }

    private void ConfirmImport()
    {
        var p = _preview;
        if (p is null || !p.Ready) return;
        if (!string.Equals(_databaseConfirmation.Text.Trim(), p.Database, StringComparison.Ordinal) ||
            !TimeSpan.TryParse(_sourceUtcOffset.Text.Trim(), CultureInfo.InvariantCulture, out var offset))
        {
            MessageBox.Show(this, "Введите точное имя базы и смещение времени (ЧЧ:мм).");
            return;
        }
        if (MessageBox.Show(this,
                $"Выполнить НЕОБРАТИМЫЙ однократный импорт {p.History.EventCount} исторических событий?\n" +
                $"Сервер: {p.Server}\nБаза: {p.Database}\nSHA: {p.History.SourceSha256}\n" +
                "Не является финальной приёмкой. Производственную базу не затрагивать.",
                "Проверка источника и подтверждение", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _importButton.IsEnabled = false; // never blind-retry, especially after unknown COMMIT
        try
        {
            var receipt = _import.Import(p.Server, p.Database, p.Revision,
                p.History.SourceSha256, offset, confirmedByOperator: true);
            MessageBox.Show(this, $"Readback: {receipt.State}, событий {receipt.EventCount}, SHA {receipt.SourceSha256}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Импорт не подтверждён: " + ex.Message +
                "\nНе нажимайте повторно до независимой сверки Readback().",
                "Миграция заблокирована", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
