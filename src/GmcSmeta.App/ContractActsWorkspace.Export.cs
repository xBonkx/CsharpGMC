using System.Diagnostics;
using System.IO;
using System.Windows;
using GmcSmeta.Core;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class ContractActsWorkspace
{
    private CancellationTokenSource? _summaryExportCancellation;

    private async void SummaryExport_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryExportCancellation is not null || _summaryCancellation is not null ||
            !IsVisible || SummaryPage.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(_summaryRevision)) return;
        var summary = _summary;
        var totals = _totals;
        if (summary is null || totals is null || _summaryActs.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Сначала откройте сводную: нет данных.",
                "Сводная в Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Snapshot collections are replaced on reload, never edited by the UI.
        // Capture the act order, title and date before the dialog/worker.
        var acts = _summaryActs.OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
        var title = _title;
        var date = DateTime.Today;
        var version = _summaryVersion;
        var owner = Window.GetWindow(this);
        using var cancellation = new CancellationTokenSource();
        _summaryExportCancellation = cancellation;
        var token = cancellation.Token;
        var originalContent = SummaryExportButton.Content;
        SummaryExportButton.IsEnabled = false;
        ExportFilePublication.PreparedExport? prepared = null;
        bool IsCurrent() => !token.IsCancellationRequested && version == _summaryVersion &&
            IsLoaded && IsVisible && SummaryPage.Visibility == Visibility.Visible &&
            ReferenceEquals(summary, _summary);
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить в Excel",
                Filter = "Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.Concat(("Сводная — " + title).Select(c => invalid.Contains(c) ? ' ' : c)).Trim(),
            };
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true || !IsCurrent())
                return;
            var destination = Path.GetFullPath(dialog.FileName);
            SummaryExportButton.Content = "Формирование Excel…";
            var timer = Stopwatch.StartNew();
            prepared = await Task.Run(() => ExportFilePublication.Stage(destination, stream =>
            {
                using var book = KsProgressWorkbook.Create(summary, totals, acts, title, date, token);
                token.ThrowIfCancellationRequested();
                book.SaveAs(stream);
                token.ThrowIfCancellationRequested();
            }), token);
            var renderMs = timer.ElapsedMilliseconds;
            if (!IsCurrent()) return;

            // Commit point: after this check publication is allowed to finish.
            // Navigation cannot start another export until this one exits.
            timer.Restart();
            await Task.Run(prepared.Publish);
            Trace.WriteLine($"KS progress export: render+stage={renderMs}ms; publish={timer.ElapsedMilliseconds}ms");
            if (!IsCurrent()) return;
            try { Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
            catch { /* The saved workbook is valid without a viewer. */ }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent())
                MessageBox.Show(owner, ex.Message, "Сводная в Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
            else Trace.WriteLine("KS progress export failed after navigation: " + ex.GetType().Name);
        }
        finally
        {
            if (prepared is not null) await Task.Run(prepared.Dispose);
            _summaryExportCancellation = null;
            SummaryExportButton.Content = originalContent;
            SummaryExportButton.IsEnabled = true;
        }
    }
}
