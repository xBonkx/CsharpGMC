using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class ActDiscountDialog : Window
{
    internal sealed record Input(string Mode,decimal? Value);
    private readonly ComboBox _mode=new();
    private readonly TextBox _percent=new(){HorizontalContentAlignment=HorizontalAlignment.Right};
    private readonly TextBox _amount=new(){HorizontalContentAlignment=HorizontalAlignment.Right};
    private readonly TextBlock _preview=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _save=new(){Content="Сохранить",IsDefault=true};
    private readonly decimal _gross,_contractRate;
    private readonly DiscountPolicy.ActDiscount _original;
    public Input? Result{get;private set;}
    public ActDiscountDialog(decimal gross,decimal payable,decimal contractRate,DiscountPolicy.ActDiscount original)
    {
        _gross=gross;_contractRate=contractRate;_original=original;
        Title="Корректировка акта КС-2";Width=560;MinWidth=340;SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.CanResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        DynamicFormLayout.Apply(this);
        DynamicFormLayout.Field(_mode);
        DynamicFormLayout.Field(_percent);
        DynamicFormLayout.Field(_amount);
        _preview.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogListGap");
        var panel=new StackPanel();
        var heading=WorkspaceWindowUi.Heading("До скидки с НДС: "+Fmt(gross)+" ₽");
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiValueSize");
        heading.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        panel.Children.Add(heading);
        foreach(var label in new[]{"Скидка по договору","Индивидуальная скидка, %","Установить итоговую сумму вручную"})_mode.Items.Add(label);
        panel.Children.Add(_mode);
        void Field(string title,TextBox input)
        {
            var caption=new TextBlock{Text=title,TextWrapping=TextWrapping.Wrap};
            caption.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormLabelFieldGap");
            input.SetResourceReference(FrameworkElement.MarginProperty,"LayoutFormStackedFieldGap");
            System.Windows.Automation.AutomationProperties.SetName(input,title);
            panel.Children.Add(caption);
            panel.Children.Add(input);
        }
        _percent.Text=original.Percent.ToString("0.#########",CultureInfo.GetCultureInfo("ru-RU"));
        _amount.Text=Fmt(payable);
        Field("Скидка, %",_percent);Field("Итог с НДС, ₽",_amount);
        panel.Children.Add(_preview);
        var buttons=new WrapPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Отмена",IsCancel=true};
        cancel.SetResourceReference(FrameworkElement.MarginProperty,"LayoutDialogActionGap");
        DynamicFormLayout.StyleActions(_save,cancel);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);Content=DynamicFormLayout.WithFooter(panel,buttons);
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
