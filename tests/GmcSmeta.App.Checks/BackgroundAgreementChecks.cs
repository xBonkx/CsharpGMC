using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GmcSmeta.App;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

internal static class BackgroundAgreementChecks
{
    internal static void Run(string output)
    {
        var connection=Environment.GetEnvironmentVariable("GMC_AGREEMENT_ACCEPTANCE_CONNECTION")??throw new Exception("Set the dedicated Agreement Pilot connection.");
        var settings=new SqlConnectionStringBuilder(connection);
        if(!SqlSchemaCompatibility.IsAgreementTestDatabase(settings.InitialCatalog,settings.DataSource))throw new Exception("Only a local Agreement Pilot is accepted.");
        using var conn=SqlServerDatabase.Open(connection);
        using var cmd=new SqlCommand("SELECT TOP(1) id FROM dbo.objects WHERE name=N'Synthetic agreement acceptance' ORDER BY id DESC",conn);
        var objectId=checked(Convert.ToInt32(cmd.ExecuteScalar()));
        var initial=SqlServerWorkspace.ReadAgreementCard(connection,objectId);
        var target=initial.LegalCards.Last();
        if(initial.LegalCards.Count!=2||target.Id==initial.SelectedLegal?.Id)
            throw new Exception("Two independent legal cards required for exact-selection check.");
        if(!string.IsNullOrWhiteSpace(target.Number))
        {
            var hits=SqlServerWorkspaceNavigation.SearchAsync(connection,target.Number).GetAwaiter().GetResult();
            if(!hits.Any(h=>h.Kind=="agreement"&&h.AgreementId==target.Id&&h.ObjectId==objectId&&h.ContractId==0))
                throw new Exception("Global search did not return the exact legal agreement.");
        }
        var page=new AgreementCardView(connection,objectId,target.Id);
        var host=new Window{Content=page,Width=1065,Height=780,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));
        host.Show();
        try
        {
            var fields=(StackPanel)page.FindName("Fields");
            Wait(()=>fields.IsEnabled,"load legal card");
            var number=(TextBox)page.FindName("AgreementNumberBox");
            var date=(TextBox)page.FindName("AgreementDateBox");
            var selector=(ComboBox)page.FindName("LegalSelector");
            if(number.IsReadOnly||date.IsReadOnly||selector.Items.Count!=2)throw new Exception("Independent legal fields are not editable or multiple agreements are missing.");
            if(selector.SelectedValue is not long selected||selected!=target.Id||number.Text!=target.Number)
                throw new Exception("Requested second legal agreement was not selected.");
            var subject=(TextBox)page.FindName("LegalSubjectBox");
            subject.Text="Verified by background WPF save";
            var saved=false;page.Saved+=()=>saved=true;
            ((Button)page.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(()=>saved,"save legal card");
            var read=SqlServerWorkspace.ReadAgreementCard(connection,objectId,target.Id);
            if(read.SelectedLegal?.Subject!=subject.Text||read.SelectedLegal.Id!=target.Id)throw new Exception("WPF legal edit was not persisted to the selected card.");
            if(read.LegalCards.Single(c=>c.Id==initial.SelectedLegal!.Id).Subject!=initial.SelectedLegal!.Subject)
                throw new Exception("Editing the second agreement changed the first agreement.");
            host.UpdateLayout();
            Directory.CreateDirectory(output);
            var bitmap=new RenderTargetBitmap(1065,780,96,96,PixelFormats.Pbgra32);bitmap.Render(page);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,"legal-card.png"));png.Save(file);
            Console.WriteLine("PASS background WPF legal card: actual SQL load, editable legal number/date, two agreements, persisted subject; no user mouse or activation.");
        }
        finally{host.Close();SynchronizationContext.SetSynchronizationContext(previousContext);}
    }

    private static void Wait(Func<bool> complete,string name)
    {
        var frame=new DispatcherFrame();var started=DateTime.UtcNow;
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(complete()||DateTime.UtcNow-started>TimeSpan.FromSeconds(15))frame.Continue=false;};
        timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        if(!complete())throw new Exception("Timeout: "+name);
    }
}
