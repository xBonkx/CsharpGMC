using GmcSmeta.Core.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class PtoWindow
{
    private bool Belongs(PtoPackage p)=>
        (!_scopeObjectId.HasValue || p.ObjectId==_scopeObjectId) &&
        (!_scopeContractId.HasValue || p.ContractId==_scopeContractId);
    private void RequirePackageListCurrent()
    {
        if(_packageWriteNeedsRefresh)
            throw new InvalidOperationException("Сначала обновите список комплектов и проверьте результат предыдущей операции.");
    }

    private async Task<PtoPackage?> SelectedAsync()
    {
        var id=SelectedId();
        if(id is null)return null;
        var archive=_archive;
        var current=await Task.Run(()=>archive.Sql.Get(id.Value,_scopeObjectId));
        if(!Belongs(current))
        {
            await ReloadAfterArchiveChangeAsync();
            return null;
        }
        return current;
    }

    private async Task EditAsync(PtoPackage p)
    {
        // The editor can save or release a package. Do not reuse the list
        // until its authoritative post-dialog readback succeeds.
        _packageWriteNeedsRefresh=true;
        new PtoEditorWindow(_archive,p,_connection) { Owner=this }.ShowDialog();
        await ReloadAfterArchiveChangeAsync(p.Id);
    }

    private async void Create_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        // One contract query per picker, not one synchronous SQL read per object switch.
        var choices=await Task.Run(()=>
        {
            var availableObjects=_source.Objects().Where(o=>_scopeObjectId is null || o.Id==_scopeObjectId).ToArray();
            if(availableObjects.Length==0)throw new InvalidOperationException("Для ПТО отсутствуют доступные объекты.");
            return (Objects:availableObjects,Contracts:_source.Contracts(_scopeObjectId).ToLookup(c=>c.ObjectId));
        });
        var objects=choices.Objects;
        if(_scopeContractId is long cid)
        {
            var scopedObject=objects.Single();
            var scopedContract=choices.Contracts[scopedObject.Id].FirstOrDefault(c=>c.Id==cid) ??
                throw new InvalidOperationException("Договор больше не доступен в выбранном объекте.");
            await CreateForAsync(scopedObject,scopedContract);
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
            items.AddRange(choices.Contracts[choice.Id]);
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
        await CreateForAsync(obj,contract);
    });

    private async Task CreateForAsync(PtoSqlSource.ObjectChoice obj,PtoSqlSource.ContractChoice? contract)
    {
        if(_scopeObjectId.HasValue && obj.Id!=_scopeObjectId ||
            _scopeContractId.HasValue && contract?.Id!=_scopeContractId)
            throw new InvalidOperationException("Неверная привязка комплекта ПТО.");
        var archive=_archive;
        _packageWriteNeedsRefresh=true;
        var p=await Task.Run(()=>
        {
            // Revalidate the chosen source after the modal picker.
            var currentObject=_source.Objects().FirstOrDefault(o=>o.Id==obj.Id) ??
                throw new InvalidOperationException("Объект больше не доступен.");
            var currentContract=contract is null?null:
                _source.Contracts(obj.Id).FirstOrDefault(c=>c.Id==contract.Id) ??
                throw new InvalidOperationException("Договор больше не доступен в выбранном объекте.");
            var package=_source.Snapshot(currentObject,currentContract,archive.Sql.ObjectProfile(obj.Id));
            package.Fields["number"]=PtoRules.NextNumber(archive.Sql.Packages(obj.Id),package);
            archive.Sql.Save(package);
            return package;
        });
        await EditAsync(p);
    }

    private async void Edit_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        if(await SelectedAsync() is { } selected)await EditAsync(selected);
    });
    private void Packages_DoubleClick(object sender,MouseButtonEventArgs e)=>Edit_Click(sender,e);

    private async void Copy_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        if(await SelectedAsync() is not { } p)return;
        var archive=_archive;
        _packageWriteNeedsRefresh=true;
        var copy=await Task.Run(()=>archive.Sql.Copy(p.Id));
        await EditAsync(copy);
    });

    private async void Delete_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        if(await SelectedAsync() is not { } p)return;
        if(MessageBox.Show(this,"Удалить АОСР № "+PtoRules.Text(p,"number")+
            "? Ранее опубликованные комплекты и исходные файлы остаются в архиве.",
            "Удаление",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        var archive=_archive;
        _packageWriteNeedsRefresh=true;
        await Task.Run(()=>archive.Sql.Delete(p));
        await ReloadAfterArchiveChangeAsync();
    });

    private async void Certificates_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        new PtoCertificatesWindow(_archive) {Owner=this}.ShowDialog();
        await ReloadAfterArchiveChangeAsync();
    });

    private async void ObjectCard_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        RequirePackageListCurrent();
        var hadSelection=SelectedId().HasValue;
        var selected=await SelectedAsync();
        if(hadSelection && selected is null)return;
        var archive=_archive;
        var profile=await Task.Run(()=>
        {
            var objects=_source.Objects();
            var candidate=selected is not null?objects.FirstOrDefault(x=>x.Id==selected.ObjectId):
                objects.FirstOrDefault(x=>_scopeObjectId is null || x.Id==_scopeObjectId);
            if(candidate is null)return (Object:(PtoSqlSource.ObjectChoice?)null,Profile:new Dictionary<string,string>(),Revision:0L);
            var (data,version)=archive.Sql.ReadObjectProfile(candidate.Id);
            return (Object:(PtoSqlSource.ObjectChoice?)candidate,Profile:data,Revision:version);
        });
        var obj=profile.Object;
        if(obj is null)return;
        var (current,revision)=(profile.Profile,profile.Revision);
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
        var saving=false;
        dialog.Closing+=(_,e)=>{if(saving)e.Cancel=true;};
        save.Click+=async (_,_) =>
        {
            if(saving||!save.IsEnabled)return;
            foreach(var(key,value) in fields)current[key]=value.Text.Trim();
            saving=true;
            panel.IsEnabled=false;
            try
            {
                await Task.Run(()=>archive.Sql.SaveObjectProfile(obj.Id,current,revision));
                saving=false;
                dialog.DialogResult=true;
            }
            catch(Exception ex)
            {
                saving=false;
                panel.IsEnabled=true;
                save.IsEnabled=false;
                MessageBox.Show(dialog,ex.Message+"\nЗакройте карточку и проверьте сохранённые реквизиты.",
                    "Карточка ПТО",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        };
        dialog.Content=panel;dialog.ShowDialog();
    });

    private async void Templates_Click(object sender,RoutedEventArgs e)=>await RunArchiveOperationAsync(async ()=>
    {
        var dialog=new OpenFileDialog{Title="Шаблон АОСР или реестра",
            Filter="Excel (*.xlsx)|*.xlsx"};
        if(dialog.ShowDialog(this)!=true)return;
        var kind=MessageBox.Show(this,
            "Какой оригинальный шаблон загружаете?\nДа — АОСР\nНет — Реестр\nОтмена — не импортировать",
            "Пользовательский шаблон",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(kind==MessageBoxResult.Cancel)return;
        var archive=_archive;
        var path=dialog.FileName;
        var templateKind=kind==MessageBoxResult.Yes?"aosr":"registry";
        await Task.Run(()=>archive.StoreTemplate(templateKind,path));
        MessageBox.Show(this,"Шаблон проверен и сохранён. Оригинал не изменён.","ПТО");
    });
}
