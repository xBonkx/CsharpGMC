namespace GmcSmeta.Core.Domain;

/// <summary>UI-owned bounded cache. A caller must confirm the database revision
/// and metadata before using a candidate or storing an accepted snapshot.</summary>
public sealed class ObjectCardSnapshotCache
{
    public sealed record Entry(string Revision, ObjectDocuments Documents);
    private const int Capacity = 24;
    private readonly Dictionary<(int ObjectId, NavigationMode Mode), Entry> _entries = new();
    private readonly LinkedList<(int ObjectId, NavigationMode Mode)> _order = new();
    private string? _profile;
    private string? _revision;

    public Entry? Find(string profile, int objectId, NavigationMode mode) =>
        string.Equals(_profile, profile, StringComparison.Ordinal)
            ? _entries.GetValueOrDefault((objectId, mode)) : null;

    public void Store(string profile, int objectId, NavigationMode mode,
        string revision, ObjectDocuments documents)
    {
        if (string.IsNullOrWhiteSpace(revision))
            throw new ArgumentException("Snapshot revision is required.", nameof(revision));
        if (!string.Equals(_profile, profile, StringComparison.Ordinal) || _revision != revision)
        {
            _entries.Clear();
            _order.Clear();
            _profile = profile;
            _revision = revision;
        }

        var key = (objectId, mode);
        _entries[key] = new Entry(revision, documents);
        _order.Remove(key);
        _order.AddFirst(key);
        if (_order.Count > Capacity)
        {
            var oldest = _order.Last!.Value;
            _order.RemoveLast();
            _entries.Remove(oldest);
        }
    }
}
