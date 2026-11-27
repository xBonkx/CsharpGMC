using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        AutoGenerateColumns=false,IsReadOnly=true,MinRowHeight=27,AlternationCount=0,SelectionMode=DataGridSelectionMode.Single,Background=System.Windows.Media.Brushes.White,
        CanUserAddRows=false,CanUserDeleteRows=false,SelectionUnit=DataGridSelectionUnit.FullRow
    };
    private IReadOnlyList<PtoCertificateSearch.Row> _rows=Array.Empty<PtoCertificateSearch.Row>();
    private readonly DispatcherTimer _filterTimer=new(){Interval=TimeSpan.FromMilliseconds(150)};
    private readonly WrapPanel _actions=new(){Margin=new Thickness(0,0,0,8)};
    private bool _busy,_closed,_ready;
    public PtoCertificate? SelectedCertificate {get;private set;}

    public PtoCertificatesWindow(PtoArchive archive,bool pick=false)
    {
        _archive=archive;_pick=pick;
        Title=pick?"Выберите подтверждающий документ":"ПТО · Библиотека сертификатов";
        Width=1050;Height=650;MinWidth=680;MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        SetResourceReference(FontFamilyProperty,"UiFontFamily");
        SetResourceReference(FontSizeProperty,"UiBodySize");
        _grid.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
        _grid.SetResourceReference(DataGrid.RowStyleProperty,"PythonTableRow");
        var panel=new DockPanel{Margin=new Thickness(12)};
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        header.Children.Add(_actions);
        void Button(string caption,Func<Task> run,string? icon=null)
        {
            var b=new Button{Content=caption,Padding=new Thickness(10,4,10,4),Margin=new Thickness(0,0,6,4)};
            b.SetResourceReference(FrameworkElement.StyleProperty, "PythonToolbarButton");
            if(icon is not null)b.SetResourceReference(ContentControl.ContentTemplateProperty, icon);
            b.Click+=async (_,_)=>await RunAsync(run);_actions.Children.Add(b);
        }
        Button("Добавить PDF…",New);
        Button("Изменить карточку",Edit);
        Button("Открыть PDF",Open);
        if(pick)
        {
            var choose=new Button{Content="Добавить выбранный в акт",Padding=new Thickness(10,4,10,4),Margin=new Thickness(0,0,6,4)};
            choose.SetResourceReference(FrameworkElement.StyleProperty,"PythonToolbarButton");
            choose.Click+=(_,_)=>{if(!_busy&&_ready)Choose();};
            _actions.Children.Add(choose);
        }
        Button("Обновить",RefreshAsync,"IconLabelRefresh");
        _search.Margin=new Thickness(0);
        _search.Height=32;
        _search.SetResourceReference(FrameworkElement.StyleProperty,"PythonSearch");
        _search.Background=System.Windows.Media.Brushes.White;
        _search.BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0,0xD7,0xE2));
        _search.ToolTip="Поиск по производителю, номеру, материалу, виду документа";
        _search.TextChanged+=(_,_)=>QueueFilter();
        _filterTimer.Tick+=FilterTimer_Tick;
        header.Children.Add(PtoFormUi.WithPlaceholder(_search,"Продукция, материал, производитель, номер"));
        foreach(var(title,path,width) in new[]{
            ("Продукция","Product",260d),("Тип","Kind",150d),("Производитель","Manufacturer",180d),
            ("Номер","Number",110d),("Выдан","Issued",85d),("До","Expires",85d),
            ("Проверено","Verified",80d)})
        {
            bool center = path is "Issued" or "Expires" or "Verified" or "Number";
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header=title, SortMemberPath=path, Binding=path switch
                {
                    "Verified"=>new System.Windows.Data.Binding(path){Converter=PtoFormUi.VerifiedText},
                    "Expires"=>PtoFormUi.ExpiryBinding(),
                    _=>new System.Windows.Data.Binding(path)
                },
                Width=path == "Product" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                MinWidth=path == "Product" ? 190 : 36,
                ElementStyle=(Style)Application.Current.FindResource(center ? "CompactCostCenter" : "CompactCostText"),
                HeaderStyle=(Style)Application.Current.FindResource(center ? "KsQuantityHeader" : "KsTextHeader"),
            });
        }
        TableColumnMenu.SetProtect(_grid, "Продукция|Номер|До|Проверено");
        TableColumnMenu.SetKey(_grid, "pto-certificates");
        _grid.MouseDoubleClick+=async (_,_)=>{if(_busy||!_ready)return;if(_pick)Choose();else await RunAsync(Edit);};
        ScrollViewer.SetIsDeferredScrollingEnabled(_grid,true);
        VirtualizingPanel.SetVirtualizationMode(_grid,VirtualizationMode.Recycling);
        panel.Children.Add(_grid);Content=panel;
        Loaded+=InitialLoad;
        Closing+=(_,e)=>{if(_busy)e.Cancel=true;};
        Closed+=(_,_)=>
        {
            _closed=true;
            _filterTimer.Stop();
            _filterTimer.Tick-=FilterTimer_Tick;
        };
    }

    private async void InitialLoad(object sender,RoutedEventArgs e)
    {
        Loaded-=InitialLoad;
        await RunAsync(RefreshAsync);
    }

    private async Task RunAsync(Func<Task> act)
    {
        if(_busy||_closed)return;
        if(_ready&&_filterTimer.IsEnabled)
        {
            _filterTimer.Stop();
            ApplyFilter(Selected()?.Id);
        }
        _busy=true;_actions.IsEnabled=_grid.IsEnabled=_search.IsEnabled=false;
        _filterTimer.Stop();
        try{await act();}
        catch(Exception e){if(!_closed)MessageBox.Show(this,e.Message,"Библиотека сертификатов",MessageBoxButton.OK,
            MessageBoxImage.Warning);}
        finally
        {
            _busy=false;
            if(!_closed){_actions.IsEnabled=true;_grid.IsEnabled=_search.IsEnabled=_ready;}
        }
    }

    private PtoCertificate? Selected()=>_grid.SelectedItem as PtoCertificate;
    private async Task RefreshAsync()
    {
        var selected=Selected()?.Id;
        _ready=false;
        _grid.ItemsSource=null;
        _rows=Array.Empty<PtoCertificateSearch.Row>();
        _rows=await Task.Run(()=>PtoCertificateSearch.Build(_archive.Sql.Certificates()));
        _ready=true;
        ApplyFilter(selected);
    }

    private void QueueFilter()
    {
        if(_busy||_closed||!_ready)return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void FilterTimer_Tick(object? sender,EventArgs e)
    {
        _filterTimer.Stop();
        if(!_busy&&!_closed&&_ready)ApplyFilter(Selected()?.Id);
    }

    private void ApplyFilter(Guid? selected)
    {
        var visible=PtoCertificateSearch.Filter(_rows,_search.Text);
        _grid.ItemsSource=visible;
        _grid.SelectedItem=visible.FirstOrDefault(c=>c.Id==selected);
    }
    private async Task New()
    {
        var picker=new OpenFileDialog{Title="Добавить сертификат / декларацию / паспорт PDF",
            Filter="PDF (*.pdf)|*.pdf"};
        if(picker.ShowDialog(this)!=true)return;
        var cert=new PtoCertificate();
        if(!EditDialog(cert))return;
        var path=picker.FileName;
        await Task.Run(()=>_archive.ImportCertificate(path,cert));
        await RefreshAsync();
    }
    private async Task Edit()
    {
        if(Selected() is not { } current)return;
        // detached copy, so rejecting the dialog never changes the list or SQL snapshots.
        var data=System.Text.Json.JsonSerializer.Deserialize<PtoCertificate>(
            System.Text.Json.JsonSerializer.Serialize(current,PtoRules.Json),PtoRules.Json)!;
        data.Revision=current.Revision;
        if(!EditDialog(data))return;
        await Task.Run(()=>_archive.Sql.SaveCertificate(data));
        await RefreshAsync();
    }
    private bool EditDialog(PtoCertificate c)
    {
        var dlg=new Window {Owner=this,Title="Реквизиты документа о качестве",Width=720,Height=540,MinWidth=550,MinHeight=430,
            WindowStartupLocation=WindowStartupLocation.CenterOwner};
        WorkspaceWindowUi.ApplyChrome(dlg);
        var panel=new DockPanel{Margin=new Thickness(14)};
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,
            Margin=new Thickness(0,10,0,0)};
        var save=new Button{Content="Сохранить реквизиты",Padding=new Thickness(14,6,14,6),Margin=new Thickness(0,0,8,0)};
        save.SetResourceReference(FrameworkElement.StyleProperty,"PythonToolbarButton");
        var cancel=new Button{Content="Отмена",IsCancel=true,Padding=new Thickness(14,6,14,6)};
        cancel.SetResourceReference(FrameworkElement.StyleProperty,"PythonToolbarButton");
        actions.Children.Add(save);actions.Children.Add(cancel);
        DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var stack=new StackPanel();
        scroll.Content=stack;panel.Children.Add(scroll);
        var types=new ComboBox{ItemsSource=PtoRules.QualityKinds,SelectedItem=c.Kind,MinHeight=32};
        PtoFormUi.AddRow(stack,"Тип документа",types,230);
        var fields=new Dictionary<string,TextBox>();
        foreach(var (key,label,value) in new[]{
            ("product","Продукция / оборудование",c.Product),
            ("manufacturer","Производитель",c.Manufacturer),("number","Номер",c.Number),
            ("issued","Дата выдачи ДД.ММ.ГГГГ",c.Issued),
            ("expires","Окончание действия ДД.ММ.ГГГГ",c.Expires),
            ("materials","Материалы / примечания для поиска",c.Materials)})
        {
            var edit=new TextBox{Text=value,Padding=new Thickness(8,5,8,5),
                TextWrapping=TextWrapping.Wrap,MinHeight=32};
            PtoFormUi.AddRow(stack,label,edit,230);fields[key]=edit;
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
    private async Task Open()
    {
        if(Selected() is not { } current)return;
        var file=await Task.Run(()=>
        {
            var path=_archive.FilePath(current.RelativePath);
            if(!File.Exists(path) || !PtoArchive.Sha(path).Equals(current.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Исходный PDF отсутствует или SHA-256 не совпадает.");
            return path;
        });
        if(!_closed)Process.Start(new ProcessStartInfo(file){UseShellExecute=true});
    }
    private void Choose()
    {
        if(!_ready||_busy||Selected() is not { } current)return;
        // A pending search must not let Enter/double click pick a stale visible row.
        if(_filterTimer.IsEnabled){_filterTimer.Stop();ApplyFilter(current.Id);}
        if(Selected() is not { } confirmed)return;
        SelectedCertificate=confirmed;
        DialogResult=true;
    }
}
