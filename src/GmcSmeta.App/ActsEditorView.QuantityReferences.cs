using System.Collections.ObjectModel;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private static readonly IReadOnlyDictionary<long, string> EmptyRefNumbers =
        new ReadOnlyDictionary<long, string>(new Dictionary<long, string>());
    private IReadOnlyDictionary<long, string>? _indexedRefNumbers;
    private ActQuantityReferenceIndex? _quantityReferenceIndex;

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
}
