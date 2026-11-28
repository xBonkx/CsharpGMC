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
    private string _name;
    private string _unit;
    private string _qty;
    private string _price;
    private string _purchase;
    private string _work;
    private string _note;
    private string _basis;
    // Cache the exact displayed/edited text, not the unrounded SQL decimal.
    // NumberText.Parse is invariant-culture and pure, including invalid input.
    private ParsedNumber _parsedQty, _parsedPrice, _parsedWork;

    private struct ParsedNumber
    {
        private string? _text;
        private decimal? _value;
        internal decimal? Get(string text)
        {
            if (!ReferenceEquals(_text, text))
            {
                _value = NumberText.Parse(text);
                _text = text;
            }
            return _value;
        }
    }
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
    private int? _stagePresentationVersion;

    // Source is immutable for the lifetime of this view model. Only edited
    // numeric strings need to be formatted again after an equal SQL readback.
    [Flags]
    private enum DirtyText { None = 0, Qty = 1, Price = 2, Purchase = 4, Work = 8 }
    private DirtyText _dirtyText;
    private bool _areaInputsDirty = true;
    private string _authoritativeAreaEdit = "";

    private string? _displayNum;
    private int? _displayIndentLevel;

    public EditableEstimateRow(EstimateRow row)
    {
        Source = row;
        _name = row.Name;
        _unit = row.Unit;
        _qty = row.IsSystem ? "" : EstimateVisualPolicy.Quantity(row.Qty);
        _price = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.Price);
        _purchase = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.PurchasePrice);
        _work = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.WorkPrice);
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
        if ((_dirtyText & DirtyText.Qty) != 0)
            QtyText = row.IsSystem ? "" : EstimateVisualPolicy.Quantity(row.Qty);
        if ((_dirtyText & DirtyText.Price) != 0)
            PriceText = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.Price);
        if ((_dirtyText & DirtyText.Purchase) != 0)
            PurchaseText = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.PurchasePrice);
        if ((_dirtyText & DirtyText.Work) != 0)
            WorkText = row.IsSystem || row.IsSub ? "" : EstimateVisualPolicy.Money(row.WorkPrice);
        _dirtyText = DirtyText.None;
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
    internal bool IsPendingInsert { get; init; }
    public bool IsVirtual => IsGroup || IsTypeHeader || IsPendingInsert;
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

    public string Name { get => _name; set => ChangeAreaInput(ref _name, value); }
    public string Unit { get => _unit; set => ChangeAreaInput(ref _unit, value); }
    public string QtyText
    {
        get => _qty;
        set
        {
            if (!ChangeNumeric(ref _qty, value, DirtyText.Qty)) return;
            OnPropertyChanged(nameof(DisplayAmount));
            OnPropertyChanged(nameof(DisplayAmountText));
        }
    }
    public string PriceText
    {
        get => _price;
        set
        {
            if (!ChangeNumeric(ref _price, value, DirtyText.Price)) return;
            OnPropertyChanged(nameof(DisplayAmount));
            OnPropertyChanged(nameof(DisplayAmountText));
            OnPropertyChanged(nameof(HasNoPrice));
            OnPropertyChanged(nameof(DisplayPriceText));
        }
    }
    public string PurchaseText { get => _purchase; set => ChangeNumeric(ref _purchase, value, DirtyText.Purchase); }
    public string WorkText
    {
        get => _work;
        set
        {
            if (ChangeNumeric(ref _work, value, DirtyText.Work))
            {
                OnPropertyChanged(nameof(WorkDisplayText));
                OnPropertyChanged(nameof(HasNoWorkPrice));
            }
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
        IsSystem || IsSub || IsVirtual || Source.WorkPrice is not decimal work || work == 0m ? null :
        Source.Qty * work;

    public void FillArea()
    {
        // Quantity, manual area and summary flag are immutable Source fields.
        // Name/Unit setters invalidate this presentation even for an optimistic edit.
        // An edited area text still has to be restored from the last computed value.
        if (!_areaInputsDirty)
        {
            AreaUnitEditText = _authoritativeAreaEdit;
            return;
        }

        var cell = IsSystem || IsVirtual
            ? new AreaCellPresentation.Cell("", "", "", "", "", false, false)
            : AreaCellPresentation.For(Name, Unit, Source.Qty, Source.AreaManual, Source.IsAreaSummary);
        // As before, publish all fields before raising any binding notification.
        var editChanged = _areaUnitEdit != cell.UnitEditor;
        var unitChanged = _areaUnitDisplay != cell.Unit;
        var totalChanged = _areaTotalDisplay != cell.Total;
        var tipChanged = _areaTip != cell.UnitTip;
        var totalTipChanged = _areaTotalTip != cell.TotalTip;
        var warningChanged = _areaWarning != cell.Warning;
        var manualChanged = _areaManualFlag != cell.Manual;
        _authoritativeAreaEdit = _areaUnitEdit = cell.UnitEditor;
        _areaUnitDisplay = cell.Unit;
        _areaTotalDisplay = cell.Total;
        _areaTip = cell.UnitTip;
        _areaTotalTip = cell.TotalTip;
        _areaWarning = cell.Warning;
        _areaManualFlag = cell.Manual;
        _areaInputsDirty = false;
        if (editChanged) OnPropertyChanged(nameof(AreaUnitEditText));
        if (unitChanged) OnPropertyChanged(nameof(AreaUnitDisplay));
        if (totalChanged) OnPropertyChanged(nameof(AreaTotalDisplay));
        if (tipChanged) OnPropertyChanged(nameof(AreaUnitTip));
        if (totalTipChanged) OnPropertyChanged(nameof(AreaTotalTip));
        if (warningChanged) OnPropertyChanged(nameof(AreaWarning));
        if (manualChanged) OnPropertyChanged(nameof(AreaManualFlag));
    }

    // Inline Python «Этапы» columns are computed from SQL Server stage
    // documents, never stored as new fields in the full KP.
    public IReadOnlyList<string> StageDisplay
    {
        get => _stageDisplay;
        set
        {
            _stagePresentationVersion = null;
            var next = value ?? Array.Empty<string>();
            if (ReferenceEquals(_stageDisplay, next) || _stageDisplay.SequenceEqual(next)) return;
            _stageDisplay = next;
            OnPropertyChanged();
        }
    }
    internal bool HasStagePresentation(int version) => _stagePresentationVersion == version;

    internal void PublishStagePresentation(int version, IReadOnlyList<string> labels, bool overrun)
    {
        StageDisplay = labels;
        StageOverrun = overrun;
        _stagePresentationVersion = version;
    }

    public bool StageOverrun
    {
        get => _stageOverrun;
        set
        {
            if (_stageOverrun == value) return;
            _stagePresentationVersion = null;
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
            var qty = _parsedQty.Get(_qty);
            var price = _parsedPrice.Get(_price);
            var amount = qty is not null && price is not null ? qty.Value * price.Value : Source.DisplayAmount;
            return amount == 0m ? null : amount;
        }
    }

    // Python kp_editor._fill_pos: money(p.price) if p.price else "—" — a missing price is a dash,
    // never "0,00"; the group label «разные» passes through untouched.
    public string DisplayPriceText => IsSystem || IsSub ? "" :
        string.IsNullOrWhiteSpace(_price) || _parsedPrice.Get(_price) == 0m ? "—" : _price;

    // Python: money(p.amount) — «0,00» is shown, only комплектующие stay empty.
    public string DisplayAmountText => IsSub ? "" : N2(IsGroup || IsSystem ? Source.Amount : OptimisticAmount());

    // Python: money(p.work_price) if p.work_price else "—" (СМР «по поз.»); subs stay empty.
    public bool HasNoWorkPrice => !IsSystem && !IsSub &&
        (IsGroup || (!IsVirtual && !OutOfKp)) &&
        (string.IsNullOrWhiteSpace(_work) || _parsedWork.Get(_work) == 0m);

    public string WorkDisplayText => IsSystem || IsSub || IsTypeHeader ? "" :
        string.IsNullOrWhiteSpace(_work) || _parsedWork.Get(_work) == 0m ? "—" : _work;

    private decimal OptimisticAmount()
    {
        var qty = _parsedQty.Get(_qty);
        var price = _parsedPrice.Get(_price);
        return qty is not null && price is not null ? qty.Value * price.Value : Source.Amount;
    }

    private static string N2(decimal v) => EstimateVisualPolicy.Money(v);

    public string DisplayWorkAmountText => EstimateVisualPolicy.Money(DisplayWorkAmount);

    public bool HasNoPrice => IsGroup ? GroupHasNoPrice :
        !IsSystem && !IsSub &&
        (string.IsNullOrWhiteSpace(_price) || _parsedPrice.Get(_price) == 0m);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool ChangeNumeric(ref string field, string? value, DirtyText flag,
        [CallerMemberName] string? property = null)
    {
        value ??= "";
        if (field == value) return false;
        _dirtyText |= flag;
        return Change(ref field, value, property);
    }

    private void ChangeAreaInput(ref string field, string? value,
        [CallerMemberName] string? property = null)
    {
        value ??= "";
        if (field == value) return;
        _areaInputsDirty = true;
        Change(ref field, value, property);
    }

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
