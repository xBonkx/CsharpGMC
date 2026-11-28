using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private void RefreshInlineStageCommandButtons()
    {
        if (InlineNewStageButton is null || InlineRestButton is null) return;
        var data = _inlineStagesData;
        InlineNewStageButton.IsEnabled = BulkStageAllowed;
        InlineRestButton.Visibility = data is { Rest: null } && data.Stages.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        InlineRestButton.IsEnabled = BulkStageAllowed;
    }

    private async void InlineNewStage_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineStagesData is { } snapshot)
            await RunInlineStageCommandAsync(snapshot, "Этап создан", () =>
                SqlServerStages.CreateStage(_sqlConnectionString, _contractId, snapshot.Revision));
    }

    private async void InlineCreateRest_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineStagesData is { Rest: null } snapshot && snapshot.Stages.Count > 0)
            await RunInlineStageCommandAsync(snapshot, "Документ «Остаток» создан", () =>
                SqlServerStages.CreateRest(_sqlConnectionString, _contractId, snapshot.Revision));
    }

    private bool CanRunInlineStageCommand(InlineStagesSnapshot snapshot) =>
        BulkStageAllowed && ReferenceEquals(snapshot, _inlineStagesData) &&
        !HasActiveEstimateEdit();

    private async Task InvokeInlineStageActionAsync(long id, string action, InlineStagesSnapshot snapshot)
    {
        if (!CanRunInlineStageCommand(snapshot)) return;
        var stage = snapshot.Stages.FirstOrDefault(s => s.Id == id) ??
            (snapshot.Rest?.Id == id ? snapshot.Rest : null);
        if (stage is null || stage.Locked || stage.StageSum is not null) return;
        var revision = snapshot.Revision;
        var owner = Window.GetWindow(this);
        try
        {
            switch (action)
            {
                case "rename" when !stage.Rest:
                    var rename = new PromptDialog("Название этапа", "Название", stage.Name) { Owner = owner };
                    if (rename.ShowDialog() != true || string.IsNullOrWhiteSpace(rename.Value)) return;
                    var name = rename.Value;
                    await RunInlineStageCommandAsync(snapshot, "Название этапа сохранено", () =>
                        SqlServerStages.RenameStage(_sqlConnectionString, _contractId, id, name, revision));
                    break;
                case "money":
                    var money = await Task.Run(() =>
                    {
                        using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                        if (SqlServerDatabase.Revision(conn) != revision)
                            throw new InvalidOperationException("Этапы изменились. Обновите смету.");
                        var result = SqlServerStages.ReadStageMoney(conn, id);
                        if (SqlServerDatabase.Revision(conn) != revision)
                            throw new InvalidOperationException("Этапы изменились при чтении.");
                        return result;
                    });
                    if (!CanRunInlineStageCommand(snapshot)) return;
                    var md = new StageMoneyDialog(money) { Owner = owner };
                    if (md.ShowDialog() != true) return;
                    var (smr, transport, target) = (md.Smr, md.Transport, md.ContractTarget);
                    await RunInlineStageCommandAsync(snapshot, "СМР, транспорт и сумма сохранены", () =>
                        SqlServerStages.SaveStageMoney(_sqlConnectionString, _contractId, id, smr, transport, target, revision));
                    break;
                case "systems":
                    var data = await Task.Run(() =>
                    {
                        using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                        if (SqlServerDatabase.Revision(conn) != revision)
                            throw new InvalidOperationException("Этапы изменились. Обновите смету.");
                        var systemMoney = SqlServerStages.ReadStageMoney(conn, id);
                        var systems = SqlServerStages.ReadStageSystems(conn, id);
                        if (SqlServerDatabase.Revision(conn) != revision)
                            throw new InvalidOperationException("Этапы изменились при чтении.");
                        return (Money: systemMoney, Systems: systems);
                    });
                    if (!CanRunInlineStageCommand(snapshot)) return;
                    if (!data.Money.CanEdit || !data.Money.SignedStageExists || data.Systems.Count == 0)
                    {
                        SetStatus("Для СМР по системам нужны свободный этап, распределённые позиции и подписанный этап КП.", true);
                        return;
                    }
                    var sd = new StageSystemsMoneyDialog(data.Money.Name, data.Systems) { Owner = owner };
                    if (sd.ShowDialog() != true) return;
                    var amounts = sd.Amounts;
                    await RunInlineStageCommandAsync(snapshot, "СМР систем сохранено", () =>
                        SqlServerStages.SaveStageSystems(_sqlConnectionString, _contractId, id, amounts, revision));
                    break;
                case "put" when !stage.Rest:
                    if (!ConfirmStageCommand("Перенести весь положительный остаток полного КП в этот этап?")) return;
                    await RunInlineStageCommandAsync(snapshot, "Остаток перенесён", () =>
                    {
                        var (result, _) = SqlServerStages.PutRest(_sqlConnectionString, _contractId, id, revision);
                        if (!result.Saved) throw new InvalidOperationException(result.Error);
                    });
                    break;
                case "clear" when !stage.Rest:
                    if (!ConfirmStageCommand("Убрать все строки из этапа? Само КП этапа останется пустым.")) return;
                    await RunInlineStageCommandAsync(snapshot, "Этап очищен", () =>
                    {
                        var result = SqlServerStages.ClearStage(_sqlConnectionString, _contractId, id, revision);
                        if (!result.Saved) throw new InvalidOperationException(result.Error);
                    });
                    break;
                case "delete":
                    if (!ConfirmStageCommand($"Удалить «{stage.Name}»? Копия сохранится в корзине (30 дней).")) return;
                    await RunInlineStageCommandAsync(snapshot, "Документ этапа удалён", () =>
                        SqlServerStages.DeleteStage(_sqlConnectionString, id, revision));
                    break;
            }
        }
        catch (Exception ex) { SetStatus("Этапы: " + ex.Message, true); }
    }

    private bool ConfirmStageCommand(string text) =>
        MessageBox.Show(Window.GetWindow(this), text, "Этапы", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task RunInlineStageCommandAsync(InlineStagesSnapshot snapshot, string success, Action write)
    {
        if (!CanRunInlineStageCommand(snapshot))
        {
            SetStatus("Распределение изменилось или редактирование не завершено. Повторите команду.", true);
            return;
        }
        _inlineStageCommandBusy = true;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        System.Threading.Interlocked.Increment(ref _pendingWrites);
        RefreshInlineStagesSelectionButtons();
        SetStatus("Этапы: сохранение…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || snapshot.Revision != _serverRevision ||
                !ReferenceEquals(snapshot, _inlineStagesData))
                throw new InvalidOperationException("Распределение изменилось до записи.");
            await Task.Run(write);
            // Treat structural changes as a fresh authoritative read, not as a
            // guessed revision or an undo operation on the full estimate.
            _saveAwaitingReadback = true;
            if (!_detached && Window.GetWindow(this) is MainWindow main)
                main.RefreshStageNavigation(this, _contractId);
            SetStatus(success + ", выполняется сверка…", false);
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Операция этапа не подтверждена: " + ex.Message;
            SetStatus(_saveError, true);
        }
        finally
        {
            _writeLock.Release();
            _inlineStageCommandBusy = false;
            _inlineStagesData = null; // Never allow another command on the old snapshot.
            if (System.Threading.Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
            RefreshInlineStagesSelectionButtons();
        }
    }
}
