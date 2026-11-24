using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    /// <summary>
    /// Audit a fresh read of the act, never the optimistic/stale WPF grid.
    /// Revision is checked before and after loading to reject torn snapshots.
    /// </summary>
    private async Task<(KsPreSignAudit.Report Report, string Revision)> ReadPreSignAuditAsync(
        long actId, int contractId)
    {
        return await Task.Run(() =>
        {
            using var conn = SqlServerDatabase.Open(_connectionString);
            var before = SqlServerDatabase.Revision(conn);
            var sheet = SqlServerActs.ActSheet(conn, actId, contractId);
            var after = SqlServerDatabase.Revision(conn);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Акт изменился во время проверки. Повторите проверку после обновления.");
            return (Report: KsPreSignAudit.Check(sheet.Positions), Revision: after);
        });
    }

    private void RevertSignedCheck()
    {
        var previous = _building;
        _building = true;
        try { SignedCheck.IsChecked = _signed; }
        finally { _building = previous; }
    }

    private async void PreSignCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_actId <= 0 || _contractId <= 0) return;
        var actId = _actId;
        try
        {
            var audit = await ReadPreSignAuditAsync(actId, _contractId);
            if (actId != _actId) return; // User navigated to another act meanwhile.
            MessageBox.Show(Window.GetWindow(this), audit.Report.Format(),
                "ГМЦ-Смета — проверка КС-2", MessageBoxButton.OK,
                audit.Report.HasWarnings ? MessageBoxImage.Warning : MessageBoxImage.Information);
            SetStatus(audit.Report.HasWarnings
                ? $"Проверка КС-2: {audit.Report.Issues.Count} замечаний. Акт не изменён."
                : "Проверка КС-2: замечаний не обнаружено. Акт не изменён.");
        }
        catch (Exception ex)
        {
            if (actId != _actId) return;
            SetStatus("Не удалось проверить КС-2: " + ex.Message, true);
            MessageBox.Show(Window.GetWindow(this),
                "Проверка КС-2 не завершена. Никаких изменений не выполнено.\n" + ex.Message,
                "ГМЦ-Смета — проверка КС-2", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
