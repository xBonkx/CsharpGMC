using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class PtoVisualTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",file)))
            root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",file));
    }

    [Theory]
    [InlineData("PtoWindow.xaml","ObjectCard_Click,Certificates_Click,Templates_Click,Backup_Click,Restore_Click,Root_Click")]
    [InlineData("PtoEditorWindow.xaml","Release_Click,History_Click,UseWorks_Click,KsVor_Click,ExternalVor_Click,ToggleFile_Click,FileProperties_Click,OpenFile_Click,FromLibrary_Click")]
    public void PythonTextOnlyButtonsStayTextOnly(string file,string handlers)
    {
        var doc=XDocument.Parse(Source(file));
        foreach(var handler in handlers.Split(','))
        {
            var b=Assert.Single(doc.Descendants().Where(n=>n.Name.LocalName=="Button" &&
                (string?)n.Attribute("Click")==handler));
            Assert.Null(b.Attribute("ContentTemplate"));
            Assert.Equal("{StaticResource PythonToolbarButton}",(string?)b.Attribute("Style"));
        }
    }

    // 30c65449: сверено с Python pto/ui.py — одобренные команды носят иконки каталога (икон-паритет обязателен).
    [Theory]
    [InlineData("PtoWindow.xaml","Create_Click=IconLabelPlus,Edit_Click=IconLabelOpen,Copy_Click=IconLabelCopy")]
    [InlineData("PtoEditorWindow.xaml","Verify_Click=IconLabelCheck,Preview_Click=IconLabelFile,Save_Click=IconLabelCheck,Up_Click=IconLabelArrowUp,Down_Click=IconLabelArrowDown,AddFile_Click=IconLabelPlus")]
    public void ApprovedCatalogIconButtonsKeepTheirIcons(string file,string pairs)
    {
        var doc=XDocument.Parse(Source(file));
        foreach(var pair in pairs.Split(','))
        {
            var parts=pair.Split('=');
            var b=Assert.Single(doc.Descendants().Where(n=>n.Name.LocalName=="Button" &&
                (string?)n.Attribute("Click")==parts[0]));
            Assert.Equal("{StaticResource "+parts[1]+"}",(string?)b.Attribute("ContentTemplate"));
            Assert.Equal("{StaticResource PythonToolbarButton}",(string?)b.Attribute("Style"));
        }
    }

    [Theory]
    [InlineData("PtoWindow.xaml",1)]
    [InlineData("PtoEditorWindow.xaml",2)]
    public void TablesKeepSharedStyleAndColumnMenu(string file,int count)
    {
        var grids=XDocument.Parse(Source(file)).Descendants().Where(n=>n.Name.LocalName=="DataGrid").ToArray();
        Assert.Equal(count,grids.Length);
        Assert.All(grids,g=>
        {
            Assert.Equal("27",(string?)g.Attribute("MinRowHeight"));
            Assert.Equal("0",(string?)g.Attribute("AlternationCount"));
            Assert.Equal("{StaticResource DocumentTable}",(string?)g.Attribute("Style"));
            Assert.Contains(g.Attributes(),a=>a.Name.LocalName=="TableColumnMenu.Key");
        });
    }

    [Fact]
    public void EditorFieldsKeepReviewInvalidationAndInputCollection()
    {
        var source=Source("PtoEditorWindow.xaml.cs");
        Assert.Contains("PtoFormUi.AddRow(host,label,box,250);_inputs[key]=box;",source);
        Assert.Contains("box.TextChanged+=(_,_)=>InvalidateReview();",source);
        Assert.Contains("foreach(var (key,box) in _inputs)_data.Fields[key]=box.Text.Trim();",source);
        Assert.Contains("if(!_data.ManualReview)",source);
    }

    [Fact]
    public void CertificatePresentationPreservesSelectedModelAndVerification()
    {
        var source=Source("PtoCertificatesWindow.cs");
        Assert.Contains("_grid.SelectedItem as PtoCertificate",source);
        Assert.Contains("PtoFormUi.ExpiryBinding()",source);
        Assert.Contains("PtoFormUi.VerifiedText",source);
        Assert.Contains("checkedBox.IsChecked=false",source);
        Assert.Contains("c.Verified=checkedBox.IsChecked==true;",source);
        var helper=Source("PtoFormUi.cs");
        Assert.Contains("value is true?\"Да\":\"Нет\"",helper);
        Assert.Contains("\"Без срока\":\"Не указан\"",helper);
        Assert.Contains("BindingMode.OneWay",helper);
    }

    [Fact]
    public void VorRetainsExplicitMappingAndConfirmation()
    {
        var source=Source("PtoVorWindow.cs");
        Assert.Contains("MaxHeight=95",source);
        Assert.Contains("Parsed is null||_confirmed.IsChecked!=true",source);
        Assert.Contains("await Task.Run(()=>PtoVor.Sheets(_path))",source);
        Assert.Contains("await Task.Run(()=>PtoVor.Read(_path,sheet,first,cols))",source);
        Assert.Contains("if(_closed||generation!=_generation)return;",source);
        Assert.Contains("Parsed.Rows.Take(250)",source);
        Assert.Contains("Parsed=null;_confirmed.IsChecked=false;",source);
        Assert.Contains("IsCancel=true",source);
    }
}
