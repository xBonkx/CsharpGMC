using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class EstimateDiscountDialog : Window
{
    private readonly EstimateDiscountInput.State _state;
    private readonly ComboBox _mode=new(){TabIndex=0};
    private readonly TextBox _percent=new(){TabIndex=1,HorizontalContentAlignment=HorizontalAlignment.Right,MaxLength=32};
    private readonly TextBox _target=new(){TabIndex=2,HorizontalContentAlignment=HorizontalAlignment.Right,MaxLength=32};
    private readonly CheckBox _smr=new(){TabIndex=3,Content="Скидка и на монтаж (СМР)",IsChecked=true};
    private readonly CheckBox _whole=new(){TabIndex=4,Content="Округлять расценки до целых рублей"};
    private readonly TextBlock _description=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _preview=new(){TextWrapping=TextWrapping.Wrap,LineStackingStrategy=LineStackingStrategy.MaxHeight};
    private readonly Button _apply=new(){Content="Применить скидку",IsEnabled=false,TabIndex=5};
    private bool _busy;
    private bool _onTotal;
    public EstimateDiscountInput.Preview? Result {get;private set;}

    public EstimateDiscountDialog(EstimateDiscountInput.State state)
    {
        _state=state;
        _preview.SetResourceReference(TextBlock.FontSizeProperty, "UiMoneyInputSize");
        Title="Скидка на всю смету";Width=590;MinWidth=360;SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.CanResize;
        DynamicFormLayout.Apply(this);
        DynamicFormLayout.Field(_mode);
        DynamicFormLayout.Field(_percent);
        DynamicFormLayout.Field(_target);
        _description.SetResourceReference(FrameworkElement.StyleProperty, "UiSecondaryText");
        DynamicFormLayout.WrapCaption(_smr);
        DynamicFormLayout.WrapCaption(_whole);
        var root=new StackPanel();
        var heading=WorkspaceWindowUi.Heading(Title);
        heading.SetResourceReference(FrameworkElement.MarginProperty,"LayoutDialogIntroGap");
        root.Children.Add(heading);
        if(state.SupportsTotal)_mode.Items.Add("На итог — отдельной строкой");
        _mode.Items.Add("На расценки — изменить цены позиций");
        root.Children.Add(_mode);
        _description.SetResourceReference(FrameworkElement.MarginProperty,"LayoutDialogListGap");root.Children.Add(_description);
        void Field(string caption,TextBox box)
        {
            var grid=new Grid();
            grid.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormFieldMargin");
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            var label=new TextBlock{Text=caption,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};
            label.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormColumnGap");
            System.Windows.Automation.AutomationProperties.SetName(box,caption);
            grid.Children.Add(label);
            Grid.SetColumn(box,1);grid.Children.Add(box);root.Children.Add(grid);
        }
        Field("Скидка, %",_percent);Field("Или итог должен быть, ₽",_target);
        _smr.IsEnabled=!state.SmrIsPercent;
        _smr.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormFieldMargin");
        _whole.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormFieldMargin");
        root.Children.Add(_smr);root.Children.Add(_whole);
        var card=new Border{Background=new SolidColorBrush(Color.FromRgb(241,245,249)),
            CornerRadius=new CornerRadius(8),Child=_preview};
        card.SetResourceReference(Border.PaddingProperty,"LayoutFormScrollInset");
        card.SetResourceReference(FrameworkElement.MarginProperty,"LayoutDialogListGap");
        root.Children.Add(card);
        var actions=new WrapPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Отмена",IsCancel=true,TabIndex=6};
        cancel.SetResourceReference(FrameworkElement.MarginProperty,"LayoutDialogActionGap");
        DynamicFormLayout.StyleActions(_apply,cancel);
        actions.Children.Add(cancel);actions.Children.Add(_apply);
        Content=DynamicFormLayout.WithFooter(root,actions);
        _mode.SelectionChanged+=(_,_)=>SwitchMode();
        _percent.TextChanged+=(_,_)=>Refresh(false);
        _target.TextChanged+=(_,_)=>Refresh(true);
        _smr.Checked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _smr.Unchecked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _whole.Checked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _whole.Unchecked+=(_,_)=>Refresh(_target.IsKeyboardFocused);
        _apply.Click+=(_,_)=>{if(Result?.Changed==true)DialogResult=true;};
        _mode.SelectedIndex=0;
        Loaded+=(_,_)=>_mode.Focus();
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
