using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private static readonly IReadOnlyDictionary<long, string> EmptyRefNumbers =
        new ReadOnlyDictionary<long, string>(new Dictionary<long, string>());
    private IReadOnlyDictionary<long, string>? _indexedRefNumbers;
    private ActQuantityReferenceIndex? _quantityReferenceIndex;
    private KsReferenceHighlights? _referenceHighlights;

    /// <summary>Use the read-only published sheet map directly, without
    /// copying every position number for each formatted formula.</summary>
    private IReadOnlyDictionary<long, string> RefNumbers() =>
        _sheet?.RefDisplay ?? EmptyRefNumbers;

    private ActQuantityReferenceIndex QuantityReferenceIndex()
    {
        var numbers = RefNumbers();
        if (!ReferenceEquals(numbers, _indexedRefNumbers) || _quantityReferenceIndex is null)
        {
            _indexedRefNumbers = numbers;
            _quantityReferenceIndex = new ActQuantityReferenceIndex(numbers);
        }
        return _quantityReferenceIndex;
    }

    private void QuantityEditor_TextChanged(object sender, TextChangedEventArgs e) => UpdateQuantityReferences();

    private void QuantityEditor_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _qtyEditor)) DetachQuantityEditor();
    }

    private void UpdateQuantityReferences()
    {
        if (_rows is null) return;
        var references = QuantityReferenceIndex().EditingReferences(_qtyEditor?.Text);
        if (references.Count == 0)
        {
            _referenceHighlights?.Clear();
            return;
        }
        (_referenceHighlights ??= new KsReferenceHighlights(_rows)).Update(references);
    }

    private void DetachQuantityEditor()
    {
        if (_qtyEditor is { } editor)
        {
            editor.KeyDown -= QtyEditor_KeyDown;
            editor.TextChanged -= QuantityEditor_TextChanged;
            editor.Unloaded -= QuantityEditor_Unloaded;
            _qtyEditor = null;
        }
        _referenceHighlights?.Clear();
    }
}
