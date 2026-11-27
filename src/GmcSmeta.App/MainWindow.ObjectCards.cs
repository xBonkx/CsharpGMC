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
    private readonly Dictionary<(int ObjectId, NavigationMode Mode), ObjectDocuments> _objectCardCache = new();
    private string? _objectCardCacheRevision;
    private int? _displayedObjectId;
    private bool _objectCardTransitionPending;

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
        CancelEstimateCacheValidation();
        _objectCardTransitionPending = true;
        PageHost.IsEnabled = false;

        // Never execute contract scans, totals, finance and card projections
        // on the WPF UI thread. Keep input order and current navigation mode
        // stable while the background read runs on its own SQL connection.
        var request = ++_objectCardLoadRequest;
        var documentsSnapshot = _docs.ToArray();
        var mode = CurrentMode;
        var navigationGeneration = _estimateOpenGeneration;
        bool IsCurrent() => !_startupClosed &&
            request == _objectCardLoadRequest && navigationGeneration == _estimateOpenGeneration &&
            mode == CurrentMode && connectionString == _sqlConnectionString &&
            _objectCardTransitionPending && _selectedObject?.Id == o.Id;
        StatusText.Text = $"{o.Name} · загрузка карточек SQL Server…";
        var cacheKey = (o.Id, mode);
        var cached = _objectCardCache.GetValueOrDefault(cacheKey);
        var cachedRevision = _objectCardCacheRevision;
        (ObjectDocuments Documents, string Revision) loaded;
        try
        {
            loaded = await Task.Run(() =>
            {
                using var read = SqlServerDatabase.Open(connectionString);
                var before = SqlServerDatabase.Revision(read);
                if (string.IsNullOrWhiteSpace(before) ||
                    !Workspace.ObjectCardMetadataMatches(read, o, documentsSnapshot))
                    throw new InvalidOperationException("Состав документов или реквизиты объекта изменились. Нажмите F5.");
                var result = cached is not null && cachedRevision == before
                    ? cached
                    : Workspace.ReadObjectDocuments(read, o, documentsSnapshot, mode);
                if (before != SqlServerDatabase.Revision(read))
                    throw new InvalidOperationException("Данные изменились во время загрузки карточек. Нажмите F5.");
                return (result, before);
            });
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                // An error belongs to the requested object, never to the retained
                // previous page. Do not leave stale document actions accessible.
                PrepareObjectHeader(o, mode);
                DocumentCards.ItemsSource = null;
                ObjectTotals.DataContext = null;
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
        // A different object/estimate may have been selected before SQL replied.
        if (!IsCurrent()) return;
        if (_objectCardCacheRevision != loaded.Revision || _objectCardCache.Count >= 24)
            _objectCardCache.Clear();
        _objectCardCacheRevision = loaded.Revision;
        var documents = loaded.Documents;
        _objectCardCache[cacheKey] = documents;
        // Prepare the header, totals and cards on this dispatcher turn, with
        // no await or visibility gap before publication.
        var resetScroll = !ReferenceEquals(PageHost.Content, ObjectPage) || _displayedObjectId != o.Id;
        PrepareObjectHeader(o, mode, documents.AgreementHeading);
        ObjectTotals.DataContext = documents;
        _objectTileLayout = null;
        ObjectProgressCard.Visibility = documents.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        // The final tile is the real Python "Новое КП" action. Financial
        // totals and document count intentionally use documents.Cards only.
        DocumentCards.ItemsSource = ObjectCardLayout.AppendNewContract(
            documents.Cards, SqlWritesEnabled && !o.Archived,
            mode == NavigationMode.Kp ? "Новое КП" : "Добавить документ");
        ObjectCardsStatus.Visibility = Visibility.Collapsed;
        ObjectTotals.Visibility = ObjectCardsScroll.Visibility = Visibility.Visible;
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
