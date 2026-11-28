using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Non-destructive, full-structure Excel append from the already-open KP.</summary>
public partial class EstimateView
{
    private async Task ImportStructuredExcelFile(string sourcePath, string revision)
    {
        if (_estimate is not { } snapshot || !CanAppendImport(revision))
        { SetStatus("Обнови КП перед импортом.", true); return; }
        ExcelImportResult parsed;
        try
        {
            SetStatus("Чтение структуры книги Excel…", false);
            parsed = await Task.Run(() => ExcelWorkbookReader.Read(sourcePath, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось прочитать Excel: " + ex.Message, true);
            return;
        }
        if (parsed.Blocks.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Не найдены таблицы для структурного импорта.\n" + string.Join("\n", parsed.Notes),
                "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ExcelAppendPreviewDialog(Window.GetWindow(this)!, parsed);
        if (dialog.ShowDialog() != true) return;
        var blocks = dialog.SelectedBlocks;
        if (blocks.Count == 0) return;
        if (!CanAppendImport(revision))
        {
            SetStatus("Данные или состояние окна изменились. Повтори импорт после обновления КП.", true);
            return;
        }
        if (_estimate?.Locked != false)
        {
            SetStatus("Подписанное КП нельзя изменять.", true);
            return;
        }
        if (blocks.Any(b => b.HasWork) && _estimate.SmrMode != "pos" &&
            MessageBox.Show(Window.GetWindow(this),
                "В выбранном Excel есть цены работ, но текущее КП использует другой режим расчёта СМР.\n" +
                "Цены работ будут записаны в строки, однако пока не войдут в итоги КП.\n" +
                "Режим СМР автоматически не меняется. Продолжить?",
                "СМР при импорте", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!CanAppendImport(revision))
        {
            SetStatus("КП изменилось после подтверждения. Повтори импорт.", true);
            return;
        }
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        await _writeLock.WaitAsync();
        using var cancelWrite = new CancellationTokenSource();
        var progress = new Window
        {
            Owner = Window.GetWindow(this), Title = "Импорт Excel", Width = 430, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        DynamicFormLayout.Apply(progress);
        var content = new StackPanel();
        content.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormDialogMargin");
        content.Children.Add(new TextBlock { Text = "Запись новых разделов и позиций в SQL Server…",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8 });
        var cancelButton = new Button { Content = "Отмена", Width = 95,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "PythonToolbarButton");
        cancelButton.Click += (_, _) => { cancelWrite.Cancel(); cancelButton.IsEnabled = false; };
        content.Children.Add(cancelButton);
        progress.Content = content;
        progress.Closing += (_, _) => cancelWrite.Cancel();
        progress.Show();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision))
                throw new InvalidOperationException("КП изменилось до записи импорта.");
            SetStatus($"Импорт {blocks.Sum(b => b.PositionCount)} строк Excel в новые разделы…", false);
            var saved = await Task.Run(() => SqlServerExcelAppend.Append(
                _sqlConnectionString, _contractId, sourcePath, blocks, revision, cancelWrite.Token));
            if (!saved.Saved)
            {
                _saveError = saved.Error ?? "Импорт не выполнен";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = saved.Revision;
                _saveAwaitingReadback = true;
                SetStatus($"Записано {blocks.Sum(b => b.PositionCount)} строк " +
                    $"в {blocks.Sum(b => b.Sections.Count)} разделов. Выполняется сверка…", false);
            }
        }
        catch (OperationCanceledException)
        {
            _saveError = "Импорт отменён пользователем. Транзакция SQL откатилась.";
            SetStatus(_saveError, false);
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Импорт не подтверждён: " + ex.Message;
            SetStatus(_saveError, true);
        }
        finally
        {
            progress.Close();
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }
}

/// <summary>
/// Small WPF selection dialog for append-only import into an existing estimate.
/// Original financial metadata is deliberately NOT applied to the target document.
/// </summary>
internal sealed class ExcelAppendPreviewDialog : Window
{
    private readonly List<(CheckBox Check, ExcelImportBlock Block)> _items = new();

    public IReadOnlyList<ExcelImportBlock> SelectedBlocks =>
        _items.Where(x => x.Check.IsChecked == true).Select(x => x.Block).ToList();

    public ExcelAppendPreviewDialog(Window owner, ExcelImportResult result)
    {
        Owner = owner;
        Title = "Добавить разделы из Excel — " + Path.GetFileName(result.FileName);
        Width = 960; Height = 640; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        DynamicFormLayout.Apply(this);
        var layout = new DockPanel();
        layout.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormDialogMargin");
        var intro = new TextBlock
        {
            Text = "Отметь блоки Excel. Они будут добавлены в текущий КП отдельными разделами, " +
                "с сохранением порядка, закупочных цен, СМР по позициям и комплектующих. " +
                "Существующие позиции, суммы договора, НДС и транспорт не перезаписываются.",
            TextWrapping = TextWrapping.Wrap
        };
        intro.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        DockPanel.SetDock(intro, Dock.Top); layout.Children.Add(intro);

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFooterGap");
        var cancel = new Button { Content = "Отмена", MinWidth = 105, MinHeight = 32, IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(cancel);
        var accept = new Button { Content = "Добавить разделы", MinWidth = 155, MinHeight = 32, IsDefault = true,
            Margin = new Thickness(10, 0, 0, 0) };
        accept.Click += (_, _) =>
        {
            var selected = SelectedBlocks;
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Отметь хотя бы один блок Excel.", "Импорт",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var bad = selected.Count(b => !b.Ok);
            var warning = "Будут добавлены новые разделы и позиции, без замены существующих.\n" +
                "Суммы СМР, транспорта и параметры НДС текущего КП останутся прежними.\n" +
                "Отдельные позиции работ сохранят свои цены.\n";
            if (bad > 0) warning += $"\nВ {bad} блоках суммы не сходятся либо нет итогов.\n";
            if (MessageBox.Show(this, warning + "\nПродолжить?", "Подтверждение импорта",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            DialogResult = true;
            Close();
        };
        DynamicFormLayout.StyleActions(accept, cancel);
        bar.Children.Add(accept);
        DockPanel.SetDock(bar, Dock.Bottom);
        layout.Children.Add(bar);

        var list = new StackPanel();
        foreach (var b in result.Blocks)
        {
            var wrap = new Border { BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Brushes.LightGray, Padding = new Thickness(8) };
            var body = new StackPanel();
            var label = b.Title.Length > 0 ? b.Title : b.Sheet;
            var check = new CheckBox
            {
                IsChecked = b.Ok && (b.VolumesOnly || b.MaterialSum > 0),
                Content = $"{label} · лист {b.Sheet}, строки {b.FirstRow}–{b.LastRow} " +
                    $"· {b.PositionCount} поз. · {b.Sections.Count} разделов",
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 7)
            };
            DynamicFormLayout.WrapCaption(check);
            _items.Add((check, b));
            body.Children.Add(check);
            body.Children.Add(new TextBlock
            {
                Text = $"Материалы {b.MaterialSum:N2} ₽ · СМР {b.Smr ?? 0m:N2} ₽ · " +
                    $"Итого {b.DeclaredTotal ?? b.CalculatedTotal:N2} ₽ · " +
                    (b.Ok ? "✓ суммы сходятся" : "⚠ требуется сверка"),
                Foreground = b.Ok ? Brushes.DarkGreen : Brushes.DarkRed,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 5)
            });
            foreach (var s in b.Sections.Take(12))
                body.Children.Add(DynamicFormLayout.Secondary(
                    $"{s.Name}: {s.Positions.Sum(p => 1 + p.Children.Count)} поз.", "LayoutDialogDetailInset"));
            if (b.Sections.Count > 12)
                body.Children.Add(DynamicFormLayout.Secondary(
                    $"… ещё {b.Sections.Count - 12} разделов.", "LayoutDialogDetailInset"));
            foreach (var warning in b.Warnings.Take(6))
                body.Children.Add(new TextBlock { Text = "⚠ " + warning,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 3, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)) });
            foreach (var checkResult in b.Checks.Where(c => !c.Passed).Take(4))
                body.Children.Add(new TextBlock { Text = "✗ " + checkResult.Description,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 3, 0, 0),
                    Foreground = Brushes.DarkRed });
            wrap.Child = body;
            list.Children.Add(wrap);
        }
        var scroller = new ScrollViewer { Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(scroller);
        Content = layout;
    }
}
