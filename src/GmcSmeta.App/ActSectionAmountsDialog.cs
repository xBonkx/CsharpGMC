using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class ActSectionAmountsDialog : Window
{
    private readonly SqlServerActs.Schema31Sections _snapshot;
    private readonly bool _pos;
    private readonly decimal _actualSmr;
    private readonly List<Entry> _rows=new();
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
        HeadersVisibility=DataGridHeadersVisibility.Column,MinRowHeight=36,EnableRowVirtualization=true};
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12)};
    private readonly Button _save=new(){Content="Сохранить",MinWidth=110,MinHeight=34,IsDefault=true};
    public KsActAllocation.SectionAmounts? Result{get;private set;}

    public sealed class Entry
    {
        public long Id{get;init;}
        public string Name{get;init;}="";
        public string Plan{get;init;}="";
        public string Limit{get;init;}="";
        public string Smr{get;set;}="";
        public string Transport{get;set;}="";
    }

    public ActSectionAmountsDialog(SqlServerActs.Schema31Sections snapshot,IReadOnlyDictionary<long,string> labels,decimal actualSmr)
    {
        _snapshot=snapshot;_pos=snapshot.SmrMode=="pos";_actualSmr=actualSmr;
        Title="СМР и транспорт по разделам";Width=1180;Height=600;MinWidth=760;MinHeight=400;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};
        var top=new StackPanel{Margin=new Thickness(0,0,0,12)};
        top.Children.Add(new TextBlock{Text=$"Доступно в этом акте: СМР {Fmt(snapshot.Remaining.Smr)} ₽ · транспорт {Fmt(snapshot.Remaining.Transport)} ₽.",
            FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        top.Children.Add(new TextBlock{Text="Введите суммы вручную или рассчитайте пропорционально закрытым объёмам по ценам сметы. Объёмы не изменяются.",
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,8)});
        if(_pos)top.Children.Add(new TextBlock{Text="СМР по позициям вычисляются автоматически. Редактируется только транспорт.",TextWrapping=TextWrapping.Wrap});
        var auto=new Button{Content="Рассчитать СМР и транспорт по % выполнения",HorizontalAlignment=HorizontalAlignment.Left,MinHeight=34,Padding=new Thickness(12,4,12,4)};
        top.Children.Add(auto);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom=new StackPanel();
        bottom.Children.Add(_summary);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(new Button{Content="Отмена",MinWidth=100,MinHeight=34,IsCancel=true,Margin=new Thickness(0,0,8,0)});
        buttons.Children.Add(_save);bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        foreach(var s in snapshot.Systems)
            _rows.Add(new Entry{Id=s.Id,Name=labels.GetValueOrDefault(s.Id,"Раздел "+s.Id),Plan=Fmt(s.Smr),
                Limit=snapshot.Remaining.BySystem.TryGetValue(s.Id,out var limit)?Fmt(limit):"—"});
        _rows.Add(new Entry{Id=0,Name="Без привязки к разделу",Plan="—",Limit="—"});
        void Column(string header,string property,DataGridLength width,bool readOnly)
        {
            _grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(property)
                {UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,Mode=readOnly?BindingMode.OneWay:BindingMode.TwoWay},
                Width=width,MinWidth=readOnly?100:205,IsReadOnly=readOnly});
        }
        Column("Раздел",nameof(Entry.Name),new DataGridLength(1,DataGridLengthUnitType.Star),true);
        Column("СМР по смете",nameof(Entry.Plan),new DataGridLength(140),true);
        Column("СМР доступно",nameof(Entry.Limit),new DataGridLength(140),true);
        Column("СМР в этой КС",nameof(Entry.Smr),new DataGridLength(220),_pos);
        Column("Транспорт в этой КС",nameof(Entry.Transport),new DataGridLength(220),false);
        _grid.ItemsSource=_rows;root.Children.Add(_grid);Content=root;
        _grid.CellEditEnding+=(_,_)=>Dispatcher.BeginInvoke(new Action(Refresh));
        _save.Click+=(_,_)=>{if(Commit()&&Refresh())DialogResult=true;};
        auto.Click+=(_,_)=>{if(Commit())Fill(snapshot.Suggested);};
        Fill(snapshot.Current);
    }

    private static string Fmt(decimal v)=>v.ToString("N2",CultureInfo.GetCultureInfo("ru-RU"));
    private bool Commit()=>_grid.CommitEdit(DataGridEditingUnit.Cell,true)&&_grid.CommitEdit(DataGridEditingUnit.Row,true);
    private void Fill(KsActAllocation.SectionAmounts values)
    {
        foreach(var row in _rows)
        {
            if(!_pos||row.Smr.Length==0)row.Smr=Fmt(values.Smr.GetValueOrDefault(row.Id));
            row.Transport=Fmt(values.Transport.GetValueOrDefault(row.Id));
        }
        _grid.Items.Refresh();Refresh();
    }
    private bool Refresh()
    {
        try
        {
            decimal Parse(string text)
            {
                if(string.IsNullOrWhiteSpace(text))return 0m;
                var value=EstimateDiscountInput.Parse(text)??throw new ArgumentException("Проверьте денежные поля.");
                if(value!=Money.Round2(value))throw new ArgumentException("Суммы вводятся с точностью до копейки.");
                return value;
            }
            var smr=_pos?new Dictionary<long,decimal>():_rows.ToDictionary(r=>r.Id,r=>Parse(r.Smr));
            var tr=_rows.ToDictionary(r=>r.Id,r=>Parse(r.Transport));
            var checkedAmounts=KsSectionEditPolicy.Validate(new(smr,tr),_snapshot.Systems.Select(s=>s.Id).ToHashSet(),
                _pos,_snapshot.Remaining.Smr,_snapshot.Remaining.Transport,_snapshot.Remaining.BySystem);
            Result=checkedAmounts.Amounts;
            var works=_pos?_actualSmr:checkedAmounts.Smr;
            _summary.Text=$"В акте: СМР {Fmt(works)} ₽ · транспорт {Fmt(checkedAmounts.Transport)} ₽.\n"+
                $"Останется: СМР {Fmt(_snapshot.Remaining.Smr-works)} ₽ · транспорт {Fmt(_snapshot.Remaining.Transport-checkedAmounts.Transport)} ₽.";
            // Keep the button clickable even with bad input; saving revalidates committed cells.
            return true;
        }
        catch(ArgumentException ex){Result=null;_summary.Text=ex.Message;return false;}
    }
}
