using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Шапка и действия документа (kp_editor.py): кнопка «Изменить» (свойства), фаза подготовки КП,
/// «Скопировать расценки», «Сохранить в Excel / PDF», блокировка, подсказка и правка итоговых карточек.
/// </summary>
public partial class EstimateView
{
    // ---------------------------------------------------------------- подсказка (help.py HintBar)
    private void HintClose_Click(object sender, RoutedEventArgs e)
    {
        HintBar.Visibility = Visibility.Collapsed;
        _uiSettings.EstimateHintHidden = true;
        SaveEstimateUiSettings();
    }

    // ---------------------------------------------------------------- свойства документа (pencil / Свойства)
    private void EditTitle_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.OpenContractProperties(_doc);
    }

    // ---------------------------------------------------------------- фаза подготовки КП (sync_kp_phase)
    private void UpdatePhaseButton()
    {
        if (PhaseButton is null) return;
        var visible = _isKp && _estimate is not null;
        PhaseButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var (phase, number) = PhaseInfo(_estimate!.KpPhase);
        PhaseButtonText.Text = $"{number} · {phase.Split(" · ")[0]} ▾";
        PhaseButton.ToolTip = $"Подготовка КП: {phase}\nНажмите, чтобы передать на следующий этап";
    }

    private static (string Title, string Number) PhaseInfo(string key) => key switch
    {
        "volumes" => ("Объёмы · Дима", "1"),
        "prices" => ("Цены · Наташа", "2"),
        "review" => ("СМР и проверка · Андрей Викторович", "3"),
        "ready" => ("Готово к отправке", "4"),
        "sent" => ("Отправлено заказчику", "5"),
        _ => ("Этап подготовки не указан", "?"),
    };

    private void Phase_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _estimate is null || !_isKp) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (key, title, number) in new[]
        {
            ("volumes", "Объёмы · Дима", "1"),
            ("prices", "Цены · Наташа", "2"),
            ("review", "СМР и проверка · Андрей Викторович", "3"),
            ("ready", "Готово к отправке", "4"),
            ("sent", "Отправлено заказчику", "5"),
        })
        {
            var phaseKey = key;
            var item = new MenuItem
            {
                Header = $"{number}. {title}",
                IsCheckable = true,
                IsChecked = _estimate.KpPhase == phaseKey
            };
            item.Click += async (_, _) => await SaveKpPhaseAsync(phaseKey);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var none = new MenuItem { Header = "Этап не указан" };
        none.Click += async (_, _) => await SaveKpPhaseAsync("");
        menu.Items.Add(none);
        menu.IsOpen = true;
    }

    private async Task SaveKpPhaseAsync(string phase)
    {
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }
        try
        {
            var changed = await Task.Run(() => SqlServerWorkspace.SetKpPhase(
                _sqlConnectionString, _contractId, phase, _serverRevision));
            if (changed)
            {
                SetStatus("Этап подготовки сохранён ✓", false);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetStatus("Этап не сохранён: " + ex.Message, true);
        }
    }

    // ---------------------------------------------------------------- Скопировать расценки (btn_transfer_rates)
    private void TransferRates_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is null || !_isKp) return;
        var dialog = new RateTransferDialog(_sqlConnectionString, _contractId, _estimate.Title)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---------------------------------------------------------------- Сохранить в Excel / PDF (btn_export)
    private void ExportExcelPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var menu = new ContextMenu { PlacementTarget = sender as Button, Placement = PlacementMode.Bottom };
        void Item(string header, bool pdf, bool internalPrices = false, bool positional = false)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.ExportKpDocument(_doc,
                internalPrices: internalPrices, positional: positional, pdf: pdf);
            menu.Items.Add(item);
        }
        Item("Excel — КП для заказчика", pdf: false);
        Item("PDF — КП для заказчика", pdf: true);
        Item("Excel — закуп-розница (для нас)", pdf: false, internalPrices: true);
        Item("Excel — по позициям (СМР «по поз.»)", pdf: false, positional: true);
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- Действия документа (btn_more menu)
    private void CopyContract_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null && _estimate is not null)
            main.CopyCurrentDocument(_doc);
        ExtraActionsExpander.IsOpen = false;
    }

    private void MakeContract_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.MakeCurrentDocumentContract(_doc);
        ExtraActionsExpander.IsOpen = false;
    }

    private void CreateDop_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.CreateDopAgreement(_doc);
        ExtraActionsExpander.IsOpen = false;
    }

    private void MoveContract_Click(object sender,RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen=false;
        if(Window.GetWindow(this) is MainWindow main && _doc.Kind=="main")
            main.MoveCurrentContract(_doc);
    }

    private void DeleteContract_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.DeleteCurrentDocument(_doc);
        ExtraActionsExpander.IsOpen = false;
    }

    private void ShowPeople_Click(object sender, RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen = false;
        new PeopleWindow(_sqlConnectionString, _objectId) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void ShowOrders_Click(object sender, RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen = false;
        new OrdersWindow(new SqlServerPeopleAccess(_sqlConnectionString), _objectId)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen = false;
        try
        {
            new HistoryWindow(_sqlConnectionString, contractId: _contractId)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "История изменений",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveDocumentLink_Click(object sender, RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen = false;
        if (Window.GetWindow(this) is MainWindow main)
            main.CreateGmcsLink(_doc);
    }

    private void CompareDocs_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.CompareCurrentDocument(_doc);
        ExtraActionsExpander.IsOpen = false;
    }

    // ---------------------------------------------------------------- блокировка (toggle_lock)
    private async void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is null) return;
        ExtraActionsExpander.IsOpen = false;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }
        var toLock = !_estimate.Locked;
        if (toLock)
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                "Заблокировать смету? Цены, количество и строки больше не поменяются случайно.",
                "Заблокировать — смета подписана", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
        }
        else
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                "Снять блокировку? Цены и количество снова можно менять.",
                "Снять блокировку", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        try
        {
            var changed = await Task.Run(() => SqlServerWorkspace.SetContractLocked(
                _sqlConnectionString, _contractId, toLock, _serverRevision));
            if (changed)
            {
                SetStatus(toLock ? "Смета заблокирована ✓" : "Блокировка снята ✓", false);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetStatus("Блокировка не изменена: " + ex.Message, true);
        }
    }

    // ---------------------------------------------------------------- итоговые карточки (tile_smr / tile_tr / tile_pnr)
    private void SetSmrControls(EstimateDocument est)
    {
        var mode = string.IsNullOrWhiteSpace(est.SmrMode) ? "sum" : est.SmrMode;
        foreach (var item in SmrModeCombo.Items.Cast<ComboBoxItem>())
            if ((string)item.Tag == mode)
            {
                _smrComboSilent = true;
                SmrModeCombo.SelectedItem = item;
                _smrComboSilent = false;
                break;
            }
        var byPos = mode == "pos";
        var bySys = mode == "systems";
        var pct = mode == "percent";
        SmrEdit.IsReadOnly = est.Locked || pct || bySys || byPos;
        SmrEdit.ToolTip = byPos
            ? "СМР по позициям: сумма работ по строкам — правится в колонке «Работа за ед.»"
            : bySys ? "СМР по системам: правится в строках систем"
            : pct ? $"Сохранённый процент от материалов (только просмотр): {NumberText.QtyText(est.SmrPercent)}% = {N2(est.Totals.Smr)} ₽"
            : "СМР: общая сумма (Enter — сохранить)";
        switch (mode)
        {
            case "pos":
                var emptyWork = _rows.Count(r => !r.IsSystem && !r.IsSub && !r.IsVirtual &&
                                                 r.Source.WorkPrice is null or 0m);
                SmrHintText.Text = "сумма работ по строкам" +
                    (emptyWork > 0 ? $" · без цены работы: {emptyWork}" : "");
                break;
            case "systems":
                SmrHintText.Text = $"сумма СМР систем — правится в строках систем";
                break;
            case "percent":
                SmrHintText.Text = $"= {N2(est.Totals.Smr)} ₽";
                break;
            default:
                SmrHintText.Text = est.Statistics.Materials != 0 && est.Statistics.Smr != 0
                    ? $"{est.Statistics.Smr / est.Statistics.Materials * 100m:0.0}% от материалов"
                    : "";
                break;
        }
        if ((pct || bySys || byPos) && est.Statistics.WorkRows != 0)
            SmrHintText.Text += (SmrHintText.Text.Length == 0 ? "" : " · ") +
                $"+ монтаж строками {N2(est.Statistics.WorkRows)}";
        SmrHintText.Visibility = string.IsNullOrEmpty(SmrHintText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        TransportEdit.IsReadOnly = est.Locked;
        PnrEdit.IsReadOnly = est.Locked;
    }

    private void TotalsEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (sender is TextBox box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        e.Handled = true;
    }

    private void SmrEdit_Commit(object sender, RoutedEventArgs e) => SaveTotalsFieldFromEdit(
        SmrEdit, "smr_amount", _estimate?.Statistics.Smr, "СМР");

    private void TransportEdit_Commit(object sender, RoutedEventArgs e) => SaveTotalsFieldFromEdit(
        TransportEdit, "transport_amount", _estimate?.Totals.Transport, "Транспорт");

    private void PnrEdit_Commit(object sender, RoutedEventArgs e) => SaveTotalsFieldFromEdit(
        PnrEdit, "pnr_amount", _estimate?.Totals.Pnr, "Пусконаладка");

    private async void SaveTotalsFieldFromEdit(TextBox box, string field, decimal? current, string caption)
    {
        if (_estimate is null || _estimate.Locked || box.IsReadOnly) return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }
        var text = box.Text.Trim();
        decimal? value;
        if (text.Length == 0)
            value = null;
        else if (NumberText.Parse(text) is { } parsed)
            value = parsed;
        else
        {
            SetStatus($"{caption}: впишите сумму числом", true);
            return;
        }
        if (value is decimal v && v < 0)
        {
            SetStatus($"{caption}: отрицательная сумма недопустима", true);
            return;
        }
        if (value == current || (value is null && current == 0m)) return;
        await SaveTotalsFieldAsync(field, value, caption);
    }

    private async Task<bool> SaveTotalsFieldAsync(string field, decimal? value, string caption,
        string? displayedRevision = null)
    {
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Сохранение… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict)
            {
                _saveError ??= "Смета изменилась на сервере. Сначала обнови её.";
                return false;
            }
            if (displayedRevision is not null && displayedRevision != _serverRevision)
            {
                _saveError = "Итоги изменились до записи. Обнови смету.";
                _serverConflict = true;
                return false;
            }
            var expectedRevision = displayedRevision ?? _serverRevision;
            var saved = await Task.Run(() => SqlServerCostTotals.SaveTotalsField(
                _sqlConnectionString, _contractId, field, value, expectedRevision));
            if (!saved.Saved)
            {
                _saveError = saved.Error ?? $"{caption} не сохранено";
                _serverConflict = true;
                return false;
            }
            _serverRevision = saved.Revision;
            _saveAwaitingReadback = true;
            return true;
        }
        catch (Exception ex)
        {
            // Неизвестный результат COMMIT: запретить последующие записи до сверки.
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Не удалось подтвердить сохранение: " + ex.Message;
            return false;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? $"{caption}: записано в SQL Server, выполняется сверка…"
                    : "Ошибка записи: " + _saveError, _saveError is not null);
                _reloadTimer.Start();
            }
        }
    }

    // ---------------------------------------------------------------- режим СМР (_save_smr_mode)
    private bool _smrComboSilent;

    private async void SmrModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_smrComboSilent || _estimate is null) return;
        if (SmrModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        if (_estimate.Locked)
        {
            SetStatus("Смета подписана: режим СМР не меняется.", true);
            SetSmrControls(_estimate);
            return;
        }
        if (mode == _estimate.SmrMode) return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            SetSmrControls(_estimate);
            return;
        }
        _reloadTimer.Stop();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Сохранение… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            var saved = await Task.Run(() => SqlServerSmrMode.SetSmrMode(
                _sqlConnectionString, _contractId, mode, _serverRevision));
            if (!saved.Saved)
            {
                _saveError = saved.Error ?? "Режим СМР не изменён";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = saved.Revision;
                _saveAwaitingReadback = true;
            }
        }
        catch (Exception ex)
        {
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Режим СМР сохранён ✓"
                    : "Ошибка записи: " + _saveError, _saveError is not null);
                _reloadTimer.Start();
            }
        }
    }
}
