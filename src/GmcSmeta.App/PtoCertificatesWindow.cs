using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Reusable certificate library with explicit manual verification, no automatic "verified" flag.</summary>
public sealed class PtoCertificatesWindow : Window
{
    private readonly PtoArchive _archive;
    private readonly bool _pick;
    private readonly TextBox _search=new(){Margin=new Thickness(0,0,0,8),Padding=new Thickness(8,5,8,5)};
    private readonly DataGrid _grid=new()
    {
        AutoGenerateColumns=false,IsReadOnly=true,MinRowHeight=24,Background=System.Windows.Media.Brushes.White,
        CanUserAddRows=false,CanUserDeleteRows=false,SelectionUnit=DataGridSelectionUnit.FullRow
    };
    private List<PtoCertificate> _visible=new();
    public PtoCertificate? SelectedCertificate {get;private set;}

    public PtoCertificatesWindow(PtoArchive archive,bool pick=false)
    {
        _archive=archive;_pick=pick;
        Title=pick?"Выберите подтверждающий документ":"ПТО · Библиотека сертификатов";
        Width=1040;Height=640;MinWidth=680;MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        FontFamily=new System.Windows.Media.FontFamily("Segoe UI");
        _grid.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
        var panel=new DockPanel{Margin=new Thickness(12)};
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        var bar=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        header.Children.Add(bar);
        void Button(string caption,Action run,string icon)
        {
            var b=new Button{Content=caption,Padding=new Thickness(10,4,10,4),Margin=new Thickness(0,0,6,4)};
            b.SetResourceReference(FrameworkElement.StyleProperty, "PythonToolbarButton");
            b.SetResourceReference(ContentControl.ContentTemplateProperty, icon);
            b.Click+=(_,_)=>Run(run);bar.Children.Add(b);
        }
        Button("Импорт PDF",New,"IconLabelUpload");
        Button("Реквизиты / проверить",Edit,"IconLabelEdit");
        Button("Открыть PDF",Open,"IconLabelFile");
        if(pick)Button("Добавить в акт",Choose,"IconLabelPlus");
        Button("Обновить",Refresh,"IconLabelRefresh");
        _search.ToolTip="Поиск по производителю, номеру, материалу, виду документа";
        _search.TextChanged+=(_,_)=>Refresh();
        header.Children.Add(_search);
        foreach(var(title,path,width) in new[]{
            ("Тип","Kind",150d),("Продукция","Product",260d),("Производитель","Manufacturer",180d),
            ("Номер","Number",110d),("Выдан","Issued",85d),("До","Expires",85d),
            ("Проверено","Verified",80d)})
        {
            bool center = path is "Issued" or "Expires" or "Verified" or "Number";
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header=title, Binding=new System.Windows.Data.Binding(path),
                Width=path == "Product" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                MinWidth=path == "Product" ? 190 : 36,
                ElementStyle=(Style)Application.Current.FindResource(center ? "CompactCostCenter" : "CompactCostText"),
                HeaderStyle=(Style)Application.Current.FindResource(center ? "KsQuantityHeader" : "KsTextHeader"),
            });
        }
        TableColumnMenu.SetProtect(_grid, "Продукция|Номер|До|Проверено");
        TableColumnMenu.SetKey(_grid, "pto-certificates");
        _grid.MouseDoubleClick+=(_,_)=>{if(_pick)Choose();else Edit();};
        panel.Children.Add(_grid);Content=panel;Refresh();
    }
    private void Run(Action act)
    {
        try{act();}
        catch(Exception e){MessageBox.Show(this,e.Message,"Библиотека сертификатов",MessageBoxButton.OK,
            MessageBoxImage.Warning);}
    }
    private PtoCertificate? Selected()=>_grid.SelectedItem as PtoCertificate;
    private void Refresh()
    {
        _visible=_archive.Sql.Certificates(_search.Text.Trim()).ToList();
        _grid.ItemsSource=_visible;
    }
    private void New()
    {
        var picker=new OpenFileDialog{Title="Добавить сертификат / декларацию / паспорт PDF",
            Filter="PDF (*.pdf)|*.pdf"};
        if(picker.ShowDialog(this)!=true)return;
        var cert=new PtoCertificate();
        if(!EditDialog(cert))return;
        _archive.ImportCertificate(picker.FileName,cert);
        Refresh();
    }
    private void Edit()
    {
        if(Selected() is not { } current)return;
        // detached copy, so rejecting the dialog never changes the list or SQL snapshots.
        var data=System.Text.Json.JsonSerializer.Deserialize<PtoCertificate>(
            System.Text.Json.JsonSerializer.Serialize(current,PtoRules.Json),PtoRules.Json)!;
        data.Revision=current.Revision;
        if(!EditDialog(data))return;
        _archive.Sql.SaveCertificate(data);
        Refresh();
    }
    private bool EditDialog(PtoCertificate c)
    {
        var dlg=new Window {Owner=this,Title="Реквизиты документа о качестве",Width=720,Height=640,
            WindowStartupLocation=WindowStartupLocation.CenterOwner};
        WorkspaceWindowUi.ApplyChrome(dlg);
        var panel=new DockPanel{Margin=new Thickness(14)};
        var save=new Button{Content="Сохранить реквизиты",Padding=new Thickness(14,7,14,7),
            HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        DockPanel.SetDock(save,Dock.Bottom);panel.Children.Add(save);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var stack=new StackPanel();
        scroll.Content=stack;panel.Children.Add(scroll);
        var types=new ComboBox{ItemsSource=PtoRules.QualityKinds,SelectedItem=c.Kind};
        stack.Children.Add(new TextBlock{Text="Тип документа",Margin=new Thickness(0,7,0,2)});
        stack.Children.Add(types);
        var fields=new Dictionary<string,TextBox>();
        foreach(var (key,label,value) in new[]{
            ("product","Продукция / оборудование",c.Product),
            ("manufacturer","Производитель",c.Manufacturer),("number","Номер",c.Number),
            ("issued","Дата выдачи ДД.ММ.ГГГГ",c.Issued),
            ("expires","Окончание действия ДД.ММ.ГГГГ",c.Expires),
            ("materials","Материалы / примечания для поиска",c.Materials)})
        {
            stack.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,9,0,2)});
            var edit=new TextBox{Text=value,Padding=new Thickness(5),
                TextWrapping=TextWrapping.Wrap,MinHeight=30};
            stack.Children.Add(edit);fields[key]=edit;
        }
        var noExpiry=new CheckBox{Content="Срок действия отсутствует (проверено вручную)",
            IsChecked=c.NoExpiry,Margin=new Thickness(0,12,0,4)};
        var checkedBox=new CheckBox{Content="Реквизиты документа проверены мной вручную",
            IsChecked=c.Verified,Margin=new Thickness(0,0,0,5)};
        stack.Children.Add(noExpiry);stack.Children.Add(checkedBox);
        foreach(var edit in fields.Values)edit.TextChanged+=(_,_)=>checkedBox.IsChecked=false;
        noExpiry.Checked+=(_,_)=>checkedBox.IsChecked=false;
        noExpiry.Unchecked+=(_,_)=>checkedBox.IsChecked=false;
        types.SelectionChanged+=(_,_)=>checkedBox.IsChecked=false;
        save.Click+=(_,_)=>
        {
            try
            {
                c.Kind=types.SelectedItem?.ToString()??"Сертификат";
                c.Product=fields["product"].Text.Trim();c.Manufacturer=fields["manufacturer"].Text.Trim();
                c.Number=fields["number"].Text.Trim();c.Issued=fields["issued"].Text.Trim();
                c.Expires=fields["expires"].Text.Trim();c.Materials=fields["materials"].Text.Trim();
                c.NoExpiry=noExpiry.IsChecked==true;c.Verified=checkedBox.IsChecked==true;
                var issued=PtoRules.Date(c.Issued);
                var expires=PtoRules.Date(c.Expires);
                if(expires.HasValue && issued.HasValue && expires<issued)
                    throw new ArgumentException("Дата окончания раньше даты выдачи.");
                dlg.DialogResult=true;
            }
            catch(Exception ex){MessageBox.Show(dlg,ex.Message,"Проверка дат");}
        };
        dlg.Content=panel;
        return dlg.ShowDialog()==true;
    }
    private void Open()
    {
        if(Selected() is not { } current)return;
        var file=_archive.FilePath(current.RelativePath);
        if(!File.Exists(file) || !PtoArchive.Sha(file).Equals(current.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Исходный PDF отсутствует или SHA-256 не совпадает.");
        Process.Start(new ProcessStartInfo(file){UseShellExecute=true});
    }
    private void Choose()
    {
        if(Selected() is not { } current)return;
        SelectedCertificate=current;
        DialogResult=true;
    }
}
