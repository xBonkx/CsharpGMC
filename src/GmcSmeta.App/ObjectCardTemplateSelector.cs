using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// WPF chooses the existing real document card or the final "Новое КП" tile.
/// No document rows are replaced and no database request is performed here.
/// </summary>
public sealed class ObjectCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DocumentTemplate { get; set; }

    public DataTemplate? NewDocumentTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is NewContractCardItem ? NewDocumentTemplate : DocumentTemplate;
}
