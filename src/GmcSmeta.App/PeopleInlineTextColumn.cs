using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Unbound editor: never mutate the authoritative directory record before SQL confirms.</summary>
public sealed class PeopleInlineTextColumn : DataGridTextColumn
{
    public string Field { get; set; } = "";

    protected override System.Windows.FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var editor = new TextBox
        {
            Style = EditingElementStyle,
            Text = PeopleInlineEdit.Read(dataItem, Field)
        };
        if (Field is nameof(PeoplePerson.FioGen) or nameof(PeoplePerson.PostGen))
            editor.ToolTip = "Пусто — автоматически";
        return editor;
    }
}
