using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Editable presentation of a read-only EstimateRow. Changes appear in the grid immediately;
/// EstimateView persists each edited cell through PositionEditor.SaveCell on a worker thread.
/// Never use the in-memory values as confirmation that the database commit succeeded.
/// </summary>
public sealed class EditableEstimateRow : INotifyPropertyChanged
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private string _name;
    private string _unit;
    private string _qty;
    private string _price;
    private string _purchase;
    private string _work;
    private string _note;
    private string _basis;
    private string _areaUnitEdit = "";
    private string _areaUnitDisplay = "";
    private string _areaTotalDisplay = "";
    private string _areaTip = "";
    private string _areaTotalTip = "";
    private bool _areaWarning;
    private bool _areaManualFlag;
    private bool _isCollapsed;
    private IReadOnlyList<string> _stageDisplay = Array.Empty<string>();
    private bool _stageOverrun;

    private string? _displayNum;
    private int? _displayIndentLevel;

    public EditableEstimateRow(EstimateRow row)
    {
        Source = row;
        _name = row.Name;
        _unit = row.Unit;
        _qty = row.IsSystem ? "" : row.Qty.ToString("0.###", Ru);
        _price = row.IsSystem || row.IsSub ? "" : row.Price.ToString("N2", Ru);
        _purchase = row.PurchasePrice?.ToString("N2", Ru) ?? "";
        _work = row.IsSystem || row.IsSub ? "" : row.WorkPrice?.ToString("N2", Ru) ?? "";
        _note = row.Note;
        _basis = row.Basis;
    }

    // Reuse an unchanged authoritative row without retaining optimistic text
    // from a cancelled, rejected or no-op edit. Source equality includes all
    // structural fields, amounts and area metadata.
    internal bool TryReuseAuthoritative(EstimateRow row)
    {
        if (IsVirtual || Source != row) return false;
        Name = row.Name;
        Unit = row.Unit;
        QtyText = row.IsSystem ? "" : row.Qty.ToString("0.###", Ru);
        PriceText = row.IsSystem || row.IsSub ? "" : row.Price.ToString("N2", Ru);
        PurchaseText = row.PurchasePrice?.ToString("N2", Ru) ?? "";
        WorkText = row.IsSystem || row.IsSub ? "" : row.WorkPrice?.ToString("N2", Ru) ?? "";
        Note = row.Note;
        Basis = row.Basis;
        return true;
    }

    public EstimateRow Source { get; }
    public int Id => Source.Id;
    public int SortKey => Source.SortKey;
    public int? ParentSystemId => Source.ParentSystemId;
    public RowKind Kind => Source.Kind;
    // Virtual rows created only for Python "Одинаковые вместе" / "По типу".
    // Never pass their synthetic negative IDs to the SQL single-cell editor.
    public IReadOnlyList<long> GroupMemberIds { get; init; } = Array.Empty<long>();
    public string GroupKey { get; init; } = "";
    public string TypeKey { get; init; } = "";
    public bool IsTypeHeader { get; init; }
    public bool GroupHasNoPrice { get; init; }
    public bool IsGroup => GroupMemberIds.Count > 1;
    public bool IsVirtual => IsGroup || IsTypeHeader;
    public bool IsExpandable => Source.IsSystem || IsGroup || IsAreaSummary || IsTypeHeader;
    public bool IsSystem => Source.IsSystem;
    public bool IsSub => Source.IsSub;
    public bool OutOfKp => Source.OutOfKp;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            OnPropertyChanged();
        }
    }
    public int Level => Source.Level;
    public string Num => Source.Num;
    // View-only labels. "Одним списком" uses the owning system in the №
    // column, as Python kp_editor.pos_item(show_sys=True) does.
    public string DisplayNum
    {
        get => _displayNum ?? Source.Num;
        set
        {
            if (_displayNum == value) return;
            _displayNum = value;
            OnPropertyChanged();
        }
    }
    public int DisplayIndentLevel
    {
        get => _displayIndentLevel ?? Source.Level;
        set
        {
            if (_displayIndentLevel == value) return;
            _displayIndentLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameIndentLevel));
        }
    }

    // Pure presentation. Keep DisplayIndentLevel unchanged: stage ancestor
    // discovery and folding use the original structural depth.
    public int NameIndentLevel => IsSystem ? DisplayIndentLevel :
        Math.Max(IsSub ? 1 : 0, DisplayIndentLevel - 1);

    public string Name { get => _name; set => Change(ref _name, value); }
    public string Unit { get => _unit; set => Change(ref _unit, value); }
    public string QtyText
    {
        get => _qty;
        set
        {
            if (!Change(ref _qty, value)) return;
            OnPropertyChanged(nameof(DisplayAmount));
            OnPropertyChanged(nameof(DisplayAmountText));
        }
    }
    public string PriceText
    {
        get => _price;
        set
        {
            if (!Change(ref _price, value)) return;
            OnPropertyChanged(nameof(DisplayAmount));
            OnPropertyChanged(nameof(DisplayAmountText));
            OnPropertyChanged(nameof(HasNoPrice));
            OnPropertyChanged(nameof(DisplayPriceText));
        }
    }
    public string PurchaseText { get => _purchase; set => Change(ref _purchase, value); }
    public string WorkText
    {
        get => _work;
        set
        {
            if (Change(ref _work, value))
                OnPropertyChanged(nameof(WorkDisplayText));
        }
    }
    public string Note { get => _note; set => Change(ref _note, value); }
    public string Basis { get => _basis; set => Change(ref _basis, value); }
    public string AreaUnitEditText { get => _areaUnitEdit; set => Change(ref _areaUnitEdit, value); }
    public string AreaUnitDisplay => _areaUnitDisplay;
    public string AreaTotalDisplay => _areaTotalDisplay;
    public string AreaUnitTip => _areaTip;
    public string AreaTotalTip => _areaTotalTip;
    public bool AreaWarning => _areaWarning;
    public bool AreaManualFlag => _areaManualFlag;
    public bool IsAreaSummary => Source.IsAreaSummary;
    public decimal? DisplayWorkAmount =>
        IsSystem || IsSub || IsVirtual || Source.WorkPrice is not decimal work ? null :
        Source.Qty * work;

    public void FillArea()
    {
        if (IsSystem || IsVirtual)
        {
            _areaUnitEdit = _areaUnitDisplay = _areaTotalDisplay = _areaTip = _areaTotalTip = "";
            _areaWarning = _areaManualFlag = false;
        }
        else
        {
            var cell = AreaCellPresentation.For(Name, Unit, Source.Qty,
                Source.AreaManual, Source.IsAreaSummary);
            _areaUnitEdit = cell.UnitEditor;
            _areaUnitDisplay = cell.Unit;
            _areaTotalDisplay = cell.Total;
            _areaTip = cell.UnitTip;
            _areaTotalTip = cell.TotalTip;
            _areaWarning = cell.Warning;
            _areaManualFlag = cell.Manual;
        }
        OnPropertyChanged(nameof(AreaUnitEditText));
        OnPropertyChanged(nameof(AreaUnitDisplay));
        OnPropertyChanged(nameof(AreaTotalDisplay));
        OnPropertyChanged(nameof(AreaUnitTip));
        OnPropertyChanged(nameof(AreaTotalTip));
        OnPropertyChanged(nameof(AreaWarning));
        OnPropertyChanged(nameof(AreaManualFlag));
    }

    // Inline Python «Этапы» columns are computed from SQL Server stage
    // documents, never stored as new fields in the full KP.
    public IReadOnlyList<string> StageDisplay
    {
        get => _stageDisplay;
        set { _stageDisplay = value ?? Array.Empty<string>(); OnPropertyChanged(); }
    }
    public bool StageOverrun
    {
        get => _stageOverrun;
        set
        {
            if (_stageOverrun == value) return;
            _stageOverrun = value;
            OnPropertyChanged();
        }
    }

    public decimal? DisplayAmount
    {
        get
        {
            if (IsGroup || IsSystem || IsSub) return Source.DisplayAmount;
            // Optimistic total: use typed values when they are valid, otherwise retain the last committed amount.
            var qty = NumberText.Parse(_qty);
            var price = NumberText.Parse(_price);
            var amount = qty is not null && price is not null ? qty.Value * price.Value : Source.DisplayAmount;
            return amount == 0m ? null : amount;
        }
    }

    // Python kp_editor._fill_pos: money(p.price) if p.price else "—" — a missing price is a dash,
    // never "0,00"; the group label «разные» passes through untouched.
    public string DisplayPriceText => HasNoPrice ? "—" : _price;

    // Python: money(p.amount) — «0,00» is shown, only комплектующие stay empty.
    public string DisplayAmountText => IsSub ? "" : N2(IsGroup || IsSystem ? Source.Amount : OptimisticAmount());

    // Python: money(p.work_price) if p.work_price else "—" (СМР «по поз.»); subs stay empty.
    public string WorkDisplayText => _work.Length == 0 ? "" : NumberText.Parse(_work) == 0m ? "—" : _work;

    private decimal OptimisticAmount()
    {
        var qty = NumberText.Parse(_qty);
        var price = NumberText.Parse(_price);
        return qty is not null && price is not null ? qty.Value * price.Value : Source.Amount;
    }

    private static string N2(decimal v) => v.ToString("N2", Ru);

    public bool HasNoPrice => IsGroup ? GroupHasNoPrice :
        !IsSystem && !IsSub &&
        (string.IsNullOrWhiteSpace(_price) || NumberText.Parse(_price) == 0m);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Change(ref string field, string? value, [CallerMemberName] string? property = null)
    {
        value ??= "";
        if (field == value) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
