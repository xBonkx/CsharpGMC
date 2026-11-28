using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private CancellationTokenSource? _actExportCancellation;

    // -------------------------------------------------------------- выгрузки (меню «Ещё»)

    private static string SafeName(string name)
    {
        foreach (var c in "\\/:?*[]\"<>|")
            name = name.Replace(c, ' ');
        return name.Trim();
    }

    /// <summary>«Сохранить КС-2 и КС-3» — Excel (форма заказчика) или PDF этого акта.</summary>
    private async void ExportAct_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureActCommand();
        var act = Current;
        if (_exportInProgress || target is null || act is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить КС-2 и КС-3",
            FileName = SafeName($"КС-2, КС-3 №{act.Number} от {act.Date:dd.MM.yyyy} {_title}"),
            Filter = "Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf",
        };
        if (dlg.ShowDialog() != true) return;
        var isPdf = dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        await ExportActFileAsync(target, dlg.FileName, "КС-2 и КС-3",
            token => SqlServerKsPrintData.ReadSingle(target.ConnectionString, target.ContractId, target.ActId, target.Revision, token),
            (package, stream, token) => isPdf
                ? SaveKsPdf(stream, () => KsPdfForms.Create(package), token)
                : SaveKsWorkbook(stream, () => KsCustomerForms.Create(package), token));
    }

    /// <summary>«Ведомость объёмов работ (ВОР) по этому акту…».</summary>
    private async void ExportVor_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureMoreCommand(sender);
        var act = Current;
        if (_exportInProgress || target is null || act is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ведомость объёмов работ",
            FileName = SafeName($"ВОР №{act.Number} от {act.Date:dd.MM.yyyy} {_title}"),
            Filter = "Excel (*.xlsx)|*.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        await ExportActFileAsync(target, dlg.FileName, "ВОР",
            token => SqlServerKsPrintData.ReadSingle(target.ConnectionString, target.ContractId, target.ActId, target.Revision, token),
            (package, stream, token) => SaveKsWorkbook(stream, () => KsVorForms.Create(package, target.ActId), token));
    }

    /// <summary>All acts of this document, in one workbook (Python export_all).</summary>
    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureMoreCommand(sender);
        if (_exportInProgress || target is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Все акты в Excel",
            FileName = SafeName($"КС-2, КС-3 все акты {_title} {DateTime.Today:dd.MM.yyyy}"),
            Filter = "Excel (*.xlsx)|*.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        await ExportActFileAsync(target, dlg.FileName, "всех актов",
            token => SqlServerKsPrintData.ReadAll(target.ConnectionString, target.ContractId, target.Revision, token),
            (packages, stream, token) => SaveKsWorkbook(stream, () => KsCustomerForms.CreateAll(packages), token));
    }

    private static (long BuildMs, long SaveMs) SaveKsWorkbook(Stream stream,
        Func<ClosedXML.Excel.XLWorkbook> create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var book = create();
        var build = timer.ElapsedMilliseconds;
        if (book.Worksheets.Count == 0)
            throw new InvalidDataException("В книге нет листов.");
        token.ThrowIfCancellationRequested();
        timer.Restart();
        book.SaveAs(stream);
        token.ThrowIfCancellationRequested();
        return (build, timer.ElapsedMilliseconds);
    }

    private static (long BuildMs, long SaveMs) SaveKsPdf(Stream stream, Func<byte[]> create,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var bytes = create();
        var build = timer.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();
        timer.Restart();
        stream.Write(bytes);
        token.ThrowIfCancellationRequested();
        return (build, timer.ElapsedMilliseconds);
    }

    // The snapshot and output are independent of the live UI. Recheck the
    // editor context after each await and publish only a complete staged file.
    private async Task ExportActFileAsync<T>(ActCommandTarget target, string destination,
        string label, Func<CancellationToken, T> read,
        Func<T, Stream, CancellationToken, (long BuildMs, long SaveMs)> render)
    {
        if (_exportInProgress) return;
        _exportInProgress = true;
        using var cancellation = new CancellationTokenSource();
        _actExportCancellation = cancellation;
        var token = cancellation.Token;
        SaveButton.IsEnabled = false;
        var generation = _actLoadGeneration;
        var attachment = _exportAttachmentGeneration;
        bool IsSameView() => IsLoaded && IsVisible &&
            generation == _actLoadGeneration && attachment == _exportAttachmentGeneration;
        void RequireExportContext()
        {
            token.ThrowIfCancellationRequested();
            if (!IsSameView()) throw new OperationCanceledException();
            RequireCurrentActCommand(target);
        }

        ExportFilePublication.PreparedExport? prepared = null;
        var timings = new System.Diagnostics.Stopwatch();
        try
        {
            RequireExportContext();
            SetStatus("Подготовка " + label + "…");
            timings.Start();
            var snapshot = await Task.Run(() => read(token), token);
            var sqlMs = timings.ElapsedMilliseconds;
            RequireExportContext();

            timings.Restart();
            var phases = (BuildMs: 0L, SaveMs: 0L);
            prepared = await Task.Run(() => ExportFilePublication.Stage(destination,
                stream =>
                {
                    token.ThrowIfCancellationRequested();
                    phases = render(snapshot, stream, token);
                    token.ThrowIfCancellationRequested();
                }), token);
            var renderMs = timings.ElapsedMilliseconds;
            RequireExportContext();

            // Commit point: after the final UI-context check the already
            // prepared file may be published even if the user navigates away.
            timings.Restart();
            await Task.Run(prepared.Publish);
            System.Diagnostics.Trace.WriteLine($"KS export: SQL={sqlMs}ms; build={phases.BuildMs}ms; save={phases.SaveMs}ms; render+stage={renderMs}ms; publish={timings.ElapsedMilliseconds}ms");
            if (IsSameView() && IsCurrentActCommand(target))
                SetStatus("Сохранено: " + Path.GetFileName(destination));
        }
        catch (OperationCanceledException)
        {
            if (IsSameView()) SetStatus("Экспорт отменён.");
        }
        catch (Exception ex)
        {
            if (IsSameView() && token.IsCancellationRequested) SetStatus("Экспорт отменён.");
            else if (IsSameView()) SetStatus("Ошибка сохранения: " + ex.Message, true);
            else System.Diagnostics.Trace.WriteLine("KS export failed after navigation: " + ex.GetType().Name);
        }
        finally
        {
            if (prepared is not null)
                await Task.Run(prepared.Dispose);
            if (ReferenceEquals(_actExportCancellation, cancellation)) _actExportCancellation = null;
            _exportInProgress = false;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>«Общая КС-3 на несколько КС-2 (№1.1 + №1.2 → КС-3 №1)…».</summary>
    private void ExportGroup_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureMoreCommand(sender);
        var current = Current;
        if (target is null || current is null || _exportInProgress) return;
        OpenGroupReport(KsWorkspaceProjection.GroupKey(current), target.Revision);
    }

    private void OpenGroupReport(string key, string? expectedRevision = null)
    {
        try
        {
            var report = new KsGroupReportWindow(_connectionString, _contractId, key, expectedRevision)
            {
                Owner = Window.GetWindow(this)
            };
            report.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка расчёта КС-3: " + ex.Message, true);
        }
    }
}
