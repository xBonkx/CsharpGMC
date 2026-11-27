using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Five-tab AOSR editor; every edit is based on the loaded optimistic SQL revision.</summary>
public partial class PtoEditorWindow : Window
{
    private readonly PtoArchive _archive;
    private readonly string _connection;
    private readonly PtoSqlSource _source;
    private readonly PtoPackage _data;
    private readonly Dictionary<string,TextBox> _inputs=new();
    private bool _loading;
    private bool _busy,_allowClose,_closingSave,_writeUncertain;
    private string? _idleContext;
    private sealed record FileRow(Guid Id,string Title,string Kind,int? Pages,
        bool Included,string SystemsLabel);

    public PtoEditorWindow(PtoArchive archive,PtoPackage data,string connection)
    {
        _loading=true;
        InitializeComponent();
        _archive=archive;_data=data;_connection=connection;
        _source=new PtoSqlSource(connection);
        ContextLabel.Text="Объект: "+PtoRules.Text(data,"object")+
            (data.ContractId.HasValue?" · Договор: "+data.ContractLabel:
                " · Самостоятельный комплект без договора")+
            "\nРевизия: "+data.Revision+" · SQL Server; оригиналы в архиве: "+archive.Root;
        Title="АОСР — "+PtoRules.Text(data,"number");
        StatusCombo.ItemsSource=PtoRules.Statuses;
        foreach(var (key,label) in PtoRules.Fields)
        {
            var host=key.EndsWith("_rep",StringComparison.Ordinal) ||
                     key is "handed_by" or "accepted_by" or "other_rep"
                ?SignersPanel
                :key is "object" or "address" or "customer" or "contractor" or
                 "designer" or "executor" or "project" or "standards"?
                 ObjectPanel:ActPanel;
            var shortField=key is "number" or "date" or "section" or "start" or "finish" or "copies";
            var box=new TextBox
            {
                Text=PtoRules.Text(data,key),
                AcceptsReturn=!shortField,
                TextWrapping=shortField?TextWrapping.NoWrap:TextWrapping.Wrap,
                MinHeight=shortField?32:48,
                MaxHeight=shortField?32:80,
                VerticalScrollBarVisibility=shortField?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto,
                Padding=new Thickness(7,5,7,5)
            };
            box.TextChanged+=(_,_)=>InvalidateReview();
            PtoFormUi.AddRow(host,label,box,250);_inputs[key]=box;
        }
        StatusCombo.SelectedItem=data.Status;
        ReviewCheck.IsChecked=data.ManualReview;
        WorksGrid.ItemsSource=data.Positions;
        RefreshFiles();
        _loading=false;
    }
    private void InvalidateReview()
    {
        if(_loading)return;
        ReviewCheck.IsChecked=false;
        if(StatusCombo.SelectedItem is string status && status is "Готов" or "Подписан")
            StatusCombo.SelectedItem="Черновик";
    }
    private void Collect()
    {
        foreach(var (key,box) in _inputs)_data.Fields[key]=box.Text.Trim();
        _data.Status=StatusCombo.SelectedItem?.ToString()??"Черновик";
        _data.ManualReview=ReviewCheck.IsChecked==true;
        PtoRules.ValidateShape(_data);
    }
    private void SetBusy(bool busy)
    {
        _busy=busy;
        EditorActions.IsEnabled=!busy;
        EditorTabs.IsEnabled=!busy&&!_writeUncertain;
        foreach(var button in EditorActions.Children.OfType<Button>())
            button.IsEnabled=!busy&&(!_writeUncertain||ReferenceEquals(button,CloseButton));
        if(busy)
        {
            _idleContext=ContextLabel.Text;
            ContextLabel.Text="Выполняется операция ПТО…";
        }
        else
        {
            ContextLabel.Text=_writeUncertain
                ?"Запись не подтверждена. Закройте акт и проверьте SQL и архив перед повтором."
                :_idleContext??ContextLabel.Text;
        }
    }

    private void UpdateRevisionLabel()
    {
        _idleContext="Объект: "+PtoRules.Text(_data,"object")+
            " · "+_data.ContractLabel+" · ревизия "+_data.Revision;
        Title="АОСР — "+PtoRules.Text(_data,"number");
    }

