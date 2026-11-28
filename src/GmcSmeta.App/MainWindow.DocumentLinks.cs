using System.IO;
using Microsoft.Win32;
using System.Windows;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    /// <summary>Открытие документа по ссылке .gmcs (links.read_link / find_contract).</summary>
    private void OpenGmcsLink(string path)
    {
        try
        {
            var target = GmcsDocumentLink.Parse(File.ReadAllText(path));
            if (target is null) return;
            var targetId = target.Resolve(_objects, _docs);
            if (targetId is int id && id > 0) OpenEstimate(id);
            else MessageBox.Show(this, "Документ из ссылки не найден в текущей базе.", "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void CreateGmcsLink(DocRow doc)
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "Создать ссылку .gmcs",
                FileName = doc.Title + ".gmcs",
                Filter = "Ссылка GMC-Smeta (*.gmcs)|*.gmcs",
            };
            if (dlg.ShowDialog(this) != true) return;
            var path = dlg.FileName;
            if (!path.EndsWith(".gmcs", StringComparison.OrdinalIgnoreCase)) path += ".gmcs";
            var obj = _objects.FirstOrDefault(o => o.Id == doc.ObjectId);
            var data = GmcsDocumentLink.Serialize(doc, obj?.Name ?? "", Environment.UserName, DateTime.Now);
            File.WriteAllText(path, data, System.Text.Encoding.UTF8);
            MessageBox.Show(this, "Ссылка создана:\n" + path, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
