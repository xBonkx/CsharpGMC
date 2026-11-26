using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Строка окна КС-2 (ks_view ActPositionItem): раздел сметы или позиция
/// со строкой акта. Правится только «В этом акте»; строки акта зелёные,
/// колонка ввода подсвечена #DCE8FF, «сдаём больше, чем выдано» — оранжевым.</summary>
public sealed class KsRowVm : INotifyPropertyChanged
{
    private string _num = "";
    private string _name = "";
    private string _unit = "";
    private string _qtyByKp = "";
    private string _before = "";
    private string _now = "";
    private string _left = "";
    private string _issued = "";
    private string _price = "";
    private string _amount = "";
    private Brush? _referenceBrush;
    private Brush? _referenceFill;
    private Brush? _referenceSelectedFill;
    private bool _inAct;
    private bool _warn;
    private bool _overLeft;
    private decimal _nowValue;
    private string _nowRaw = "";
    private string _nowToolTip = "";
    private decimal _restRaw;
    private decimal _leftRaw;

    public bool IsSection { get; init; }
    public bool IsSub { get; init; }
    public long SystemId { get; init; }
    public long PositionId { get; init; }
    public bool OutOfKp { get; init; }

    public string Num { get => _num; set => SetField(ref _num, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Unit { get => _unit; set => SetField(ref _unit, value); }
    public string QtyByKp { get => _qtyByKp; set => SetField(ref _qtyByKp, value); }
    public string Before { get => _before; set => SetField(ref _before, value); }
    public string Now { get => _now; set => SetField(ref _now, value); }
    public string Left { get => _left; set => SetField(ref _left, value); }
    public string Issued { get => _issued; set => SetField(ref _issued, value); }
    public string Price { get => _price; set => SetField(ref _price, value); }
    public string Amount { get => _amount; set => SetField(ref _amount, value); }
    public bool InAct { get => _inAct; set { if (SetField(ref _inAct, value)) Raise(nameof(NowCellWeight)); } }
    public bool Warn { get => _warn; set { if (SetField(ref _warn, value)) Raise(nameof(NowCellFore)); } }
    public bool OverLeft { get => _overLeft; set { if (SetField(ref _overLeft, value)) Raise(nameof(LeftCellFore), nameof(LeftToolTip)); } }
    /// <summary>Объём в акте числом — для «Объём в акте» у выделенных строк.</summary>
    public decimal NowValue { get => _nowValue; set => SetField(ref _nowValue, value); }
    /// <summary>Формула в показе «К12+К15» (или пусто) — подставляется в редактор ячейки.</summary>
    public string NowRaw { get => _nowRaw; set => SetField(ref _nowRaw, value); }
    public string NowToolTip { get => _nowToolTip; set => SetField(ref _nowToolTip, value); }
    /// <summary>Остаток по смете до этого акта (для фильтра «Остатки»).</summary>
    public decimal RestRaw { get => _restRaw; set => SetField(ref _restRaw, value); }
    /// <summary>Остаток после акта — «Больше, чем по смете!» при минусе.</summary>
    public decimal LeftRaw { get => _leftRaw; set => SetField(ref _leftRaw, value); }

    public Brush? ReferenceBrush
    {
        get => _referenceBrush;
        set
        {
            if (ReferenceEquals(_referenceBrush,value)) return;
            _referenceBrush=value;
            Raise(nameof(ReferenceBrush),nameof(HasReference));
        }
    }
    public bool HasReference => ReferenceBrush is not null;

    /// <summary>Alpha-30 reference shade pre-composited over the S_NOW cell base.</summary>
    public Brush? ReferenceFillBrush
    {
        get => _referenceFill;
        set
        {
            if (ReferenceEquals(_referenceFill, value)) return;
            _referenceFill = value;
            Raise(nameof(ReferenceFillBrush));
        }
    }

    /// <summary>Same shade over the selection background, as Python paints over selection.</summary>
    public Brush? ReferenceSelectedFillBrush
    {
        get => _referenceSelectedFill;
        set
        {
            if (ReferenceEquals(_referenceSelectedFill, value)) return;
            _referenceSelectedFill = value;
            Raise(nameof(ReferenceSelectedFillBrush));
        }
    }

    public string RowBack { get; init; } = "#FFFFFF";
    public string NowCellBack { get; init; } = "#DCE8FF";
    public Brush NowCellFore => Warn ? WarnBrush : TextBrush;
    public FontWeight NowCellWeight => InAct && !IsSection ? FontWeights.Bold : FontWeights.Normal;
    public Brush LeftCellFore => OverLeft ? DangerBrush : TextBrush;
    public string LeftToolTip => OverLeft ? "Больше, чем по смете!" : "";
    public string NameToolTip { get; init; } = "";

    internal static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
    internal static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    internal static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Raise(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
