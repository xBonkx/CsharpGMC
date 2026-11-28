using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private readonly Dictionary<NavigationMode, HashSet<string>> _knownNavigationNodes = new();
    private Dictionary<int, NavigationObject> _navigationGroups = new();

    private void RefreshNavigation()
    {
        using var treeTiming = StartupTrace.Measure("navigation_tree_rebuild");
        UpdateNavigationCaptions();
        _navigationSearchTimer?.Stop();
        RestoreNavigationSearchExpansion();
        var mode = CurrentMode;
        var sameMode = _shownMode == mode;
        if (_shownMode is { } previous)
        {
            if (!_expanded.TryGetValue(previous, out var savedOpen))
                _expanded[previous] = savedOpen = new HashSet<string>();
            if (!_knownNavigationNodes.TryGetValue(previous, out var savedKnown))
                _knownNavigationNodes[previous] = savedKnown = new HashSet<string>();
            // Keep the remembered state of branches that have not yet been
            // materialized in this mode. Update only actually observed nodes.
            foreach (var node in Descendants(NavigationTree.Items))
            {
                var key = NodeKey(node);
                savedKnown.Add(key);
                if (node.IsExpanded) savedOpen.Add(key);
                else savedOpen.Remove(key);
            }
        }

        IReadOnlyList<NavigationYear> years;
        using (StartupTrace.Measure("navigation_tree_projection"))
            years = Navigation.Years(_objects, _docs, mode, DateTime.Today.Year);
        using (StartupTrace.Measure("navigation_search_index"))
            _navigationSearchIndex = new NavigationSearchIndex(years.SelectMany(y => y.Objects));
        _expanded.TryGetValue(mode, out var opened);
        _knownNavigationNodes.TryGetValue(mode, out var known);
        var visibleKeys = new HashSet<string>(years.Select(y => "year:" + y.Year));
        foreach (var group in years.SelectMany(y => y.Objects))
        {
            visibleKeys.Add("obj:" + group.Object.Id);
            foreach (var doc in group.Documents) visibleKeys.Add("doc:" + doc.Id);
        }
        opened?.IntersectWith(visibleKeys);
        known?.IntersectWith(visibleKeys);

        var selectedKey = sameMode && NavigationTree.SelectedItem is TreeViewItem selected
            ? NodeKey(selected) : null;
        var oldYears = sameMode
            ? NavigationTree.Items.OfType<TreeViewItem>().ToDictionary(n => (string)n.Tag)
            : new Dictionary<string, TreeViewItem>();
        var oldObjects = new Dictionary<int, TreeViewItem>(_objectNodes);
        var oldPending = new Dictionary<int, PendingNavigationDocuments>(_pendingNavigationDocuments);
        var oldGroups = _navigationGroups;
        _navigationGroups = new Dictionary<int, NavigationObject>();
        _documentNodes.Clear();
        _objectNodes.Clear();
        _pendingNavigationDocuments.Clear();

        var wasSyncing = _syncingNavigation;
        _syncingNavigation = true;
        var reused = 0;
        var rebuilt = 0;
        try
        {
            var desiredYears = new List<TreeViewItem>();
            foreach (var year in years)
            {
                var yearNode = oldYears.GetValueOrDefault(year.Year) ?? new TreeViewItem
                {
                    Header = year.Year, Tag = year.Year, FontWeight = FontWeights.SemiBold,
                    Padding = NavigationRowPadding(0),
                    HeaderTemplate = (DataTemplate)FindResource("YearNavigationTemplate"),
                    IsExpanded = NavigationReusePolicy.Expanded("year:" + year.Year,
                        mode == NavigationMode.Archive || year == years[0], opened, known)
                };
                var desiredObjects = new List<TreeViewItem>();
                foreach (var group in year.Objects)
                {
                    var id = group.Object.Id;
                    _navigationGroups.Add(id, group);
                    TreeViewItem node;
                    if (sameMode && oldObjects.TryGetValue(id, out var existing) &&
                        yearNode.Items.Contains(existing) && oldGroups.TryGetValue(id, out var oldGroup) &&
                        NavigationReusePolicy.SameBranch(oldGroup, group))
                    {
                        node = existing;
                        node.Tag = group.Object;
                        reused++;
                        if (oldPending.TryGetValue(id, out _))
                            _pendingNavigationDocuments[id] = new(group.Documents, opened, known);
                        else
                            foreach (var child in Descendants(node.Items))
                                if (child.Tag is DocRow doc) _documentNodes.Add(doc.Id, child);
                    }
                    else
                    {
                        rebuilt++;
                        node = new TreeViewItem
                        {
                            Header = group.Object.Name, Tag = group.Object,
                            Padding = NavigationRowPadding(1),
                            HeaderTemplate = (DataTemplate)FindResource("ObjectNavigationTemplate"),
                            FontWeight = FontWeights.SemiBold, ToolTip = group.Object.Name
                        };
                        if (group.Documents.Count > 0)
                        {
                            _pendingNavigationDocuments[id] = new(group.Documents, opened, known);
                            node.Items.Add(new TreeViewItem
                            {
                                Tag = NavigationPlaceholder, Visibility = Visibility.Collapsed,
                                Focusable = false, IsEnabled = false
                            });
                            node.Expanded += ObjectNavigation_Expanded;
                        }
                        // Assign after publication in _objectNodes below.
                    }
                    _objectNodes.Add(id, node);
                    desiredObjects.Add(node);
                }
                ReconcileNavigationItems(yearNode.Items, desiredObjects);
                foreach (var node in desiredObjects)
                {
                    if (node.Tag is not ObjectRow obj) continue;
                    if (!oldObjects.TryGetValue(obj.Id, out var oldNode) || !ReferenceEquals(oldNode, node))
                        node.IsExpanded = NavigationReusePolicy.Expanded(NodeKey(node), false, opened, known);
                    if (node.IsExpanded) EnsureObjectDocuments(node);
                }
                desiredYears.Add(yearNode);
            }
            ReconcileNavigationItems(NavigationTree.Items, desiredYears);
            foreach (var oldNode in oldObjects.Values)
                if (oldNode.Tag is ObjectRow obj &&
                    (!_objectNodes.TryGetValue(obj.Id, out var current) || !ReferenceEquals(oldNode, current)))
                    oldNode.Expanded -= ObjectNavigation_Expanded;

            _shownMode = mode;
            ApplyNavigationFilter();
            if (selectedKey is not null && visibleKeys.Contains(selectedKey))
            {
                TreeViewItem? target = null;
                if (selectedKey.StartsWith("obj:", StringComparison.Ordinal) &&
                    int.TryParse(selectedKey.AsSpan(4), out var objectId))
                    _objectNodes.TryGetValue(objectId, out target);
                else if (selectedKey.StartsWith("doc:", StringComparison.Ordinal) &&
                    int.TryParse(selectedKey.AsSpan(4), out var docId))
                {
                    if (!_documentNodes.TryGetValue(docId, out target) &&
                        _docs.FirstOrDefault(d => d.Id == docId) is { } doc &&
                        _objectNodes.TryGetValue(doc.ObjectId, out var parent))
                    {
                        EnsureObjectDocuments(parent);
                        _documentNodes.TryGetValue(docId, out target);
                    }
                }
                else if (selectedKey.StartsWith("year:", StringComparison.Ordinal))
                    target = desiredYears.FirstOrDefault(n => NodeKey(n) == selectedKey);
                if (target is not null && !target.IsSelected) target.IsSelected = true;
            }
        }
        finally { _syncingNavigation = wasSyncing; }
        StartupTrace.Sample("navigation_tree_nodes", _objectNodes.Count, _documentNodes.Count);
        StartupTrace.Sample("navigation_tree_branch_reuse", reused, rebuilt);
    }

    private static void ReconcileNavigationItems(ItemCollection items, IReadOnlyList<TreeViewItem> desired)
    {
        var retained = desired.ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is not TreeViewItem node || !retained.Contains(node)) items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var node = desired[i];
            if (i < items.Count && ReferenceEquals(items[i], node)) continue;
            var oldIndex = items.IndexOf(node);
            if (oldIndex >= 0) items.RemoveAt(oldIndex);
            items.Insert(i, node);
        }
    }

    private void ObjectNavigation_Expanded(object sender, RoutedEventArgs e)
    {
        // Expanded is a bubbling event; child document expansion is unrelated.
        if (sender is TreeViewItem node && ReferenceEquals(e.OriginalSource, node))
            EnsureObjectDocuments(node);
    }

    private void EnsureObjectDocuments(TreeViewItem node)
    {
        if (node.Tag is not ObjectRow obj ||
            !_pendingNavigationDocuments.TryGetValue(obj.Id, out var pending) ||
            !_objectNodes.TryGetValue(obj.Id, out var current) || !ReferenceEquals(node, current))
            return;

        // Build locally before replacing the placeholder. The existing
        // Navigation.Documents projection retains stage hierarchy and order.
        using var timing = StartupTrace.Measure("navigation_lazy_documents");
        var opened = pending.Opened;
        var shortTitle = CurrentMode != NavigationMode.Kp &&
            AgreementPresentation.Resolve(pending.Documents) is not null;
        var children = Navigation.Documents(pending.Documents)
            .Select(doc => DocumentNode(doc, opened, pending.Known, 2, shortTitle)).ToList();
        _pendingNavigationDocuments.Remove(obj.Id);
        node.Expanded -= ObjectNavigation_Expanded;
        node.Items.Clear();
        foreach (var child in children) node.Items.Add(child);
    }

    private TreeViewItem DocumentNode(NavigationDocument entry, HashSet<string>? opened, HashSet<string>? known, int depth, bool shortTitle = false)
    {
        var header = new NavigationDocumentHeader(entry.Document, depth > 2, entry.Children.Count > 0)
            { ShortAgreementTitle = shortTitle };
        var node = new TreeViewItem
        {
            Header = header, Tag = entry.Document,
            Padding = NavigationRowPadding(depth),
            HeaderTemplate = (DataTemplate)FindResource("DocumentNavigationTemplate"),
            ToolTip = entry.Document.NavigationTip,
            FontStyle = header.IsItalic ? FontStyles.Italic : FontStyles.Normal
        };
        System.Windows.Automation.AutomationProperties.SetName(node, header.DisplayName);
        node.IsExpanded = NavigationReusePolicy.Expanded(NodeKey(node), entry.Children.Count > 0, opened, known);
        _documentNodes[entry.Document.Id] = node;
        // A collapsed object's documents may first be created by search.
        // Capture their initial state before any search-time user expansion.
        _searchExpansionSnapshot?.TryAdd(node, node.IsExpanded);
        foreach (var child in entry.Children) node.Items.Add(DocumentNode(child, opened, known, depth + 1, shortTitle));
        return node;
    }

}
