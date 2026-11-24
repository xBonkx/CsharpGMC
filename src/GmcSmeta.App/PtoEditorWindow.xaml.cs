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
            host.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,8,0,3)});
            var shortField=key is "number" or "date" or "section" or "start" or "finish" or "copies";
            var box=new TextBox
            {
                Text=PtoRules.Text(data,key),
                AcceptsReturn=!shortField,
                TextWrapping=shortField?TextWrapping.NoWrap:TextWrapping.Wrap,
                MinHeight=shortField?29:45,
                MaxHeight=shortField?30:95,
                VerticalScrollBarVisibility=shortField?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto,
                Padding=new Thickness(7,5,7,5)
            };
            box.TextChanged+=(_,_)=>InvalidateReview();
            host.Children.Add(box);_inputs[key]=box;
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
    private bool Save()
    {
        try
        {
            Collect();
            var findings=_archive.Inspect(_data);
            if(_data.Status is "Готов" or "Подписан" &&
               (findings.Errors.Count>0 || !_data.ManualReview))
                throw new InvalidOperationException("Для статуса «Готов» или «Подписан» "+
                    "устраните ошибки и подтвердите ручную проверку.\n"+
                    string.Join("\n",findings.Errors));
            _archive.Sql.Save(_data);
            ContextLabel.Text="Объект: "+PtoRules.Text(_data,"object")+
                " · "+_data.ContractLabel+" · ревизия "+_data.Revision;
            Title="АОСР — "+PtoRules.Text(_data,"number");
            return true;
        }
        catch(Exception ex)
        {
            MessageBox.Show(this,ex.Message,"Сохранение АОСР",MessageBoxButton.OK,MessageBoxImage.Warning);
            return false;
        }
    }
    private void Run(Action action)
    {
        try{action();}
        catch(Exception ex)
        {
            MessageBox.Show(this,ex.Message,"ПТО / АОСР",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
    private void Save_Click(object sender,RoutedEventArgs e)=>Save();
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Window_Closing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(!Save())e.Cancel=true;
    }
    private void Verify_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        Collect();
        var report=_archive.Inspect(_data);
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
    private void Release_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var report=_archive.Inspect(_data);
        if(report.Errors.Count>0)
            throw new InvalidOperationException("Выпуск заблокирован:\n"+string.Join("\n",report.Errors));
        if(!_data.ManualReview)
            throw new InvalidOperationException("Подтвердите ручную проверку реквизитов, подписантов и приложений.");
        var result=_archive.BuildDelivery(_data);
        var open=MessageBox.Show(this,
            "Комплект собран в отдельную папку:\n"+result.Folder+
            "\n\nSHA-256 описи: "+result.ManifestHash+
            "\n\nОбщий PDF не сохраняет цифровые подписи исходников."+
            "\n\nОткрыть папку выпуска?",
            "Комплект для сдачи",MessageBoxButton.YesNo,MessageBoxImage.Information);
        if(open==MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(result.Folder){UseShellExecute=true});
    });
    private void History_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var history=_archive.Sql.History(_data.Id);
        var output=string.Join("\n\n",history.Select(v=>
            $"Ревизия {v.Revision} · {v.SavedUtc:dd.MM.yyyy HH:mm} UTC · {v.Action}\n"+
            JsonSerializer.Serialize(v.Snapshot,new JsonSerializerOptions(PtoRules.Json){WriteIndented=true})));
        ShowText("История ревизий АОСР (только чтение)",output);
    });
    private void ReleaseHistory_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var releases=_archive.Sql.Releases(_data.Id);
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
        list.MouseDoubleClick+=(_,_)=>Run(()=>
        {
            if(list.SelectedIndex<0)return;
            var release=releases[list.SelectedIndex];
            var dir=_archive.FilePath(release.RelativePath);
            var manifest=Path.Combine(dir,"Состав_выпуска.json");
            if(!File.Exists(manifest)||
                !PtoArchive.Sha(manifest).Equals(release.ManifestSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив выпуска отсутствует или SHA-256 описи не совпадает.");
            Process.Start(new ProcessStartInfo(dir){UseShellExecute=true});
        });
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

    private void KsVor_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(_data.ContractId is not long cid)
            throw new InvalidOperationException("Для самостоятельного акта без договора ВОР из КС недоступна.");
        if(!Save())return;
        var chosen=Choose(this,"Выберите акт КС-2",_source.Ks(cid));
        if(chosen is null)return;
        if(_data.Positions.Any(x=>x.SourceActId==chosen.Id))
            throw new InvalidOperationException("ВОР этого акта уже добавлена в комплект.");
        var rows=_source.KsWorks(cid,chosen.Id);
        if(rows.Count==0)throw new InvalidOperationException("В КС нет выполненных объёмов.");
        // Exact shared KS export logic. No writes to acts, positions, contract or estimate.
        var scope=SqlServerKsContractScope.Read(_connection,cid);
        var group=KsWorkspaceProjection.Build(scope.Acts)
            .FirstOrDefault(g=>g.Acts.Any(a=>a.Id==chosen.Id));
        if(group is null)throw new InvalidOperationException("Не найдена группа КС исходного акта.");
        var print=SqlServerKsPrintData.Read(_connection,cid,group.Key);
        var temp=Path.Combine(Path.GetTempPath(),"gmc-pto-ks-"+Guid.NewGuid().ToString("N")+".xlsx");
        try
        {
            using(var book=KsVorForms.Create(print,chosen.Id))book.SaveAs(temp);
            var attachment=_archive.AddAttachment(_data,temp,
                "ВОР по КС № "+chosen.Number+" от "+chosen.Date,"ВОР");
            foreach(var item in rows)item.AttachmentId=attachment.Id;
            _data.Positions.AddRange(rows);
            _archive.Sql.Save(_data);
            WorksGrid.Items.Refresh();RefreshFiles();
            InvalidateReview();
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    });
    private void ExternalVor_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var picker=new OpenFileDialog{Title="Внешняя ведомость объёмов работ",
            Filter="Excel (*.xlsx)|*.xlsx"};
        if(picker.ShowDialog(this)!=true)return;
        var dialog=new PtoVorWindow(picker.FileName){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.Parsed is not { } parsed)return;
        if(parsed.Rows.Count==0)
            throw new ArgumentException("В выбранной таблице нет строк с наименованиями и объёмами.");
        var attachment=_archive.AddAttachment(_data,picker.FileName,
            "Внешняя ВОР — "+Path.GetFileName(picker.FileName),"ВОР");
        foreach(var item in parsed.Rows)item.AttachmentId=attachment.Id;
        _data.Positions.AddRange(parsed.Rows);
        _archive.Sql.Save(_data);
        WorksGrid.Items.Refresh();RefreshFiles();InvalidateReview();
        if(parsed.Warnings.Count>0)
            ShowText("Предупреждения при импорте ВОР",string.Join("\n",parsed.Warnings));
    });

    private void RefreshFiles()
    {
        FilesGrid.ItemsSource=_data.Attachments.OrderBy(x=>x.Order)
            .Select(x=>new FileRow(x.Id,x.Title,x.Kind,x.Pages,x.Included,
                string.Join(", ",x.Systems))).ToArray();
    }
    private PtoAttachment? SelectedFile() =>
        FilesGrid.SelectedItem is FileRow row?
            _data.Attachments.FirstOrDefault(x=>x.Id==row.Id):null;
    private void Move(int delta)
    {
        if(SelectedFile() is not { } selected)return;
        var list=_data.Attachments.OrderBy(x=>x.Order).ToList();
        var index=list.IndexOf(selected);
        var other=index+delta;
        if(other<0 || other>=list.Count)return;
        (list[index],list[other])=(list[other],list[index]);
        for(var i=0;i<list.Count;i++)list[i].Order=i;
        _data.ManualReview=false;_data.Status="Черновик";
        _archive.Sql.Save(_data);RefreshFiles();InvalidateReview();
    }
    private void Up_Click(object sender,RoutedEventArgs e)=>Run(()=>Move(-1));
    private void Down_Click(object sender,RoutedEventArgs e)=>Run(()=>Move(1));
    private void AddFile_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var pick=new OpenFileDialog{Title="Схемы, сертификаты, паспорта, ВОР или другие приложения",
            Filter="PDF или Excel (*.pdf;*.xlsx)|*.pdf;*.xlsx",Multiselect=true};
        if(pick.ShowDialog(this)!=true)return;
        foreach(var path in pick.FileNames)
        {
            var type=Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)?
                "Исполнительная схема":"Другое приложение";
            var pages=type=="Исполнительная схема"?PtoArchive.PdfPages(path):(int?)null;
            _archive.AddAttachment(_data,path,Path.GetFileNameWithoutExtension(path),type,pages);
        }
        RefreshFiles();InvalidateReview();
    });
    private void ToggleFile_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(SelectedFile() is not { } file)return;
        file.Included=!file.Included;
        _data.ManualReview=false;_data.Status="Черновик";
        _archive.Sql.Save(_data);RefreshFiles();InvalidateReview();
    });
    private void FileProperties_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(SelectedFile() is not { } file)return;
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
                _archive.Sql.Save(_data);RefreshFiles();InvalidateReview();
                dlg.DialogResult=true;
            }
            catch(Exception ex){MessageBox.Show(dlg,ex.Message,"Приложение");}
        };
        dlg.Content=new ScrollViewer{Content=stack};dlg.ShowDialog();
    });
    private void OpenFile_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(SelectedFile() is not { } file)return;
        var state=_archive.FileInfo(file);
        if(!state.Exists || !state.HashMatches)
            throw new InvalidDataException("Файл отсутствует или отличается от оригинала.");
        Process.Start(new ProcessStartInfo(_archive.FilePath(file.RelativePath)){UseShellExecute=true});
    });
    private void FromLibrary_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(!Save())return;
        var dialog=new PtoCertificatesWindow(_archive,pick:true){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.SelectedCertificate is not { } cert)return;
        _archive.LinkCertificate(_data,cert);
        RefreshFiles();InvalidateReview();
    });
}
