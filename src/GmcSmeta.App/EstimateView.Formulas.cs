using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private sealed record EstimateFormulaPick(EditableEstimateRow Row, DataGridColumn Column, decimal Value);
    private TextBox? _formulaEditor;
    private EditableEstimateRow? _formulaRow;
    private DataGridColumn? _formulaColumn;
    private string? _formulaRevision;
    private readonly Dictionary<int, EstimateFormulaPick> _formulaPicks = new();
    private EstimateFormulaAdorner? _formulaAdorner;
    private AdornerLayer? _formulaLayer;
    private bool _formulaScrollRefreshQueued;

    private bool IsEstimateFormulaColumn(DataGridColumn column) =>
        Field(column) is "qty" or "price" or "purchase_price" or "work_price" ||
        _inlineStageColumns.Contains(column);

    private string SavedEstimateFormula(EditableEstimateRow row, DataGridColumn column)
    {
        var index = _inlineStageColumns.IndexOf(column);
        if (index >= 0)
            return _inlineStagesData is { } data && data.Rows.TryGetValue(row.Id, out var source)
                ? source.StageFormulas.ElementAtOrDefault(index) ?? "" : "";
        return Field(column) is { } field ? EstimateCellFormula.Read(row.Source.Formulas, field) : "";
    }

    private void BeginEstimateFormulaEdit(DataGridPreparingCellForEditEventArgs e)
    {
        EndEstimateFormulaEdit();
        if (e.Row.Item is not EditableEstimateRow row || row.IsVirtual ||
            !IsEstimateFormulaColumn(e.Column) || FindTextBox(e.EditingElement) is not { } editor) return;
        _formulaEditor = editor;
        _formulaRow = row;
        _formulaColumn = e.Column;
        _formulaRevision = _serverRevision;
        var saved = SavedEstimateFormula(row, e.Column);
        if (saved.Length != 0 && e.EditingEventArgs is not TextCompositionEventArgs)
        {
            editor.Text = saved;
            editor.SelectAll();
        }
        editor.ToolTip = "Начните с =, выбирайте числовые ячейки и вводите + − * /. Enter — сохранить, Esc — отменить.";
        editor.TextChanged += EstimateFormula_TextChanged;
        editor.Unloaded += EstimateFormula_Unloaded;
    }

    private void EstimateFormula_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "=" || !EstimateGrid.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is TextBox || _formulaEditor is not null ||
            EstimateGrid.CurrentCell.Column is not { } column || !IsEstimateFormulaColumn(column)) return;
        if (!EstimateGrid.BeginEdit(e) || _formulaEditor is not { } editor) return;
        editor.Text = "=";
        editor.CaretIndex = 1;
        editor.Focus();
        e.Handled = true;
    }

    private void EstimateFormula_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_formulaEditor is not { } editor || !editor.Text.TrimStart().StartsWith("=") ||
            e.OriginalSource is not DependencyObject hit ||
            VisualAncestor<DataGridCell>(hit) is not { } cell ||
            !cell.IsDescendantOf(EstimateGrid) ||
            cell.DataContext is not EditableEstimateRow row) return;
        if (ReferenceEquals(row, _formulaRow) && ReferenceEquals(cell.Column, _formulaColumn)) return;
        e.Handled = true;
        if (row.IsSystem || row.IsVirtual || ReadEstimateFormulaValue(row, cell.Column) is not decimal value)
        {
            SetStatus("Для формулы выберите числовую ячейку позиции.", true);
            return;
        }
        var existing = _formulaPicks.FirstOrDefault(p =>
            ReferenceEquals(p.Value.Row, row) && ReferenceEquals(p.Value.Column, cell.Column));
        var index = existing.Key;
        if (index == 0)
        {
            index = _formulaPicks.Count + 1;
            _formulaPicks[index] = new(row, cell.Column, value);
        }
        var token = $"[{index}]";
        editor.Focus();
        var start = editor.SelectionStart;
        var count = editor.SelectionLength;
        if (start == 0 && count > 0) { start = 1; count = Math.Max(0, count - 1); }
        editor.Select(start, count);
        editor.SelectedText = token;
        editor.CaretIndex = start + token.Length;
        editor.SelectionLength = 0;
    }

    private decimal? ReadEstimateFormulaValue(EditableEstimateRow row, DataGridColumn column)
    {
        var index = _inlineStageColumns.IndexOf(column);
        if (index >= 0)
        {
            if (_inlineStagesData is not { } data || !data.Rows.TryGetValue(row.Id, out var source)) return null;
            return index < source.PerStage.Length ? source.PerStage[index] : source.Rest;
        }
        return column.Header?.ToString() switch
        {
            "Кол-во" => row.Source.Qty,
            "Цена" => row.IsSub ? null : row.Source.Price,
            "Закупка" => row.Source.PurchasePrice,
            "Сумма" => row.IsSub ? null : row.Source.Amount,
            "Работа за ед." => row.Source.WorkPrice,
            "Работа всего" => row.DisplayWorkAmount,
            "м² за ед." => NumberText.Parse(row.AreaUnitDisplay),
            "м² всего" => NumberText.Parse(row.AreaTotalDisplay),
            _ => null
        };
    }

    private void EstimateFormula_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formulaEditor is not { } editor) return;
        IReadOnlyList<int> refs = editor.Text.TrimStart().StartsWith("=")
            ? EstimateCellFormula.References(editor.Text) : Array.Empty<int>();
        if (refs.Count == 0)
        {
            _formulaAdorner?.Update(Array.Empty<EstimateFormulaAdorner.Reference>());
            return;
        }
        if (_formulaAdorner is null)
        {
            _formulaLayer = AdornerLayer.GetAdornerLayer(EstimateGrid);
            if (_formulaLayer is null) return;
            _formulaAdorner = new EstimateFormulaAdorner(EstimateGrid);
            _formulaLayer.Add(_formulaAdorner);
        }
        _formulaAdorner.Update(refs.Where(_formulaPicks.ContainsKey).Select(i =>
        {
            var pick = _formulaPicks[i];
            return new EstimateFormulaAdorner.Reference(i, pick.Row, pick.Column);
        }).ToArray());
    }

    private void RefreshEstimateFormulaHighlightsAfterScroll()
    {
        if (_formulaAdorner is null || _formulaScrollRefreshQueued) return;
        _formulaScrollRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _formulaScrollRefreshQueued = false;
            _formulaAdorner?.InvalidateVisual();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool ResolveEstimateFormulaEdit(DataGridCellEditEndingEventArgs e)
    {
        if (_formulaEditor is not { } editor || !ReferenceEquals(e.Column, _formulaColumn)) return true;
        if (editor.Text.TrimStart().StartsWith("="))
        {
            var expanded = EstimateCellFormula.Expand(editor.Text,
                _formulaPicks.ToDictionary(p => p.Key, p => p.Value.Value));
            if (expanded is null || _formulaRevision != _serverRevision || _serverConflict ||
                System.Threading.Volatile.Read(ref _pendingWrites) != 0)
            {
                SetStatus("Проверьте формулу (до 40 символов после подстановки) и актуальность сметы.", true);
                return false;
            }
            if (_inlineStageColumns.Contains(e.Column) &&
                (NumberText.Parse(expanded) is not decimal qty || qty < 0 ||
                 NumberText.FormulaText(expanded) is not null &&
                 decimal.Round(qty, 6) <= StageAllocationPresentation.Epsilon))
            {
                SetStatus("Формула этапа должна давать положительное количество; для удаления введите 0.", true);
                return false;
            }
            // An explicit "=5" is still a formula for the user. Parentheses
            // keep it in the existing arithmetic-formula storage contract.
            editor.Text = NumberText.FormulaText(expanded) is null && NumberText.Parse(expanded) is decimal number &&
                !(_inlineStageColumns.Contains(e.Column) && number <= StageAllocationPresentation.Epsilon)
                ? "=(" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : expanded;
        }
        EndEstimateFormulaEdit();
        return true;
    }

    private void EstimateFormula_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _formulaEditor)) EndEstimateFormulaEdit();
    }

    private void EndEstimateFormulaEdit()
    {
        if (_formulaEditor is { } editor)
        {
            editor.TextChanged -= EstimateFormula_TextChanged;
            editor.Unloaded -= EstimateFormula_Unloaded;
        }
        _formulaEditor = null;
        _formulaRow = null;
        _formulaColumn = null;
        _formulaRevision = null;
        _formulaPicks.Clear();
        if (_formulaAdorner is not null) _formulaLayer?.Remove(_formulaAdorner);
        _formulaAdorner = null;
        _formulaLayer = null;
    }
}
