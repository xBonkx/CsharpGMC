using GmcSmeta.Core.Db;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _filterTimer=new(){Interval=TimeSpan.FromMilliseconds(150)};
    private (string Query,string? Status)? _publishedFilter;
    private bool _initializing=true;
    private bool _initialReadStarted;
    private bool _initializingArchive=true;
    private CancellationTokenSource? _readCancellation;
    private bool _loading;
    private bool _closed;
    private bool _archiveOperation;
    private bool _packageWriteNeedsRefresh;

    public PtoWindow(string connection,long? objectId=null,long? contractId=null)
    {
        InitializeComponent();
        if(contractId.HasValue && !objectId.HasValue)throw new ArgumentException("Для договора требуется объект.");
        _connection=connection;_scopeObjectId=objectId;_scopeContractId=contractId;
        _source=new PtoSqlSource(connection);
        SetLoading(true);
        ArchiveLabel.Text="Загрузка исполнительной документации…";
        _filterTimer.Tick+=FilterTimer_Tick;
        Loaded+=async (_,_)=>await InitializeAsync();
        // Archive initialization includes the existing Pilot-only schema check/creation.
        Closing+=(_,e)=>{if(_initializingArchive||_archiveOperation)e.Cancel=true;};
        Closed+=(_,_)=>
        {
            _closed=true;
            _readCancellation?.Cancel();
            _filterTimer.Stop();
            _filterTimer.Tick-=FilterTimer_Tick;
        };
    }

    private void SetLoading(bool value)
    {
        _loading=value;
        if(value)_filterTimer.Stop();
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

    private IReadOnlyList<PtoPackageListProjection.Row> ReadRows(PtoArchive archive,CancellationToken token=default)=>
        PtoPackageListProjection.Build(archive.Sql.Packages(_scopeObjectId,_scopeContractId,token),
            _scopeObjectId,_scopeContractId,token);

    private Guid? SelectedId()=>(Packages.SelectedItem as PtoPackageListProjection.Row)?.Id;

    private void ApplyRows(IReadOnlyList<PtoPackageListProjection.Row> rows,Guid? selected)
    {
        _rows=rows;
        _publishedFilter=null;
        ArchiveLabel.Text="Файловый архив: "+_archive.Root+
            " · карточки, сертификаты, версии: Microsoft SQL Server (только GmcSmetaPilot_*)";
        ApplyFilter(selected);
    }

    private void ApplyFilter(Guid? selected)
    {
        var q=Search.Text.Trim();
        var status=(StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if(_publishedFilter is { } published && published==(q,status))return;
        var visible=PtoPackageListProjection.Filter(_rows,q,status);
        Packages.ItemsSource=visible;
        Packages.SelectedItem=visible.FirstOrDefault(r=>r.Id==selected);
        _publishedFilter=(q,status);
    }

    private void InvalidateRows()
    {
        _publishedFilter=null;
        _rows=Array.Empty<PtoPackageListProjection.Row>();
        Packages.ItemsSource=_rows;
        ArchiveLabel.Text="Список не загружен. Нажмите «Обновить».";
    }

    private async Task RefreshAsync()
    {
        if(_closed || _loading || _initializing)return;
        var selected=SelectedId();
        var archive=_archive;
        _packageWriteNeedsRefresh=true;
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
            _packageWriteNeedsRefresh=false;
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
        if(_closed || _loading || _initializing || Packages is null || Search is null)return;
        _filterTimer.Stop();
        if(ReferenceEquals(sender,StatusFilter))ApplyFilter(SelectedId());
        else _filterTimer.Start();
    }

    private void FilterTimer_Tick(object? sender,EventArgs e)
    {
        _filterTimer.Stop();
        if(!_closed && !_loading && !_initializing)ApplyFilter(SelectedId());
    }

    private void FlushPendingFilter()
    {
        if(!_filterTimer.IsEnabled)return;
        _filterTimer.Stop();
        ApplyFilter(SelectedId());
    }

    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    private async Task RunArchiveOperationAsync(Func<Task> action)
    {
        if(_closed||_loading||_initializing)return;
        FlushPendingFilter();
        _archiveOperation=true;
        SetLoading(true);
        try{await action();}
        catch(Exception ex)
        {
            if(!_closed)
            {
                if(_packageWriteNeedsRefresh)
                {
                    InvalidateRows();
                    ShowError(new InvalidOperationException(ex.Message+
                        "\n"+"Обновите список и проверьте результат перед повторением операции.",ex));
                }
                else ShowError(ex);
            }
        }
        finally
        {
            _archiveOperation=false;
            if(!_closed)SetLoading(false);
        }
    }

    private async Task ReloadAfterArchiveChangeAsync(Guid? preferred=null)
    {
        var selected=preferred??SelectedId();
        var archive=_archive;
        _packageWriteNeedsRefresh=true;
        InvalidateRows();
        var rows=await Task.Run(()=>ReadRows(archive));
        ApplyRows(rows,selected);
        _packageWriteNeedsRefresh=false;
    }

    private async void Root_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
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
        RequirePackageListCurrent();
        var source=new OpenFileDialog{Title="Безопасная SQL/ZIP копия ПТО",Filter="ZIP (*.zip)|*.zip"};
        if(source.ShowDialog(this)!=true)return;
        var path=source.FileName;
        await Task.Run(()=>PtoBackup.ValidateZip(path));
        var dest=new OpenFolderDialog{Title="Новая пустая папка восстановления"};
        if(dest.ShowDialog(this)!=true)return;
        var target=dest.FolderName;
        // Invalidate the old view before starting a restore that can change SQL.
        _packageWriteNeedsRefresh=true;
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
