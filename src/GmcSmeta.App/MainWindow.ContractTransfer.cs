using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    internal void MoveCurrentContract(DocRow doc)
    {
        if(!SqlWritesEnabled||_sqlConnectionString is null||doc.Kind!="main") return;
        try
        {
            var snapshot=SqlServerWorkspace.DescribeContractTransfer(_sqlConnectionString,doc.Id);
            if(snapshot.SourceId!=doc.ObjectId)
                throw new InvalidOperationException("Объект договора изменился. Обновите список.");
            var candidates=_objects.Where(o=>!o.Archived&&o.Id!=doc.ObjectId).OrderBy(o=>o.Name).ToList();
            if(candidates.Count==0) throw new InvalidOperationException("Нет другого действующего объекта.");
            var dialog=new Window{Owner=this,Title="Перенос договора",Width=530,SizeToContent=SizeToContent.Height,
                ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel{Margin=new Thickness(20)};
            panel.Children.Add(new TextBlock{Text=doc.Title,FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            panel.Children.Add(new TextBlock{Text=$"Из объекта «{snapshot.SourceName}». Документов к переносу: {snapshot.DocumentIds.Count}.",
                Margin=new Thickness(0,8,0,12),TextWrapping=TextWrapping.Wrap});
            panel.Children.Add(new TextBlock{Text="Новый объект",Margin=new Thickness(0,0,0,4)});
            var picker=new ComboBox{ItemsSource=candidates,DisplayMemberPath="Name",SelectedIndex=-1,MinHeight=34};
            panel.Children.Add(picker);
            panel.Children.Add(new TextBlock{Text="КС, сметы, вложения и идентификаторы сохраняются. При наличии связанных этапов или ведомостей выдачи перенос будет отклонён.",
                TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12)});
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=new Button{Content="Отмена",IsCancel=true,MinWidth=100,Margin=new Thickness(0,0,8,0)};
            var confirm=new Button{Content="Перенести",IsDefault=true,IsEnabled=false,MinWidth=110};
            picker.SelectionChanged+=(_,_)=>confirm.IsEnabled=picker.SelectedItem is ObjectRow;
            confirm.Click+=(_,_)=>dialog.DialogResult=true;
            actions.Children.Add(cancel);actions.Children.Add(confirm);panel.Children.Add(actions);
            dialog.Content=panel;
            if(dialog.ShowDialog()!=true||picker.SelectedItem is not ObjectRow target) return;
            foreach(var id in snapshot.DocumentIds)
                if(id<=int.MaxValue&&_estimateCache.TryGetValue((int)id,out var cached)&&!cached.CanTransferDocument)
                    throw new InvalidOperationException("Сначала завершите открытое редактирование и сохранение документов.");
            SqlServerWorkspace.TransferContract(_sqlConnectionString,doc.Id,snapshot.SourceId,target.Id,snapshot.DocumentIds,snapshot.Revision);
            ReloadAndSelect(target.Id);
            // The revision changed; no obsolete cached view may be reused after transfer.
            OpenEstimate(doc.Id);
            StatusText.Text=$"Договор перенесён в «{target.Name}».";
        }
        catch(Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this,ex.Message,"Перенос договора",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
}
