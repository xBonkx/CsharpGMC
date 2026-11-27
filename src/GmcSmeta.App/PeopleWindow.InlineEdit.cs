using System.Windows.Controls;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class PeopleView
{
    private bool _inlinePending;

    private void Inline_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_closed || _loading || _operationBusy || _inlinePending || string.IsNullOrWhiteSpace(_revision))
            e.Cancel = true;
    }

    private void Inline_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Column is not PeopleInlineTextColumn column ||
            e.EditingElement is not TextBox editor) return;
        if (_loading || _operationBusy || _inlinePending || string.IsNullOrWhiteSpace(_revision))
        {
            e.Cancel = true;
            return;
        }

        object original = e.Row.Item;
        object changed;
        try
        {
            if (editor.Text == PeopleInlineEdit.Read(original, column.Field)) return;
            changed = PeopleInlineEdit.Change(original, column.Field, editor.Text);
            if (Equals(original, changed)) return;
        }
        catch (ArgumentException ex)
        {
            e.Cancel = true;
            HintText.Text = ex.Message + " (Esc — отменить.)";
            return;
        }

        // Do not reenter DataGrid.CommitEdit from CellEditEnding. Block competing actions
        // until WPF finishes the current cell transaction and the existing SQL guard takes over.
        _inlinePending = true;
        var revision = _revision;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            _inlinePending = false;
            if (_closed || _loading || _operationBusy || revision != _revision) return;
            await RunAsync(async () =>
            {
                if (changed is PeoplePerson person)
                {
                    await MutateAsync(expected => _db.SavePerson(person, expected));
                    await ReloadMutationAsync(personId: person.Id);
                }
                else if (changed is PeopleCar car)
                {
                    await MutateAsync(expected => _db.SaveCar(car, expected));
                    await ReloadMutationAsync(carId: car.Id);
                }
                HintText.Text = "Изменения сохранены. Двойной щелчок или F2 — редактировать ячейку.";
            });
        }));
    }
}
