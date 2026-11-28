using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class OfficeDocumentVisualTests
{
    private static XDocument Read(string name)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",name)))
            root=root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName,"src","GmcSmeta.App",name));
    }

    private static XElement Control(XDocument doc,string name)
    {
        XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        return Assert.Single(doc.Descendants().Where(n=>(string?)n.Attribute(x+"Name")==name));
    }

    [Theory]
    [InlineData("LettersWindow.xaml","New_Click,Open_Click,Copy_Click,Root_Click,Backup_Click,Restore_Click,AttachExisting_Click")]
    [InlineData("LetterEditorWindow.xaml","Save_Click,Preview_Click,Build_Click,Sent_Click,AutoNumber_Click,Object_Click,Company_Click,FileAdd_Click,FileOpen_Click,FileRename_Click,FileToggle_Click,FileUp_Click,FileDown_Click")]
    [InlineData("PassportsWindow.xaml","Add_Click,Batch_Click,Duplicate_Click,Delete_Click,Root_Click,Save_Click,ExportSelected_Click,ExportAll_Click,AddPoint_Click,DeletePoint_Click")]
    public void OfficeCommandsRetainApprovedIconOrTextRoles(string file,string handlers)
    {
        var doc=Read(file);
        foreach(var handler in handlers.Split(','))
        {
            var button=Assert.Single(doc.Descendants().Where(n=>n.Name.LocalName=="Button" &&
                (string?)n.Attribute("Click")==handler));
            var expected = (file, handler) switch
            {
                ("LettersWindow.xaml", "New_Click") => "IconLabelPlus",
                ("LettersWindow.xaml", "Open_Click" or "Root_Click") => "IconLabelOpen",
                ("LettersWindow.xaml", "Copy_Click") => "IconLabelCopy",
                ("LetterEditorWindow.xaml", "Save_Click") => "IconLabelCheck",
                ("LetterEditorWindow.xaml", "Preview_Click") => "IconLabelFile",
                ("LetterEditorWindow.xaml", "Build_Click") => "IconLabelDownload",
                ("LetterEditorWindow.xaml", "FileUp_Click") => "IconOnlyArrowUp",
                ("LetterEditorWindow.xaml", "FileDown_Click") => "IconOnlyArrowDown",
                ("PassportsWindow.xaml", "Add_Click") => "IconLabelPlus",
                ("PassportsWindow.xaml", "Duplicate_Click") => "IconLabelCopy",
                ("PassportsWindow.xaml", "Delete_Click") => "IconLabelTrash",
                ("PassportsWindow.xaml", "ExportSelected_Click" or "ExportAll_Click") => "IconLabelDownload",
                _ => null
            };
            Assert.Equal(expected is null ? null : "{StaticResource " + expected + "}",
                (string?)button.Attribute("ContentTemplate"));
            Assert.Equal("{StaticResource PythonToolbarButton}",(string?)button.Attribute("Style"));
        }
    }

    [Theory]
    [InlineData("LettersWindow.xaml","LetterList")]
    [InlineData("LetterEditorWindow.xaml","FilesGrid")]
    [InlineData("PassportsWindow.xaml","PassportList")]
    [InlineData("PassportsWindow.xaml","EquipmentGrid")]
    [InlineData("PassportsWindow.xaml","PointsGrid")]
    public void DocumentTablesKeepControlsAndUsePlain27DipRows(string file,string name)
    {
        var grid=Control(Read(file),name);
        Assert.Equal("27",(string?)grid.Attribute("MinRowHeight"));
        Assert.Equal("0",(string?)grid.Attribute("AlternationCount"));
        Assert.Equal("{StaticResource DocumentTable}",(string?)grid.Attribute("Style"));
        Assert.Equal("{StaticResource PythonTableRow}",(string?)grid.Attribute("RowStyle"));
    }

    [Fact]
    public void SearchAndNotesPlaceholdersDoNotChangeEditing()
    {
        var letters=Read("LettersWindow.xaml");
        Assert.Equal("Search_Changed",(string?)Control(letters,"SearchBox").Attribute("TextChanged"));
        Assert.Contains("Номер, тема, объект, адресат…",letters.ToString());
        var passports=Read("PassportsWindow.xaml");
        Assert.Contains("Примечания к оборудованию",passports.ToString());
        Assert.Equal("Points_KeyDown",(string?)Control(passports,"PointsGrid").Attribute("PreviewKeyDown"));
        Assert.Equal("Equipment_BeginningEdit",(string?)Control(passports,"EquipmentGrid").Attribute("BeginningEdit"));
    }

    [Fact]
    public void OrdersKeepOriginalGlyphsAndSelectedPerson()
    {
        var doc=Read("OrdersWindow.xaml");
        var make=Control(doc,"MakeButton");
        Assert.Equal("{StaticResource IconLabelFile}",(string?)make.Attribute("ContentTemplate"));
        Assert.Equal("Make_Click",(string?)make.Attribute("Click"));
        var person=Control(doc,"PersonBox");
        Assert.Equal("Person_Changed",(string?)person.Attribute("SelectionChanged"));
        Assert.Contains("Path=\"Fio\"",person.ToString());
        Assert.Contains("Path=\"Post\"",person.ToString());
        Assert.Contains("{StaticResource GlyphFolder}",doc.ToString());
        Assert.Contains("#EA580C",doc.ToString());
        Assert.Contains("IsEnabled",doc.ToString());
    }
}
