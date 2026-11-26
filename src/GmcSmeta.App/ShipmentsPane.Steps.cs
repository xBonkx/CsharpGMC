using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ShipmentsPane
{
    // Python _printed: per shipment, in this view's session only, not an SQL flag.
    private readonly HashSet<long> _exportedShipments = new();

    private void MarkShipmentExported(long shipmentId)
    {
        _exportedShipments.Add(shipmentId);
        // An export can finish after navigating to a different shipment.
        if (IsLoaded && !_building && _revision.Length > 0 &&
            Current is { } current && current.Id == shipmentId && current.Id == _shipmentId)
            UpdateShipmentSteps(current);
    }

    private void UpdateShipmentSteps(SqlServerShipments.ShipmentInfo? current)
    {
        ShipmentStepsBar.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        if (current is null) return;

        var step = ShipmentStepPolicy.Current(current.Fixed, current.Lines,
            current.Recipient, _exportedShipments.Contains(current.Id));
        SetShipmentStep(ShipmentStepPositions, 0, step, "Отметьте, что выдаёте");
        SetShipmentStep(ShipmentStepRecipient, 1, step, "Укажите, кому");
        SetShipmentStep(ShipmentStepExport, 2, step, "Распечатайте");
        SetShipmentStep(ShipmentStepIssue, 3, step, "Выдайте на объект");
    }

    private static void SetShipmentStep(ContentControl control, int index, int current, string caption)
    {
        control.Tag = index < current ? "done" : index == current ? "now" : "";
        control.Content = (index < current ? "✓ " : $"{index + 1}  ") + caption;
    }
}