    private async Task SaveAsync()
    {
        // Collect on the dispatcher before exclusively handing the model to the worker.
        Collect();
        await Task.Run(()=>
        {
            var findings=_archive.Inspect(_data);
            if(_data.Status is "Готов" or "Подписан" &&
               (findings.Errors.Count>0 || !_data.ManualReview))
                throw new InvalidOperationException("Для статуса «Готов» или «Подписан» "+
                    "устраните ошибки и подтвердите ручную проверку.\n"+
                    string.Join("\n",findings.Errors));
            _archive.Sql.Save(_data);
        });
        UpdateRevisionLabel();
    }

    private void HandleError(Exception ex)
    {
        // BuildDelivery wraps an uncertain SQL commit with the published folder.
        for(Exception? error=ex;error is not null;error=error.InnerException)
            if(error is PtoCommitUncertainException){_writeUncertain=true;break;}
        MessageBox.Show(this,ex.Message+(_writeUncertain
            ?"\nЗапись не подтверждена. Не повторяйте операцию до сверки SQL и архива.":""),
            "ПТО / АОСР",MessageBoxButton.OK,MessageBoxImage.Warning);
    }

    private void Run(Action action)
    {
        if(_busy||_writeUncertain||_closingSave)return;
        try{action();}
        catch(Exception ex){HandleError(ex);if(_writeUncertain)SetBusy(false);}
    }

    private async Task RunAsync(Func<Task> action)
    {
        if(_busy||_writeUncertain||_closingSave)return;
        SetBusy(true);
        try{await action();}
        catch(Exception ex){HandleError(ex);}
        finally{SetBusy(false);}
    }

    private async void Save_Click(object sender,RoutedEventArgs e)=>await RunAsync(SaveAsync);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Window_Closing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(_allowClose)return;
        if(_busy||_closingSave){e.Cancel=true;return;}
        if(_writeUncertain)
        {
            e.Cancel=MessageBox.Show(this,
                "Запись не подтверждена. Закрыть без повторного сохранения и проверить данные?",
                "АОСР",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes;
            return;
        }
        // Closing is synchronous; cancel first, then save and close only after confirmation.
        e.Cancel=true;
        _closingSave=true;
        Dispatcher.BeginInvoke(new Action(()=>_ = SaveAndCloseAsync()));
    }

