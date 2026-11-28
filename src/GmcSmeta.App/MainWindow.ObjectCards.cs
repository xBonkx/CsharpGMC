using System.Threading;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // UI-only sequence; superseded object-card results never replace the
    // currently open page when a user navigates quickly.
    private int _objectCardLoadRequest;
    private readonly ObjectCardSnapshotCache _objectCardCache = new();
    private readonly SemaphoreSlim _objectCardReadGate = new(1, 1);
    private CancellationTokenSource? _objectCardReadCancellation;
    private int? _displayedObjectId;
    private bool _objectCardTransitionPending;
    private IReadOnlyList<object> _objectCardItems = Array.Empty<object>();
    private int _objectCardPage;
    private bool? _objectTotalsUserVisible;

    internal void PresentObjectCardItems(IReadOnlyList<object> items, int requestedPage, bool resetScroll)
    {
        _objectCardItems = items;
        PublishObjectCardPage(requestedPage, resetScroll);
    }

    private void PublishObjectCardPage(int requestedPage, bool resetScroll)
    {
        var range = ObjectCardLayout.PageRange(_objectCardItems.Count, requestedPage);
        _objectCardPage = range.Page;
        DocumentCards.ItemsSource = _objectCardItems.Skip(range.First).Take(range.Count).ToArray();
        ObjectCardPager.Visibility = range.Pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        ObjectCardsPrevious.IsEnabled = range.Page > 0;
        ObjectCardsNext.IsEnabled = range.Page + 1 < range.Pages;
        ObjectCardsPageText.Text = $"{range.Page + 1} / {range.Pages}";
        if (resetScroll) ObjectCardsScroll.ScrollToTop();
    }

    private void ObjectCardsPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!_objectCardTransitionPending && ReferenceEquals(PageHost.Content, ObjectPage) && _selectedObject?.Id == _displayedObjectId && _displayedObjectId is not null) PublishObjectCardPage(_objectCardPage - 1, true);
    }

    private void ObjectCardsNext_Click(object sender, RoutedEventArgs e)
    {
        if (!_objectCardTransitionPending && ReferenceEquals(PageHost.Content, ObjectPage) && _selectedObject?.Id == _displayedObjectId && _displayedObjectId is not null) PublishObjectCardPage(_objectCardPage + 1, true);
    }

    private void ObjectPage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateObjectTotalsVisibility();

    private void UpdateObjectTotalsVisibility()
    {
        if (ObjectTotals is null || ObjectTotalsToggle is null || ObjectPage is null) return;
        var available = ObjectTotals.DataContext is ObjectDocuments;
        ObjectTotalsToggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        // On a short viewport keep document cards usable. The user's explicit
        // choice takes priority for the current page; resizing never changes it.
        var show = available && (_objectTotalsUserVisible ?? (ObjectPage.ActualHeight <= 0 || ObjectPage.ActualHeight >= 560));
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ObjectTotals.Visibility != visibility) ObjectTotals.Visibility = visibility;
        ObjectTotalsToggle.Content = show ? "Скрыть итоги" : "Показать итоги";
    }

    private void ObjectTotalsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_objectCardTransitionPending) return;
        _objectTotalsUserVisible = ObjectTotals.Visibility != Visibility.Visible;
        UpdateObjectTotalsVisibility();
    }

    private void CancelObjectCardRead() => _objectCardReadCancellation?.Cancel();

    private async void ShowObject(ObjectRow o)
    {
        if (_startupClosed || _serverConn is null || _sqlConnectionString is not { } connectionString) return;
        ActivateDocumentsWorkspace();

        // Только команды, перенесённые на SQL Server; вид страницы объекта — как в Python.
        if (ObjectRenameBox.Visibility == Visibility.Visible) EndPageRename();
        // Keep the previous page intact until the new snapshot is ready.
        // Its commands must not target the newly selected object meanwhile.
        _selectedObject = o;
        _openDocumentId = null;
        ++_estimateOpenGeneration;
        CancelNavigationRefresh();
        CancelEstimateCacheValidation();
        _objectCardTransitionPending = true;
        PageHost.IsEnabled = false;

        // Never execute contract scans, totals, finance and card projections
        // on the WPF UI thread. Keep input order and current navigation mode
        // stable while the background read runs on its own SQL connection.
        CancelObjectCardRead();
        using var cancellation = new CancellationTokenSource();
        _objectCardReadCancellation = cancellation;
        var token = cancellation.Token;
        using var operationTiming = StartupTrace.BeginOperation();
        var request = ++_objectCardLoadRequest;
        // Only this object's documents are used by both scoped readers.
        var documentsSnapshot = _docs.Where(d => d.ObjectId == o.Id).ToArray();
        var mode = CurrentMode;
        var navigationGeneration = _estimateOpenGeneration;
        bool IsCurrent() => !_startupClosed &&
            request == _objectCardLoadRequest && navigationGeneration == _estimateOpenGeneration &&
            mode == CurrentMode && connectionString == _sqlConnectionString &&
            _objectCardTransitionPending && _selectedObject?.Id == o.Id;
        StatusText.Text = $"{o.Name} · загрузка карточек SQL Server…";
        var cached = _objectCardCache.Find(connectionString, o.Id, mode);
        (ObjectDocuments Documents, string Revision) loaded;
        var entered = false;
        try
        {
            using var waitTiming = StartupTrace.MeasureElapsed("object_cards_read_gate");
            await _objectCardReadGate.WaitAsync(token);
            waitTiming?.Dispose();
            entered = true;
            if (!IsCurrent() || token.IsCancellationRequested) return;
            loaded = await Task.Run(async () =>
            {
                using var read = await SqlServerDatabase.OpenAsync(connectionString, token).ConfigureAwait(false);
                var before = await SqlServerDatabase.RevisionAsync(read, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(before) ||
                    !Workspace.ObjectCardMetadataMatches(read, o, documentsSnapshot, token))
                    throw new InvalidOperationException("Состав документов или реквизиты объекта изменились. Нажмите F5.");
                token.ThrowIfCancellationRequested();
                var result = cached is not null && cached.Revision == before
                    ? cached.Documents
                    : Workspace.ReadObjectDocuments(read, o, documentsSnapshot, mode, token);
                token.ThrowIfCancellationRequested();
                if (before != await SqlServerDatabase.RevisionAsync(read, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Данные изменились во время загрузки карточек. Нажмите F5.");
                return (result, before);
            }, token);
        }
        catch (Exception) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                // An error belongs to the requested object, never to the retained
                // previous page. Do not leave stale document actions accessible.
                PrepareObjectHeader(o, mode);
                _objectCardItems = Array.Empty<object>();
                PublishObjectCardPage(0, true);
                ObjectTotals.DataContext = null;
                UpdateObjectTotalsVisibility();
                ObjectTotals.Visibility = ObjectCardsScroll.Visibility = Visibility.Collapsed;
                ObjectCardsStatus.Text = "Не удалось загрузить документы. Нажмите F5, чтобы повторить.";
                ObjectCardsStatus.Visibility = Visibility.Visible;
                _displayedObjectId = null;
                ShowPage(ObjectPage);
                StatusText.Text = "Не удалось загрузить документы: " + ex.Message;
                LogError(ex);
            }
            return;
        }
        finally
        {
            if (entered) _objectCardReadGate.Release();
            if (ReferenceEquals(_objectCardReadCancellation, cancellation)) _objectCardReadCancellation = null;
        }
        // A different object/estimate may have been selected before SQL replied.
        if (!IsCurrent()) return;
        using var bindTiming = StartupTrace.Measure("object_cards_bind");
        var documents = loaded.Documents;
        _objectCardCache.Store(connectionString, o.Id, mode, loaded.Revision, documents);
        // Prepare the header, totals and cards on this dispatcher turn, with
        // no await or visibility gap before publication.
        var resetScroll = !ReferenceEquals(PageHost.Content, ObjectPage) || _displayedObjectId != o.Id;
        PrepareObjectHeader(o, mode, documents.AgreementHeading);
        ObjectTotals.DataContext = documents;
        _objectTileLayout = null;
        ObjectProgressCard.Visibility = documents.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        // The final tile is the real Python "Новое КП" action. Financial
        // totals and document count intentionally use documents.Cards only.
        var items = ObjectCardLayout.AppendNewContract(
            documents.Cards, SqlWritesEnabled && !o.Archived,
            mode == NavigationMode.Kp ? "Новое КП" : "Добавить документ");
        if (resetScroll) _objectTotalsUserVisible = null;
        PresentObjectCardItems(items, resetScroll ? 0 : _objectCardPage, resetScroll);
        ObjectCardsStatus.Visibility = Visibility.Collapsed;
        ObjectCardsScroll.Visibility = Visibility.Visible;
        UpdateObjectTotalsVisibility();
        ObjectTotalValue.Text = Money.Format(documents.Total);
        ObjectTotalSub.Visibility = documents.VatTop > .005m ? Visibility.Visible : Visibility.Collapsed;
        ObjectTotalSub.Text = $"материалы + СМР {Money.Format(documents.Total - documents.VatTop)} + НДС сверху {Money.Format(documents.VatTop)}";
        ObjectMaterialsValue.Text = Money.Format(documents.Materials);
        ObjectSmrValue.Text = Money.Format(documents.Smr);
        ObjectMaterialsCaption.Text = documents.VatTop > .005m ? "Материалы (без НДС)" : "Материалы";
        ObjectSmrCaption.Text = documents.VatTop > .005m ? "СМР (без НДС)" : "СМР";
        _displayedObjectId = o.Id;
        ShowPage(ObjectPage);
        if (resetScroll) ObjectCardsScroll.ScrollToTop();
        ResizeCards();
        StatusText.Text = $"{o.Name}  ·  документов: {documents.Cards.Count}  ·  SQL Server";
    }

    private void PrepareObjectHeader(ObjectRow o, NavigationMode mode, string? agreementHeading = null)
    {
        var truncated = o.Name.Length > 110;
        var name = truncated ? o.Name[..105].TrimEnd(' ', ',', '.') + "…" : o.Name;
        ObjectTitle.Text = name + (o.Archived ? "   (архив" + (o.ArchivedOn.Length >= 4 ? " " + o.ArchivedOn[..4] : "") + ")" : "");
        var singleAgreement = mode != NavigationMode.Kp && !string.IsNullOrWhiteSpace(agreementHeading);
        ObjectCrumb.Text = mode == NavigationMode.Kp ? "КП" : singleAgreement ? "Договор" : "Группа документов";
        ObjectTotalCaption.Text = mode == NavigationMode.Kp ? "Итого по КП" : singleAgreement ? "Итого по договору" : "Итого по группе";
        ObjectDocumentsCaption.Text = mode == NavigationMode.Kp ? "КОММЕРЧЕСКИЕ ПРЕДЛОЖЕНИЯ" :
            mode == NavigationMode.Archive ? "ДОКУМЕНТЫ" : "ПРИЛОЖЕНИЯ И ДС";
        ObjectToolsCaption.Text = singleAgreement ? "Документы договора" : "Документы группы";
        ObjectNewKpButton.Content = mode == NavigationMode.Kp ? "Новое КП" : "Добавить документ";
        ObjectNewKpButton.ToolTip = mode == NavigationMode.Kp ? "Создать КП: пустое или копию существующего" :
            "Создать приложение, ДС или КП";
        ObjectTitle.ToolTip = truncated ? o.Name : "Двойной щелчок — переименовать карточку";
        ObjectNewKpButton.Visibility = ObjectPropertiesButton.Visibility =
            ObjectMoreButton.Visibility = ObjectRenameButton.Visibility = ObjectCrumb.Visibility = Visibility.Visible;
        ObjectNewKpButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectPropertiesButton.Content = mode == NavigationMode.Kp ? "Свойства" : "Реквизиты";
        ObjectPropertiesButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectMoreButton.IsEnabled = SqlWritesEnabled;
        ObjectRenameButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectTitle.TextWrapping = TextWrapping.Wrap;
        var parts = new[] { agreementHeading ?? "", o.Customer.Length > 0 ? "заказчик: " + o.Customer : "", o.Address,
            o.Folder.Length > 0 ? "папка: " + o.Folder : "" }.Where(p => p.Length > 0).ToList();
        if (mode == NavigationMode.Contracts && !singleAgreement)
            parts.Insert(0, "Реквизиты договора не определены однозначно");
        ObjectSubtitle.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Заказчик, адрес и папку документов можно указать в «Свойствах».";
        ObjectSubtitle.TextWrapping = TextWrapping.Wrap;
        ObjectDocumentTools.Visibility = mode == NavigationMode.Contracts ? Visibility.Visible : Visibility.Collapsed;
    }
}
