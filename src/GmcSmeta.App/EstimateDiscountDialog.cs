using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class EstimateDiscountDialog : Window
{
    private readonly EstimateDiscountInput.State _state;
    private readonly ComboBox _mode=new(){MinHeight=34};
    private readonly TextBox _percent=new(){MinHeight=34,MinWidth=230,Padding=new Thickness(8,4,8,4),MaxLength=32};
    private readonly TextBox _target=new(){MinHeight=34,MinWidth=230,Padding=new Thickness(8,4,8,4),MaxLength=32};
    private readonly CheckBox _smr=new(){Content="Скидка и на монтаж (СМР)",IsChecked=true};
    private readonly CheckBox _whole=new(){Content="Округлять расценки до целых рублей"};
    private readonly TextBlock _description=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray};
    private readonly TextBlock _preview=new(){TextWrapping=TextWrapping.Wrap,LineHeight=24};
    private readonly Button _apply=new(){Content="Применить скидку",MinWidth=150,MinHeight=34,IsDefault=true,IsEnabled=false};
    private bool _busy;
    private bool _onTotal;
    public EstimateDiscountInput.Preview? Result {get;private set;}

    public EstimateDiscountDialog(EstimateDiscountInput.State state)
    {
        _state=state;
        _preview.SetResourceReference(TextBlock.FontSizeProperty, "UiMoneyInputSize");
        Title="Скидка на всю смету";Width=590;MinWidth=520;SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        var root=new StackPanel{Margin=new Thickness(22,18,22,18)};
        root.Children.Add(new TextBlock{Text=Title,FontSize=(double)FindResource("UiH2Size"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
        if(state.SupportsTotal)_mode.Items.Add("На итог — отдельной строкой");
        _mode.Items.Add("На расценки — изменить цены позиций");
        root.Children.Add(_mode);
        _description.Margin=new Thickness(0,10,0,12);root.Children.Add(_description);
        void Field(string caption,TextBox box)
        {
            var grid=new Grid{Margin=new Thickness(0,0,0,8)};
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(260)});
            grid.Children.Add(new TextBlock{Text=caption,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
            Grid.SetColumn(box,1);grid.Children.Add(box);root.Children.Add(grid);
        }
        Field("Скидка, %",_percent);Field("Или итог должен быть, ₽",_target);
        _smr.IsEnabled=!state.SmrIsPercent;
        _smr.Margin=new Thickness(0,2,0,8);_whole.Margin=new Thickness(0,0,0,10);
        root.Children.Add(_smr);root.Children.Add(_whole);
        var card=new Border{Background=new SolidColorBrush(Color.FromRgb(241,245,249)),
            CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Child=_preview,Margin=new Thickness(0,6,0,14)};
        root.Children.Add(card);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Отмена",IsCancel=true,MinWidth=100,MinHeight=34,Margin=new Thickness(0,0,8,0)};
        if(TryFindResource("PythonToolbarPrimary") is Style primary)_apply.Style=primary;
        if(TryFindResource("PythonToolbarButton") is Style standard)cancel.Style=standard;
        actions.Children.Add(cancel);actions.Children.Add(_apply);root.Children.Add(actions);
        Content=root;
        _mode.SelectionChanged+=(_,_)=>SwitchMode();
        _percent.TextChanged+=(_,_)=>Refresh(false);
        _target.TextChanged+=(_,_)=>Refresh(true);
        _smr.Checked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _smr.Unchecked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _whole.Checked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _whole.Unchecked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _apply.Click+=(_,_)=>{if(Result?.Changed==true)DialogResult=true;};
        _mode.SelectedIndex=0;
    }
    private static string Rate(decimal value)=>value.ToString("0.#########",CultureInfo.GetCultureInfo("ru-RU"));
    private static string MoneyText(decimal value)=>value.ToString("N2",CultureInfo.GetCultureInfo("ru-RU"));
    private void SwitchMode()
    {
        _onTotal=_state.SupportsTotal&&_mode.SelectedIndex==0;
        _smr.Visibility=_whole.Visibility=_onTotal?Visibility.Collapsed:Visibility.Visible;
        _description.Text=_onTotal?"Скидка отдельной строкой от итога с НДС. Расценки и количества не меняются. 0% снимает скидку.":
            _state.CurrentDiscount!=0m?"Сначала снимите итоговую скидку, чтобы не применить две скидки одновременно.":
            "Расценки изменятся. Итог предварительный: округление цен может дать разницу.";
        _busy=true;_percent.Text=_onTotal?Rate(_state.CurrentDiscount):"";_target.Clear();_busy=false;
        Refresh(false);
    }
    private void Refresh(bool fromTarget)
    {
        if(_busy)return;
        _busy=true;
        try
        {
            var smr=_smr.IsChecked==true;var whole=_whole.IsChecked==true;
            var p=fromTarget?EstimateDiscountInput.FromTarget(_state,_onTotal,EstimateDiscountInput.Parse(_target.Text),smr,whole):
                EstimateDiscountInput.FromPercent(_state,_onTotal,EstimateDiscountInput.Parse(_percent.Text),smr,whole);
            Result=p;
            if(fromTarget)_percent.Text=p is null?"":Rate(p.Percent);
            else _target.Text=p is null?"":MoneyText(p.Total);
            _apply.IsEnabled=p?.Changed==true;
            _apply.Content=p is {OnTotal:true,Percent:0m}&&_state.CurrentDiscount!=0m?"Снять скидку":"Применить скидку";
            var basis=_onTotal?_state.Gross:_state.Total;
            _preview.Text=p is null?$"Сейчас итого: {MoneyText(_state.Total)} ₽":
                $"Исходный итог: {MoneyText(basis)} ₽\n"+
                $"{(p.Adjustment<0?"Наценка":"Скидка")}: {MoneyText(Math.Abs(p.Adjustment))} ₽ ({Rate(Math.Abs(p.Percent))}%)\n"+
                $"Новый итог: {MoneyText(p.Total)} ₽";
        }
        finally{_busy=false;}
    }
}