    private async Task SaveAndCloseAsync()
    {
        SetBusy(true);
        try
        {
            await SaveAsync();
            _allowClose=true;
        }
        catch(Exception ex){HandleError(ex);}
        finally
        {
            _closingSave=false;
            SetBusy(false);
        }
        if(_allowClose)Close();
    }
    private async void Verify_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        Collect();
        var report=await Task.Run(()=>_archive.Inspect(_data));
        ShowText("Проверка комплекта",
            "КРИТИЧЕСКИЕ ОШИБКИ\n"+(report.Errors.Count==0?"Не выявлены":string.Join("\n",report.Errors))+
            "\n\nПРЕДУПРЕЖДЕНИЯ И РУЧНЫЕ ДЕЙСТВИЯ\n"+
            string.Join("\n",report.Warnings)+
            "\n\nРучное подтверждение: "+(_data.ManualReview?"отмечено":"НЕ выполнено"));
    });
    private void Preview_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        Collect();
        ShowText("Предпросмотр содержания — не печатный макет Excel",
            string.Join("\n\n",PtoRules.Fields.Select(pair=>
                pair.Value+":\n"+PtoRules.Text(_data,pair.Key)))+
            "\n\nПРИЛОЖЕНИЯ\n"+string.Join("\n",_data.Attachments.Where(a=>a.Included)
                .OrderBy(a=>a.Order).Select((a,i)=>$"{i+1}. {a.Title} ({a.Pages??0} л.)")));
    });
    private async void Release_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        if(!_data.ManualReview)
            throw new InvalidOperationException("Подтвердите ручную проверку реквизитов, подписантов и приложений.");
        // BuildDelivery independently verifies files, review, revision and registration.
        var result=await Task.Run(()=>_archive.BuildDelivery(_data));
        var open=MessageBox.Show(this,
            "Комплект собран в отдельную папку:\n"+result.Folder+
            "\n\nSHA-256 описи: "+result.ManifestHash+
            "\n\nОбщий PDF не сохраняет цифровые подписи исходников."+
            "\n\nОткрыть папку выпуска?",
            "Комплект для сдачи",MessageBoxButton.YesNo,MessageBoxImage.Information);
        if(open==MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(result.Folder){UseShellExecute=true});
    });
    private async void History_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        var id=_data.Id;
        var output=await Task.Run(()=>
        {
            var history=_archive.Sql.History(id);
            var json=new JsonSerializerOptions(PtoRules.Json){WriteIndented=true};
            return string.Join("\n\n",history.Select(v=>
                $"Ревизия {v.Revision} · {v.SavedUtc:dd.MM.yyyy HH:mm} UTC · {v.Action}\n"+
                JsonSerializer.Serialize(v.Snapshot,json)));
        });
        ShowText("История ревизий АОСР (только чтение)",output);
    });
    private async void ReleaseHistory_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        var id=_data.Id;
        var releases=await Task.Run(()=>_archive.Sql.Releases(id));
        if(releases.Count==0)
        {
            MessageBox.Show(this,"Выпусков этого АОСР пока нет.","История выпусков");return;
        }
        var dlg=new Window{Owner=this,Title="История выпусков — АОСР № "+PtoRules.Text(_data,"number"),
            Width=860,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new DockPanel{Margin=new Thickness(12)};
        var hint=new TextBlock{Text="Дважды щёлкните выпуск, чтобы открыть его папку после проверки SHA-256 описи.",
            Margin=new Thickness(0,0,0,8),TextWrapping=TextWrapping.Wrap};
        DockPanel.SetDock(hint,Dock.Top);panel.Children.Add(hint);
        var list=new ListBox{ItemsSource=releases.Select(x=>
            x.CreatedUtc.ToString("dd.MM.yyyy HH:mm")+" UTC  ·  "+x.RelativePath).ToArray()};
        bool opening=false,closed=false;
        dlg.Closed+=(_,_)=>closed=true;
        list.MouseDoubleClick+=async (_,_)=>
        {
            if(opening||closed||list.SelectedIndex<0)return;
            var release=releases[list.SelectedIndex];
            opening=true;list.IsEnabled=false;
            try
            {
                var dir=await Task.Run(()=>
                {
                    var path=_archive.FilePath(release.RelativePath);
                    var manifest=Path.Combine(path,"Состав_выпуска.json");
                    if(!File.Exists(manifest)||
                        !PtoArchive.Sha(manifest).Equals(release.ManifestSha256,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архив выпуска отсутствует или SHA-256 описи не совпадает.");
                    return path;
                });
                if(!closed)Process.Start(new ProcessStartInfo(dir){UseShellExecute=true});
            }
            catch(Exception ex){if(!closed)MessageBox.Show(dlg,ex.Message,"История выпусков");}
            finally{opening=false;if(!closed)list.IsEnabled=true;}
        };
        panel.Children.Add(list);dlg.Content=panel;dlg.ShowDialog();
    });

    private void ShowText(string title,string text)
    {
        var dlg=new Window{Title=title,Owner=this,Width=870,Height=660,
            WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dlg.Content=new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};
        dlg.ShowDialog();
    }
    private void UseWorks_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        var selected=WorksGrid.SelectedItems.OfType<PtoWork>().ToList();
        if(selected.Count==0)throw new ArgumentException("Выделите работы из договора / ВОР.");
        Collect();
        var lines=selected.Select(x=>string.Join(" ",new[]{x.System,x.Name,x.Qty,x.Unit}
            .Where(x=>!string.IsNullOrWhiteSpace(x)))).ToArray();
        var existing=PtoRules.Text(_data,"works");
        _inputs["works"].Text=string.Join("\n",new[]{existing,string.Join("\n",lines)}
            .Where(s=>s.Length>0));
        foreach(var system in selected.Select(x=>x.System).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase))
            if(!_data.SelectedWorkSystems.Contains(system,StringComparer.OrdinalIgnoreCase))
                _data.SelectedWorkSystems.Add(system);
        InvalidateReview();
        MessageBox.Show(this,$"Добавлено строк работ: {selected.Count}. Сверьте объёмы перед подписью.","ПТО");
    });

    private static T? Choose<T>(Window owner,string title,IReadOnlyList<T> data)
        where T:class
    {
        if(data.Count==0)return null;
        var dialog=new Window{Owner=owner,Title=title,Width=620,Height=430,
            WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var dock=new DockPanel{Margin=new Thickness(12)};
        var button=new Button{Content="Выбрать",Padding=new Thickness(12,7,12,7),
            HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,7,0,0)};
        DockPanel.SetDock(button,Dock.Bottom);dock.Children.Add(button);
        var list=new ListBox{ItemsSource=data};
        list.MouseDoubleClick+=(_,_)=>{if(list.SelectedItem is not null)dialog.DialogResult=true;};
        button.Click+=(_,_)=>{if(list.SelectedItem is not null)dialog.DialogResult=true;};
        dock.Children.Add(list);dialog.Content=dock;
        return dialog.ShowDialog()==true?list.SelectedItem as T:null;
    }

    private async void KsVor_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        if(_data.ContractId is not long cid)
            throw new InvalidOperationException("Для самостоятельного акта без договора ВОР из КС недоступна.");
        await SaveAsync();
        var acts=await Task.Run(()=>_source.Ks(cid));
        var chosen=Choose(this,"Выберите акт КС-2",acts);
        if(chosen is null)return;
        if(_data.Positions.Any(x=>x.SourceActId==chosen.Id))
            throw new InvalidOperationException("ВОР этого акта уже добавлена в комплект.");
        var rows=await Task.Run(()=>_source.KsWorks(cid,chosen.Id));
        if(rows.Count==0)throw new InvalidOperationException("В КС нет выполненных объёмов.");
        // Exact shared KS export logic. No writes to acts, positions, contract or estimate.
        var folder=Directory.CreateTempSubdirectory("gmc-pto-ks-").FullName;
        var temp=Path.Combine(folder,"vor.xlsx");
        try
        {
            var attachment=await Task.Run(()=>
            {
                var scope=SqlServerKsContractScope.Read(_connection,cid);
                var group=KsWorkspaceProjection.Build(scope.Acts)
                    .FirstOrDefault(g=>g.Acts.Any(a=>a.Id==chosen.Id));
                if(group is null)throw new InvalidOperationException("Не найдена группа КС исходного акта.");
                var print=SqlServerKsPrintData.Read(_connection,cid,group.Key);
                using(var book=KsVorForms.Create(print,chosen.Id))book.SaveAs(temp);
                return _archive.AddAttachment(_data,temp,
                    "ВОР по КС № "+chosen.Number+" от "+chosen.Date,"ВОР");
            });
            // The bound work list is changed only on the dispatcher.
            foreach(var item in rows)item.AttachmentId=attachment.Id;
            _data.Positions.AddRange(rows);
            await Task.Run(()=>_archive.Sql.Save(_data));
        }
        finally
        {
            try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            WorksGrid.Items.Refresh();RefreshFiles();InvalidateReview();UpdateRevisionLabel();
        }
    });
    private async void ExternalVor_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        var picker=new OpenFileDialog{Title="Внешняя ведомость объёмов работ",
            Filter="Excel (*.xlsx)|*.xlsx"};
        if(picker.ShowDialog(this)!=true)return;
        var dialog=new PtoVorWindow(picker.FileName){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.Parsed is not { } parsed)return;
        if(parsed.Rows.Count==0)
            throw new ArgumentException("В выбранной таблице нет строк с наименованиями и объёмами.");
        var path=picker.FileName;
        try
        {
            var attachment=await Task.Run(()=>_archive.AddAttachment(_data,path,
                "Внешняя ВОР — "+Path.GetFileName(path),"ВОР"));
            foreach(var item in parsed.Rows)item.AttachmentId=attachment.Id;
            _data.Positions.AddRange(parsed.Rows);
            await Task.Run(()=>_archive.Sql.Save(_data));
        }
        finally{WorksGrid.Items.Refresh();RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
        if(parsed.Warnings.Count>0)
            ShowText("Предупреждения при импорте ВОР",string.Join("\n",parsed.Warnings));
    });

    private void RefreshFiles()
    {
        var selected=(FilesGrid.SelectedItem as FileRow)?.Id;
        var rows=_data.Attachments.OrderBy(x=>x.Order)
            .Select(x=>new FileRow(x.Id,x.Title,x.Kind,x.Pages,x.Included,
                string.Join(", ",x.Systems))).ToArray();
        FilesGrid.ItemsSource=rows;
        FilesGrid.SelectedItem=rows.FirstOrDefault(x=>x.Id==selected);
    }
    private PtoAttachment? SelectedFile() =>
        FilesGrid.SelectedItem is FileRow row?
            _data.Attachments.FirstOrDefault(x=>x.Id==row.Id):null;
    private async Task MoveAsync(int delta)
    {
        if(SelectedFile() is not { } selected)return;
        var list=_data.Attachments.OrderBy(x=>x.Order).ToList();
        var index=list.IndexOf(selected);
        var other=index+delta;
        if(other<0 || other>=list.Count)return;
        Collect();
        (list[index],list[other])=(list[other],list[index]);
        for(var i=0;i<list.Count;i++)list[i].Order=i;
        _data.ManualReview=false;_data.Status="Черновик";
        try{await Task.Run(()=>_archive.Sql.Save(_data));}
        finally{RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
    }
    private async void Up_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>MoveAsync(-1));
    private async void Down_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>MoveAsync(1));
    private async void AddFile_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        var pick=new OpenFileDialog{Title="Схемы, сертификаты, паспорта, ВОР или другие приложения",
            Filter="PDF или Excel (*.pdf;*.xlsx)|*.pdf;*.xlsx",Multiselect=true};
        if(pick.ShowDialog(this)!=true)return;
        var paths=pick.FileNames;
        try
        {
            await Task.Run(()=>
            {
                foreach(var path in paths)
                {
                    var type=Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)?
                        "Исполнительная схема":"Другое приложение";
                    var pages=type=="Исполнительная схема"?PtoArchive.PdfPages(path):(int?)null;
                    _archive.AddAttachment(_data,path,Path.GetFileNameWithoutExtension(path),type,pages);
                }
            });
        }
        finally{RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
    });
    private async void ToggleFile_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        if(SelectedFile() is not { } file)return;
        Collect();
        file.Included=!file.Included;
        _data.ManualReview=false;_data.Status="Черновик";
        try{await Task.Run(()=>_archive.Sql.Save(_data));}
        finally{RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
    });
    private async void FileProperties_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        if(SelectedFile() is not { } file)return;
        Collect();
        var dlg=new Window{Owner=this,Title="Реквизиты / системы схемы",Width=620,Height=420,
            WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var stack=new StackPanel{Margin=new Thickness(16)};
        TextBox Add(string title,string value)
        {
            stack.Children.Add(new TextBlock{Text=title,Margin=new Thickness(0,10,0,4)});
            var edit=new TextBox{Text=value,Padding=new Thickness(6)};
            stack.Children.Add(edit);return edit;
        }
        var title=Add("Название",file.Title);
        var kind=Add("Тип приложения",file.Kind);
        var pages=Add("Количество листов",file.Pages?.ToString()??"");
        var systems=Add("Системы вентиляции через запятую",string.Join(", ",file.Systems));
        var save=new Button{Content="Сохранить",Padding=new Thickness(10,6,10,6),
            HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        stack.Children.Add(save);
        save.Click+=(_,_)=>
        {
            try
            {
                if(title.Text.Trim().Length==0)throw new ArgumentException("Укажите название.");
                if(pages.Text.Trim().Length>0 && (!int.TryParse(pages.Text.Trim(),out var n)||n<1))
                    throw new ArgumentException("Количество листов должно быть положительным.");
                file.Title=title.Text.Trim();file.Kind=kind.Text.Trim();
                file.Pages=pages.Text.Trim().Length==0?null:int.Parse(pages.Text.Trim());
                file.Systems=systems.Text.Split(',',';','\n').Select(x=>x.Trim())
                    .Where(x=>x.Length>0).Distinct().ToList();
                _data.ManualReview=false;_data.Status="Черновик";
                dlg.DialogResult=true;
            }
            catch(Exception ex){MessageBox.Show(dlg,ex.Message,"Приложение");}
        };
        dlg.Content=new ScrollViewer{Content=stack};
        if(dlg.ShowDialog()!=true)return;
        try{await Task.Run(()=>_archive.Sql.Save(_data));}
        finally{RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
    });
    private async void OpenFile_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        if(SelectedFile() is not { } file)return;
        var path=await Task.Run(()=>
        {
            var state=_archive.FileInfo(file);
            if(!state.Exists || !state.HashMatches)
                throw new InvalidDataException("Файл отсутствует или отличается от оригинала.");
            return _archive.FilePath(file.RelativePath);
        });
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    });
    private async void FromLibrary_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        await SaveAsync();
        var dialog=new PtoCertificatesWindow(_archive,pick:true){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.SelectedCertificate is not { } cert)return;
        try{await Task.Run(()=>_archive.LinkCertificate(_data,cert));}
        finally{RefreshFiles();InvalidateReview();UpdateRevisionLabel();}
    });
}
