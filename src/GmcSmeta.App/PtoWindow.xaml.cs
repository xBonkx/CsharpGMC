using GmcSmeta.Core.Db;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class PtoWindow : Window
{
    private readonly string _connection;
    private readonly long? _scopeObjectId;
    private readonly long? _scopeContractId;
    private readonly PtoSqlSource _source;
    private PtoArchive _archive=null!;
    private IReadOnlyList<PtoPackageListProjection.Row> _rows=Array.Empty<PtoPackageListProjection.Row>();
    private bool _initializing=true;
    private bool _initialReadStarted;
    private bool _initializingArchive=true;
    private CancellationTokenSource? _readCancellation;
    private bool _loading;
    private bool _closed;
    private bool _archiveOperation;

    public PtoWindow(string connection,long? objectId=null,long? contractId=null)
    {
        InitializeComponent();
        if(contractId.HasValue && !objectId.HasValue)throw new ArgumentException("Для договора требуется объект.");
        _connection=connection;_scopeObjectId=objectId;_scopeContractId=contractId;
        _source=new PtoSqlSource(connection);
        SetLoading(true);
        ArchiveLabel.Text="Загрузка исполнительной документации…";
        Loaded+=async (_,_)=>await InitializeAsync();
        // Archive initialization includes the existing Pilot-only schema check/creation.
        Closing+=(_,e)=>{if(_initializingArchive||_archiveOperation)e.Cancel=true;};
        Closed+=(_,_)=>
        {
            _closed=true;
            _readCancellation?.Cancel();
        };
    }

    private void SetLoading(bool value)
    {
        _loading=value;
        PtoContent.IsEnabled=!value;
    }

    private async Task InitializeAsync()
    {
        if(_closed || !_initializing || !_loading || _archive is not null)return;
        // Loaded normally fires once; detach the initialization state before dispatching.
        if(_initialReadStarted)return;
        _initialReadStarted=true;
        using var cancellation=new CancellationTokenSource();
        _readCancellation=cancellation;
        var token=cancellation.Token;
        try
        {
            var snapshot=await Task.Run(()=>
            {
                string? title=null;
                if(_scopeContractId is long cid)
                {
                    var doc=_source.Contracts(_scopeObjectId).FirstOrDefault(c=>c.Id==cid) ??
                        throw new InvalidOperationException("Договор не найден в выбранном объекте.");
                    title="АОСР · "+doc;
                }
                var archive=new PtoArchive(_connection,PtoUserSettings.Root);
                return (Archive:archive,Title:title);
            });
            _initializingArchive=false;
            if(_closed || token.IsCancellationRequested)return;
            var rows=await Task.Run(()=>ReadRows(snapshot.Archive,token),token);
            if(_closed || token.IsCancellationRequested)return;
            _archive=snapshot.Archive;
            if(snapshot.Title is not null)Title=snapshot.Title;
            ApplyRows(rows,null);
        }
        catch(Exception ex)
        {
            _initializingArchive=false;
            _initializing=false;
            if(!_closed && !token.IsCancellationRequested){ShowError(ex);Close();}
        }
        finally
        {
            _readCancellation=null;
            _initializingArchive=false;
            _initializing=false;
            if(!_closed)SetLoading(false);
        }
    }

    private void ShowError(Exception e)=>
        MessageBox.Show(this,e.Message,"ПТО / Исполнительная документация",
            MessageBoxButton.OK,MessageBoxImage.Warning);

    private void Try(Action action)
    {
        if(_closed || _loading || _initializing)return;
        try{action();}
        catch(Exception e){ShowError(e);}
    }

    private IReadOnlyList<PtoPackageListProjection.Row> ReadRows(PtoArchive archive,CancellationToken token=default)=>
        PtoPackageListProjection.Build(archive.Sql.Packages(_scopeObjectId,_scopeContractId,token),
            _scopeObjectId,_scopeContractId,token);

    private Guid? SelectedId()=>(Packages.SelectedItem as PtoPackageListProjection.Row)?.Id;

    private void ApplyRows(IReadOnlyList<PtoPackageListProjection.Row> rows,Guid? selected)
    {
        _rows=rows;
        ArchiveLabel.Text="Файловый архив: "+_archive.Root+
            " · карточки, сертификаты, версии: Microsoft SQL Server (только GmcSmetaPilot_*)";
        ApplyFilter(selected);
    }

    private void ApplyFilter(Guid? selected)
    {
        var q=Search.Text.Trim();
        var status=(StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var visible=PtoPackageListProjection.Filter(_rows,q,status);
        Packages.ItemsSource=visible;
        Packages.SelectedItem=visible.FirstOrDefault(r=>r.Id==selected);
    }

    private void InvalidateRows()
    {
        _rows=Array.Empty<PtoPackageListProjection.Row>();
        Packages.ItemsSource=_rows;
        ArchiveLabel.Text="Список не загружен. Нажмите «Обновить».";
    }

    // Existing mutation/child-dialog readback stays synchronous and authoritative.
    private void Refresh()
    {
        var selected=SelectedId();
        InvalidateRows();
        ApplyRows(ReadRows(_archive),selected);
    }

    private async Task RefreshAsync()
    {
        if(_closed || _loading || _initializing)return;
        var selected=SelectedId();
        var archive=_archive;
        InvalidateRows();
        SetLoading(true);
        ArchiveLabel.Text="Обновление списка комплектов…";
        using var cancellation=new CancellationTokenSource();
        _readCancellation=cancellation;
        var token=cancellation.Token;
        try
        {
            var rows=await Task.Run(()=>ReadRows(archive,token),token);
            if(_closed || token.IsCancellationRequested)return;
            ApplyRows(rows,selected);
        }
        catch(Exception ex)
        {
            if(!_closed && !token.IsCancellationRequested){InvalidateRows();ShowError(ex);}
        }
        finally
        {
            _readCancellation=null;
            if(!_closed)SetLoading(false);
        }
    }

    private void Filter_Changed(object sender,RoutedEventArgs e)
    {
        if(!_closed && !_loading && !_initializing && Packages is not null && Search is not null)
            ApplyFilter(SelectedId());
    }
    private bool Belongs(PtoPackage p)=>
        (!_scopeObjectId.HasValue || p.ObjectId==_scopeObjectId) &&
        (!_scopeContractId.HasValue || p.ContractId==_scopeContractId);
    private PtoPackage? Selected()
    {
        if(Packages.SelectedItem is not PtoPackageListProjection.Row row)return null;
        var current=_archive.Sql.Get(row.Id,_scopeObjectId);
        if(!Belongs(current)) {Refresh();return null;}
        return current;
    }
    private void Edit(PtoPackage p)
    {
        new PtoEditorWindow(_archive,p,_connection) { Owner=this }.ShowDialog();
        Refresh();
    }

    private void Create_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        var objects=_source.Objects().Where(o=>_scopeObjectId is null || o.Id==_scopeObjectId).ToArray();
        if(objects.Length==0)throw new InvalidOperationException("Для ПТО отсутствуют доступные объекты.");
        if(_scopeContractId is long cid)
        {
            var scopedObject=objects.Single();
            var scopedContract=_source.Contracts(scopedObject.Id).FirstOrDefault(c=>c.Id==cid) ??
                throw new InvalidOperationException("Договор больше не доступен в выбранном объекте.");
            CreateFor(scopedObject,scopedContract);
            return;
        }
        var picker=new Window
        {
            Owner=this,Title="Создать комплект ПТО",Width=610,Height=285,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize
        };
        var panel=new StackPanel{Margin=new Thickness(18)};
        panel.Children.Add(new TextBlock{Text="Объект:",Margin=new Thickness(0,0,0,4)});
        var objBox=new ComboBox{ItemsSource=objects,SelectedIndex=0,Margin=new Thickness(0,0,0,10),
            DisplayMemberPath="Name"};
        panel.Children.Add(objBox);
        panel.Children.Add(new TextBlock{Text="Источник объёмов (можно создать комплект без договора):",
            Margin=new Thickness(0,0,0,4)});
        var contractBox=new ComboBox { Margin=new Thickness(0,0,0,12) };
        panel.Children.Add(contractBox);
        void UpdateContracts()
        {
            if(objBox.SelectedItem is not PtoSqlSource.ObjectChoice choice)return;
            var items=new List<object>{"Без договора — самостоятельный комплект"};
            items.AddRange(_source.Contracts(choice.Id));
            contractBox.ItemsSource=items;contractBox.SelectedIndex=0;
        }
        objBox.SelectionChanged+=(_,_)=>UpdateContracts();
        UpdateContracts();
        var create=new Button {Content="Создать",Padding=new Thickness(16,7,16,7),
            HorizontalAlignment=HorizontalAlignment.Right};
        create.Click+=(_,_)=>picker.DialogResult=true;
        panel.Children.Add(create);
        picker.Content=panel;
        if(picker.ShowDialog()!=true || objBox.SelectedItem is not PtoSqlSource.ObjectChoice obj)return;
        var contract=contractBox.SelectedItem as PtoSqlSource.ContractChoice;
        CreateFor(obj,contract);
    });

    private void CreateFor(PtoSqlSource.ObjectChoice obj,PtoSqlSource.ContractChoice? contract)
    {
        if(_scopeObjectId.HasValue && obj.Id!=_scopeObjectId ||
            _scopeContractId.HasValue && contract?.Id!=_scopeContractId)
            throw new InvalidOperationException("Неверная привязка комплекта ПТО.");
        var p=_source.Snapshot(obj,contract,_archive.Sql.ObjectProfile(obj.Id));
        p.Fields["number"]=PtoRules.NextNumber(_archive.Sql.Packages(obj.Id),p);
        _archive.Sql.Save(p);
        Edit(p);
    }

    private void Edit_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        if(Selected() is { } selected)Edit(selected);
    });
    private void Packages_DoubleClick(object sender,MouseButtonEventArgs e)=>Edit_Click(sender,e);

    private void Copy_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        if(Selected() is { } p)Edit(_archive.Sql.Copy(p.Id));
    });
    private void Delete_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        if(Selected() is not { } p)return;
        if(MessageBox.Show(this,"Удалить АОСР № "+PtoRules.Text(p,"number")+
            "? Ранее опубликованные комплекты и исходные файлы остаются в архиве.",
            "Удаление",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        _archive.Sql.Delete(p);
        Refresh();
    });
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    private void Certificates_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        new PtoCertificatesWindow(_archive) {Owner=this}.ShowDialog();
        Refresh();
    });

    private void ObjectCard_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        var selected=Selected();
        var obj=selected is not null?_source.Objects().FirstOrDefault(x=>x.Id==selected.ObjectId):
            _source.Objects().FirstOrDefault(x=>_scopeObjectId is null || x.Id==_scopeObjectId);
        if(obj is null)return;
        var (current,revision)=_archive.Sql.ReadObjectProfile(obj.Id);
        var dialog=new Window
        {
            Owner=this,Title="Карточка объекта — заготовка ПТО · "+obj.Name,
            Width=750,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner
        };
        var panel=new DockPanel{Margin=new Thickness(12)};
        var note=new TextBlock { Text="Общие реквизиты объекта берутся из SQL Server только для чтения. "+
            "Здесь редактируются исключительно ПТО-заготовки новых актов, старые снимки не изменяются.\n"+
            "Объект: "+obj.Name+" · Адрес: "+obj.Address+" · Заказчик: "+obj.Customer,
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(note,Dock.Top);panel.Children.Add(note);
        var save=new Button{Content="Сохранить заготовку ПТО",Padding=new Thickness(12,7,12,7),
            HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
        DockPanel.SetDock(save,Dock.Bottom);panel.Children.Add(save);
        var fields=new Dictionary<string,TextBox>();
        var stack=new StackPanel();
        foreach(var key in new[]{"designer","designer_rep","contractor_rep","control_rep",
            "worker_rep","project","standards","next_works","copies","handed_by"})
        {
            stack.Children.Add(new TextBlock{Text=PtoRules.Fields[key],Margin=new Thickness(0,8,0,2)});
            var edit=new TextBox{Text=current.GetValueOrDefault(key,""),AcceptsReturn=true,
                TextWrapping=TextWrapping.Wrap,MinHeight=35,MaxHeight=90,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            stack.Children.Add(edit);fields[key]=edit;
        }
        panel.Children.Add(new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        save.Click+=(_,_)=>
        {
            try
            {
                foreach(var(key,value) in fields)current[key]=value.Text.Trim();
                _archive.Sql.SaveObjectProfile(obj.Id,current,revision);
                dialog.DialogResult=true;
            }
            catch(Exception ex)
            {
                save.IsEnabled=false;
                MessageBox.Show(dialog,ex.Message+"\nЗакройте карточку и проверьте сохранённые реквизиты.",
                    "Карточка ПТО",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        };
        dialog.Content=panel;dialog.ShowDialog();
    });

    private void Templates_Click(object sender,RoutedEventArgs e)=>Try(()=>
    {
        var dialog=new OpenFileDialog{Title="Шаблон АОСР или реестра",
            Filter="Excel (*.xlsx)|*.xlsx"};
        if(dialog.ShowDialog(this)!=true)return;
        var kind=MessageBox.Show(this,
            "Какой оригинальный шаблон загружаете?\nДа — АОСР\nНет — Реестр\nОтмена — не импортировать",
            "Пользовательский шаблон",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(kind==MessageBoxResult.Cancel)return;
        _archive.StoreTemplate(kind==MessageBoxResult.Yes?"aosr":"registry",dialog.FileName);
        MessageBox.Show(this,"Шаблон проверен и сохранён. Оригинал не изменён.","ПТО");
    });
    private async Task RunArchiveOperationAsync(Func<Task> action)
    {
        if(_closed||_loading||_initializing)return;
        _archiveOperation=true;
        SetLoading(true);
        try{await action();}
        catch(Exception ex){if(!_closed)ShowError(ex);}
        finally
        {
            _archiveOperation=false;
            if(!_closed)SetLoading(false);
        }
    }

    private async Task ReloadAfterArchiveChangeAsync()
    {
        var selected=SelectedId();
        var archive=_archive;
        InvalidateRows();
        var rows=await Task.Run(()=>ReadRows(archive));
        ApplyRows(rows,selected);
    }

    private async void Root_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        var folder=new OpenFolderDialog{Title="Архив файлов ПТО (метаданные остаются в SQL)"};
        if(folder.ShowDialog(this)!=true)return;
        var target=Path.GetFullPath(folder.FolderName);
        var archive=_archive;
        var replacement=await Task.Run(()=>
        {
            if(!string.Equals(target,archive.Root,StringComparison.OrdinalIgnoreCase))
            {
                var refs=SqlServerPtoBackup.Export(_connection);
                if(refs.Certificates.Count>0 || refs.Releases.Count>0 ||
                   archive.Sql.Packages().Any(p=>p.Attachments.Count>0))
                    throw new InvalidOperationException(
                        "Архив содержит документы, библиотеку сертификатов или опубликованные выпуски. "+
                        "Без переноса файлов смена корня нарушит ссылки. "+
                        "Используйте резервную копию и восстановление в пустой архив.");
            }
            var next=new PtoArchive(_connection,target);
            PtoUserSettings.Root=next.Root;
            return next;
        });
        _archive=replacement;
        await ReloadAfterArchiveChangeAsync();
    });
    private async void Backup_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        var dlg=new SaveFileDialog {Title="Резервная копия ПТО SQL + файлы",Filter="ZIP (*.zip)|*.zip",
            FileName="ПТО-резерв.zip"};
        if(dlg.ShowDialog(this)!=true)return;
        var archive=_archive;
        var path=dlg.FileName;
        await Task.Run(()=>PtoBackup.Create(archive,path,_connection));
        MessageBox.Show(this,"Копия создана вне архива ПТО.","ПТО");
    });
    private async void Restore_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        var source=new OpenFileDialog{Title="Безопасная SQL/ZIP копия ПТО",Filter="ZIP (*.zip)|*.zip"};
        if(source.ShowDialog(this)!=true)return;
        var path=source.FileName;
        await Task.Run(()=>PtoBackup.ValidateZip(path));
        var dest=new OpenFolderDialog{Title="Новая пустая папка восстановления"};
        if(dest.ShowDialog(this)!=true)return;
        var target=dest.FolderName;
        // Invalidate the old view before starting a restore that can change SQL.
        InvalidateRows();
        _archive=await Task.Run(()=>
        {
            PtoBackup.Restore(_connection,path,target);
            var restored=new PtoArchive(_connection,target);
            PtoUserSettings.Root=restored.Root;
            return restored;
        });
        await ReloadAfterArchiveChangeAsync();
        MessageBox.Show(this,"Восстановлено в пустой архив и пустые таблицы тестовой SQL Server.","ПТО");
    });
}

public static class PtoUserSettings
{
    private static readonly string PathName=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next","pto.settings.json");
    public static string? Root
    {
        get
        {
            var env=Environment.GetEnvironmentVariable("GMC_SMETA_PTO_ROOT");
            if(!string.IsNullOrWhiteSpace(env))return env;
            try
            {
                if(File.Exists(PathName))
                    return JsonDocument.Parse(File.ReadAllText(PathName))
                        .RootElement.GetProperty("root").GetString();
            }
            catch(Exception) { }
            return null;
        }
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName,JsonSerializer.Serialize(new{root=value}));
        }
    }
    private static readonly string ModePath=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next","pto-mode.json");
    public static bool Enabled
    {
        get
        {
            try {return File.Exists(ModePath)&&JsonDocument.Parse(File.ReadAllText(ModePath))
                .RootElement.GetProperty("enabled").GetBoolean();}
            catch(Exception){return false;}
        }
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModePath)!);
            File.WriteAllText(ModePath,JsonSerializer.Serialize(new{enabled=value}));
        }
    }
}
