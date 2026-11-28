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
        if (_estimate is { } snapshot && !_doc.IsStage &&
            CanChangeHeader(snapshot, _serverRevision, false) &&
            Window.GetWindow(this) is MainWindow main)
            main.OpenContractProperties(_doc, _publishedEstimateRevision);
    }

    // ---------------------------------------------------------------- фаза подготовки КП (sync_kp_phase)
    private void UpdatePhaseButton()
    {
        if (PhaseButton is null) return;
        var visible = _isKp && !_doc.IsStage && !_doc.StageRest && _estimate is not null;
        PhaseButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var (phase, number) = PhaseInfo(_estimate!.KpPhase);
        PhaseButtonText.Text = $"{number} · {phase.Split(" · ")[0]} ▾";
        PhaseButton.ToolTip = $"Подготовка КП: {phase}\nНажмите, чтобы выбрать этап подготовки";
        PhaseButton.IsEnabled = !_estimate.Locked && !_doc.StageRest;
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
        if (sender is not Button button || _estimate is not { } snapshot || !_isKp ||
            _doc.IsStage || _doc.StageRest) return;
        var revision = _serverRevision;
        if (!CanChangeHeader(snapshot, revision, allowLocked: false)) return;
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
                IsChecked = snapshot.KpPhase == phaseKey
            };
            item.Click += async (_, _) => await SaveKpPhaseAsync(snapshot, revision, phaseKey);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var none = new MenuItem { Header = "Этап не указан" };
        none.Click += async (_, _) => await SaveKpPhaseAsync(snapshot, revision, "");
        menu.Items.Add(none);
        menu.IsOpen = true;
    }

    private Task SaveKpPhaseAsync(EstimateDocument snapshot, string revision, string phase)
    {
        if (!_isKp || _doc.IsStage || _doc.StageRest || phase == (snapshot.KpPhase ?? ""))
            return Task.CompletedTask;
        return SaveHeaderMutationAsync(snapshot, revision, false,
            () => SqlServerWorkspace.SetKpPhase(_sqlConnectionString, _contractId, phase, revision),
            "Этап подготовки");
    }

    private bool CanChangeHeader(EstimateDocument snapshot, string revision, bool allowLocked) =>
        !_detached && IsLoaded && IsEnabled && !_serverConflict && !_doc.StageRest &&
        ReferenceEquals(snapshot, _estimate) && (allowLocked || !snapshot.Locked) &&
        !string.IsNullOrWhiteSpace(revision) && revision == _serverRevision &&
        revision == _publishedEstimateRevision && CanTransferDocument &&
        Window.GetWindow(this) is MainWindow main &&
        main.IsCurrentEstimatePage(_contractId, _objectId, this) &&
        main.CanEditEstimateHeader(_contractId, _objectId);

    // Header changes participate in the same FIFO writer gate as estimate cells.
    // The displayed revision remains fixed, including after a modal confirmation.
    private async Task SaveHeaderMutationAsync(EstimateDocument snapshot, string revision,
        bool allowLocked, Func<bool> writer, string caption)
    {
        if (!CanChangeHeader(snapshot, revision, allowLocked))
        {
            SetStatus("Документ изменился или занят сохранением. Откройте меню заново.", true);
            return;
        }
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        SetStatus(caption + ": сохранение…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || !ReferenceEquals(snapshot, _estimate) ||
                revision != _serverRevision || Window.GetWindow(this) is not MainWindow main ||
                !main.IsCurrentEstimatePage(_contractId, _objectId, this) ||
                !main.CanEditEstimateHeader(_contractId, _objectId))
            {
                _saveError = "Документ изменился до записи.";
                _serverConflict = true;
                return;
            }
            await Task.Run(writer);
            // Do not guess the new revision. Prevent queued writes until
            // the authoritative reload publishes the committed snapshot.
            _serverConflict = true;
            _saveAwaitingReadback = true;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = caption + ": запись не подтверждена — " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError ?? (caption + ": сверка с SQL Server…"), _saveError is not null);
                _reloadTimer.Start();
            }
        }
        if (Volatile.Read(ref _pendingWrites) == 0)
        {
            _reloadTimer.Stop();
            await ReloadDocumentAsync();
        }
    }

    // ---------------------------------------------------------------- Скопировать расценки (btn_transfer_rates)
    private void TransferRates_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is not { } snapshot || !_isKp || _doc.IsStage) return;
        var revision = _publishedEstimateRevision;
        if (!CanChangeHeader(snapshot, revision, false)) return;
        var dialog = new RateTransferDialog(_sqlConnectionString, _contractId, snapshot.Title,
            revision, () => CanChangeHeader(snapshot, revision, false),
            writer => ApplyRateTransferAsync(snapshot, revision, writer))
        {
            Owner = Window.GetWindow(this)
        };
        var accepted = dialog.ShowDialog() == true;
        if (!dialog.WriteAttempted) return;
        if (!accepted) RequireRateTransferReadback();
        else RequestRefresh();
    }

    // ---------------------------------------------------------------- Сохранить в Excel / PDF (btn_export)
    private void ExportExcelPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is not { } snapshot || Window.GetWindow(this) is not MainWindow main) return;
        var revision = _publishedEstimateRevision;
        var document = _doc;
        bool Current() => !_detached && IsLoaded && IsEnabled && !_serverConflict &&
            CanTransferDocument && ReferenceEquals(snapshot, _estimate) && ReferenceEquals(document, _doc) &&
            !string.IsNullOrWhiteSpace(revision) && revision == _serverRevision &&
            revision == _publishedEstimateRevision && main.IsCurrentEstimatePage(_contractId, _objectId, this);
        if (!Current()) return;
        var menu = new ContextMenu { PlacementTarget = sender as Button, Placement = PlacementMode.Bottom };
        void Item(string header, bool pdf, bool internalPrices = false, bool positional = false)
        {
            var item = new MenuItem { Header = header, IsEnabled = !positional || snapshot.SmrMode == "pos" };
            item.Click += (_, _) =>
            {
                if (!Current()) { SetStatus("Смета изменилась. Откройте меню экспорта снова.", true); return; }
                main.ExportKpDocument(document, internalPrices: internalPrices, positional: positional,
                    pdf: pdf, expectedRevision: revision);
            };
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
            main.CopyCurrentDocument(_doc, _publishedEstimateRevision);
    }

    private void MakeContract_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.MakeCurrentDocumentContract(_doc, _publishedEstimateRevision);
    }

    private void CreateDop_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.CreateDopAgreement(_doc, _publishedEstimateRevision);
    }

    private void MoveContract_Click(object sender,RoutedEventArgs e)
    {
        if(Window.GetWindow(this) is MainWindow main && _doc.Kind=="main")
            main.MoveCurrentContract(_doc, _publishedEstimateRevision);
    }

    private void DeleteContract_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.DeleteCurrentDocument(_doc, _publishedEstimateRevision);
    }

    private void ShowPeople_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.OpenEstimateObjectTool(this, _doc, _publishedEstimateRevision, orders: false);
    }

    private void ShowOrders_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.OpenEstimateObjectTool(this, _doc, _publishedEstimateRevision, orders: true);
    }

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
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
        if (Window.GetWindow(this) is MainWindow main)
            main.CreateGmcsLink(_doc, _publishedEstimateRevision);
    }

    private void CompareDocs_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main && _doc is not null)
            main.CompareCurrentDocument(_doc, _publishedEstimateRevision);
    }

    // ---------------------------------------------------------------- блокировка (toggle_lock)
    private async void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is not { } snapshot) return;
        var revision = _serverRevision;
        if (!CanChangeHeader(snapshot, revision, allowLocked: true)) return;
        var toLock = !snapshot.Locked;
        var answer = MessageBox.Show(Window.GetWindow(this),
            toLock ? "Заблокировать смету? Цены, количество и строки больше не поменяются случайно."
                : "Снять блокировку? Цены и количество снова можно менять.",
            toLock ? "Заблокировать — смета подписана" : "Снять блокировку",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await SaveHeaderMutationAsync(snapshot, revision, true,
            () => SqlServerWorkspace.SetContractLocked(_sqlConnectionString, _contractId, toLock, revision),
            "Блокировка сметы");
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
        SmrEdit.IsReadOnly = est.Locked || est.MoneyFromStages || (!pct && mode != "sum");
        SmrEdit.ToolTip = byPos
            ? "СМР по позициям: сумма работ по строкам — правится в колонке «Работа за ед.»"
            : bySys ? "СМР по системам: правится в строках систем"
            : pct ? $"Процент от материалов (Enter — сохранить): {NumberText.QtyText(est.SmrPercent)}% = {N2(est.Totals.Smr)} ₽"
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
        if (est.MoneyFromStages)
        {
            SmrEdit.ToolTip = "СМР определяется этапами и остатком";
            SmrHintText.Text = "сумма СМР этапов и остатка";
        }
        SmrHintText.Visibility = string.IsNullOrEmpty(SmrHintText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        SmrCaption.Text = est.MoneyFromStages ? "СМР — сумма этапов" : "Монтаж · СМР";
        TransportCaption.Text = est.MoneyFromStages ? "Транспорт — сумма этапов" : "Транспорт, ₽";
        TransportEdit.ToolTip = est.MoneyFromStages
            ? "Транспорт определяется этапами и остатком"
            : "Транспорт: сумма (Enter — сохранить)";
        SmrModeCombo.IsEnabled = !est.Locked && !est.MoneyFromStages;
        TransportEdit.IsReadOnly = est.Locked || est.MoneyFromStages;
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

    private void SmrEdit_Commit(object sender, RoutedEventArgs e)
    {
        if (_estimate?.SmrMode != "percent")
        {
            SaveTotalsFieldFromEdit(SmrEdit, "smr_amount", _estimate?.Statistics.Smr, "СМР");
            return;
        }
        if (SmrEdit.IsReadOnly) return;
        // The display rounds to three fractional digits. Merely leaving the field
        // must not overwrite a stored percentage with higher precision.
        if (SmrEdit.Text.Trim() == NumberText.QtyText(_estimate.SmrPercent)) return;
        SaveTotalsFieldFromEdit(SmrEdit, "smr_percent", _estimate.SmrPercent, "Процент СМР");
    }

    private void TransportEdit_Commit(object sender, RoutedEventArgs e) => SaveTotalsFieldFromEdit(
        TransportEdit, "transport_amount", _estimate?.Totals.Transport, "Транспорт");

    private void PnrEdit_Commit(object sender, RoutedEventArgs e) => SaveTotalsFieldFromEdit(
        PnrEdit, "pnr_amount", _estimate?.Totals.Pnr, "Пусконаладка");

    private async void SaveTotalsFieldFromEdit(TextBox box, string field, decimal? current, string caption)
    {
        if (_estimate is not { } snapshot || snapshot.Locked || box.IsReadOnly) return;
        var displayedRevision = _publishedEstimateRevision;
        var text = box.Text.Trim();
        decimal? value;
        if (text.Length == 0)
            value = field == "smr_percent" ? 0m : null;
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
        if (!CanChangeHeader(snapshot, displayedRevision, false))
        {
            SetStatus("Итоги не изменены: сначала дождитесь сверки сметы с сервером.", true);
            return;
        }
        await SaveTotalsFieldAsync(field, value, caption, displayedRevision);
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
            if (_serverConflict || _detached ||
                Window.GetWindow(this) is not MainWindow main ||
                !main.IsCurrentEstimatePage(_contractId, _objectId, this) ||
                !main.CanEditEstimateHeader(_contractId, _objectId))
            {
                _saveError ??= "Смета изменилась или недоступна. Сначала обнови её.";
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
        if (_smrComboSilent || _estimate is not { } snapshot) return;
        if (SmrModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        if (mode == snapshot.SmrMode) return;
        var revision = _publishedEstimateRevision;
        if (snapshot.MoneyFromStages || !CanChangeHeader(snapshot, revision, false))
        {
            SetStatus("Режим СМР недоступен: проверьте блокировку, этапы и сверку с сервером.", true);
            SetSmrControls(snapshot);
            return;
        }
        await SaveHeaderMutationAsync(snapshot, revision, false, () =>
        {
            var saved = SqlServerSmrMode.SetSmrMode(_sqlConnectionString, _contractId, mode, revision);
            if (!saved.Saved)
                throw new InvalidOperationException(saved.Error ?? "Режим СМР не изменён");
            return true;
        }, "Режим СМР");
    }
}
