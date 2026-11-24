using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class StageMoneyDialog : Window
{
    private readonly SqlServerStages.StageMoneyInfo _stage;
    private bool _initializing = true;

    public decimal Smr { get; private set; }
    public decimal Transport { get; private set; }
    public decimal? ContractTarget { get; private set; }

    public StageMoneyDialog(SqlServerStages.StageMoneyInfo stage)
    {
        _stage = stage;
        InitializeComponent();
        StageTitle.Text = stage.Name;
        MaterialsText.Text = $"Материалы этапа: {stage.Materials:N2} ₽";
        SmrBox.Text = stage.Smr.ToString("0.##", CultureInfo.CurrentCulture);
        TransportBox.Text = stage.Transport.ToString("0.##", CultureInfo.CurrentCulture);
        TargetBox.Text = stage.ContractTarget?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _initializing = false;
        RefreshEditState();
    }

    private void TargetChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing) RefreshEditState();
    }

    private void RefreshEditState()
    {
        var permitted = _stage.CanEdit;
        var independent = _stage.Independent || !string.IsNullOrWhiteSpace(TargetBox.Text);
        SmrBox.IsEnabled = permitted && independent && _stage.Mode != "systems";
        TransportBox.IsEnabled = permitted && independent;
        TargetBox.IsEnabled = permitted;
        SaveButton.IsEnabled = permitted;
        NoteText.Text = !permitted
            ? "Этап подписан, заблокирован или является вычисляемым остатком. Денежные значения можно только просматривать."
            : _stage.Mode == "systems"
                ? "СМР рассчитывается по системам. Используйте меню этапа → «СМР по системам…»."
                : independent
                    ? "Сумма по договору фиксирует подписанный этап. После сохранения его объёмы и денежные условия защищены."
                    : "До подписания этапа СМР и транспорт берутся пропорционально из полного КП. Укажите сумму по договору, чтобы задать свои суммы.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_stage.CanEdit) return;
        if (!StageAllocationPresentation.TryParseNonnegative(SmrBox.Text, out var smr) ||
            !StageAllocationPresentation.TryParseNonnegative(TransportBox.Text, out var transport))
        {
            MessageBox.Show(this, "Укажите корректные неотрицательные суммы СМР и транспорта.",
                "Проверка сумм", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        decimal? target = null;
        if (!string.IsNullOrWhiteSpace(TargetBox.Text))
        {
            if (!StageAllocationPresentation.TryParseNonnegative(TargetBox.Text, out var parsed))
            {
                MessageBox.Show(this, "Сумма договора указана неверно.",
                    "Проверка сумм", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            target = parsed;
        }
        Smr = Money.Round2(smr);
        Transport = Money.Round2(transport);
        ContractTarget = target is null ? null : Money.Round2(target.Value);
        DialogResult = true;
    }
}
