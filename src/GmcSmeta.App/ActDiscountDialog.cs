using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class ActDiscountDialog : Window
{
    internal sealed record Input(string Mode,decimal? Value);
    private readonly ComboBox _mode=new(){MinHeight=34};
    private readonly TextBox _percent=new(){MinHeight=34,Padding=new Thickness(8,4,8,4)};
    private readonly TextBox _amount=new(){MinHeight=34,Padding=new Thickness(8,4,8,4)};
    private readonly TextBlock _preview=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,14)};
    private readonly Button _save=new(){Content="Сохранить",MinWidth=110,MinHeight=34,IsDefault=true};
    private readonly decimal _gross,_contractRate;
    private readonly DiscountPolicy.ActDiscount _original;
    public Input? Result{get;private set;}
    public ActDiscountDialog(decimal gross,decimal payable,decimal contractRate,DiscountPolicy.ActDiscount original)
    {
        _gross=gross;_contractRate=contractRate;_original=original;
        Title="Корректировка акта КС-2";Width=560;SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(22,18,22,18)};
        panel.Children.Add(new TextBlock{Text="До скидки с НДС: "+Fmt(gross)+" ₽",FontSize=(double)FindResource("UiValueSize"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        foreach(var label in new[]{"Скидка по договору","Индивидуальная скидка, %","Установить итоговую сумму вручную"})_mode.Items.Add(label);
        panel.Children.Add(_mode);
        void Field(string title,TextBox input)
        {
            panel.Children.Add(new TextBlock{Text=title,Margin=new Thickness(0,9,0,4)});
            panel.Children.Add(input);
        }
        _percent.Text=original.Percent.ToString("0.#########",CultureInfo.GetCultureInfo("ru-RU"));
        _amount.Text=Fmt(payable);
        Field("Скидка, %",_percent);Field("Итог с НДС, ₽",_amount);
        panel.Children.Add(_preview);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(new Button{Content="Отмена",MinWidth=100,MinHeight=34,IsCancel=true,Margin=new Thickness(0,0,8,0)});
        buttons.Children.Add(_save);panel.Children.Add(buttons);Content=panel;
        _mode.SelectionChanged+=(_,_)=>Refresh();
        _percent.TextChanged+=(_,_)=>Refresh();
        _amount.TextChanged+=(_,_)=>Refresh();
        _save.Click+=(_,_)=>{if(Result is not null&&_save.IsEnabled)DialogResult=true;};
        _mode.SelectedIndex=original.ManualTotalCents>=0?2:original.Override?1:0;
    }
    private static string Fmt(decimal amount)=>amount.ToString("N2",CultureInfo.GetCultureInfo("ru-RU"));
    private void Refresh()
    {
        var mode=_mode.SelectedIndex switch{1=>"percent",2=>"total",_=>"contract"};
        _percent.IsEnabled=mode=="percent";_amount.IsEnabled=mode=="total";
        decimal? value=mode=="percent"?EstimateDiscountInput.Parse(_percent.Text):
            mode=="total"?EstimateDiscountInput.Parse(_amount.Text):null;
        try
        {
            var normalized=DiscountPolicy.Normalize(mode,value,_contractRate);
            var payable=DiscountPolicy.Payable(_gross,normalized.Percent,normalized.ManualTotalCents);
            Result=new(mode,value);
            _save.IsEnabled=normalized!=_original;
            _preview.Text=$"Итого с НДС: {Fmt(payable)} ₽\nКорректировка: {Fmt(payable-_gross)} ₽"+
                (mode=="total"?"\nРучной итог фиксирован; изменение объёмов меняет корректировку.":"");
        }
        catch(ArgumentException ex){Result=null;_save.IsEnabled=false;_preview.Text=ex.Message;}
    }
}
