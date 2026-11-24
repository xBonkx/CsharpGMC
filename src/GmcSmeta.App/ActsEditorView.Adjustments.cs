using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private ActCommandTarget? CaptureAdjustment()
    {
        if(!SqlSchemaCompatibility.OrdinarySchema31Enabled||_signed||_contractLocked||
            _details?.SupportsAdjustment!=true||_totals is null||Current is null)return null;
        if(!ActGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!ActGrid.CommitEdit(DataGridEditingUnit.Row,true))
            return null;
        return CaptureActCommand();
    }

    private async void ActDiscount_Click(object sender,RoutedEventArgs e)
    {
        var target=CaptureAdjustment();
        if(target is null||Current is not { } current||_totals is not { } totals)return;
        var gross=current.Materials+current.Smr+current.Transport+current.Pnr;
        var dialog=new ActDiscountDialog(gross,current.Total,totals.DiscountPercent,
            new DiscountPolicy.ActDiscount(current.DiscountOverride,current.ManualTotalCents,current.DiscountPercent))
            {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()!=true||dialog.Result is not { } result)return;
        await ApplyAdjustmentAsync(target,()=>SqlServerActs.SaveDiscount(target.ConnectionString,target.ActId,
            result.Mode,result.Value,target.Revision),"Корректировка КС сохранена.");
    }

    private async void SectionAmounts_Click(object sender,RoutedEventArgs e)
    {
        var target=CaptureAdjustment();
        if(target is null)return;
        try
        {
            var snapshot=await Task.Run(()=>SqlServerActs.ReadSections(target.ConnectionString,target.ActId));
            RequireCurrentActCommand(target);
            if(snapshot.ContractId!=target.ContractId||snapshot.ActId!=target.ActId||
                snapshot.Revision!=target.Revision||snapshot.Signed||snapshot.Locked||!snapshot.Supported)
                throw new InvalidOperationException("Акт изменился или недоступен для правки. Перечитайте КС.");
            var labels=_sheet?.Systems.ToDictionary(s=>s.SystemId,s=>s.Label)??new Dictionary<long,string>();
            var dialog=new ActSectionAmountsDialog(snapshot,labels,Current?.Smr??snapshot.Smr)
                {Owner=Window.GetWindow(this)};
            if(dialog.ShowDialog()!=true||dialog.Result is not { } result)return;
            await ApplyAdjustmentAsync(target,()=>SqlServerActs.SaveSections(target.ConnectionString,target.ActId,
                result,target.Revision),"СМР и транспорт по разделам сохранены.");
        }
        catch(Exception ex){SetStatus(ex.Message,true);}
    }

    private async Task ApplyAdjustmentAsync(ActCommandTarget target,
        Func<SqlServerDatabase.SaveResult> write,string message)
    {
        try{RequireCurrentActCommand(target);}
        catch(Exception ex){SetStatus(ex.Message,true);return;}
        try
        {
            var saved=await RunActWriteAsync(write);
            if(_connectionString!=target.ConnectionString||_contractId!=target.ContractId||_actId!=target.ActId)return;
            _revision=saved.Saved?saved.Revision:"";
            SetStatus(saved.Saved?message:saved.Error??"Не удалось сохранить.",!saved.Saved);
        }
        catch(Exception ex)
        {
            if(_connectionString!=target.ConnectionString||_contractId!=target.ContractId||_actId!=target.ActId)return;
            _revision="";
            SetStatus("Исход сохранения неизвестен. Перечитайте КС: "+ex.Message,true);
        }
        Reload();
    }
}
