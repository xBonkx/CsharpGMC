using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Document-wide actions, grouped as Python kp_editor.btn_more.</summary>
public partial class EstimateView
{
    private ContextMenu? _documentActionsMenu;

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (_documentActionsMenu is { IsOpen: true } open)
        {
            open.IsOpen = false;
            return;
        }
        if (sender is not Button button || _estimate is not { } snapshot ||
            Window.GetWindow(this) is not MainWindow main || _detached) return;

        var revision = _serverRevision;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        var primary = EstimateGrid.SelectedItem;
        var stageMode = _inlineStagesOn;
        bool Current() => !_detached && IsLoaded && IsEnabled &&
            ReferenceEquals(snapshot, _estimate) && revision == _serverRevision &&
            main.IsCurrentEstimatePage(_contractId, _objectId, this);
        bool Settled() => Current() && CanTransferDocument && !_serverConflict &&
            !string.IsNullOrWhiteSpace(revision) && revision == _publishedEstimateRevision;
        bool Write(bool allowLocked) => Current() && CanChangeHeader(snapshot, revision, allowLocked);
        bool SelectionCurrent() => ReferenceEquals(primary, EstimateGrid.SelectedItem) &&
            EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
                .SequenceEqual(selection, ReferenceEqualityComparer.Instance) &&
            selection.All(row => !row.IsVirtual && _rows.Contains(row));
        var ordinary = !_doc.IsStage && !_doc.StageRest;
        var kp = ordinary && _isKp;
        var annex = ordinary && _doc.Kind == "main";
        var workRows = selection.Any(row => !row.IsSystem && !row.IsVirtual &&
            row.Id > 0 && row.Source.ParentPositionId is null);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };

        void Group(string name)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = name, IsEnabled = false });
        }
        void Add(string caption, RoutedEventHandler handler, string? glyph,
            Func<bool> allowed, bool visible = true, bool danger = false)
        {
            if (!visible) return;
            var item = glyph is null ? new MenuItem { Header = caption }
                : ContextMenuIcons.Create(caption, glyph, danger: danger);
            item.IsEnabled = allowed();
            item.Click += (s, args) =>
            {
                if (!item.IsEnabled || !allowed())
                {
                    SetStatus("Документ или выбор изменились. Откройте меню снова.", true);
                    return;
                }
                menu.IsOpen = false;
                handler(s, args);
            };
            menu.Items.Add(item);
        }

        Group("Документ");
        Add("Обновить из SQL Server", RefreshNow_Click, "Refresh", Current);
        Add("Свойства: номер, дата, организация, НДС…", EditTitle_Click, "Edit", () => ordinary && Write(false));
        Add(snapshot.Locked ? "Снять блокировку сметы" : "Заблокировать — смета подписана",
            LockToggle_Click, "Check", () => Write(true));
        Add("Создать копию КП (вариант)…", CopyContract_Click, "Duplicate", () => Write(true), kp);
        Add("Оформить как приложение (закупки, выдача, КС)…", MakeContract_Click, "Check", () => Write(false), kp);
        Add("Создать допсоглашение к этому приложению…", CreateDop_Click, "FilePlus", () => Write(true), annex);
        Add("Перенести в другой договор / группу…", MoveContract_Click, null, () => Write(true), annex);
        Add("Этапы — вычесть приложения из КП (режим вкл / выкл)", ToggleInlineStages_Click, "Layers",
            () => Settled() && !_inlineStageCommandBusy && _inlineStagesOn == stageMode, kp);
        Add("Сравнить с другим документом…", CompareDocs_Click, "List", Settled);
        Group("Цены");
        Add("Скопировать расценки", TransferRates_Click, "Download", () => Write(false), kp);
        Add("Скидка на всю смету…", Discount_Click, "Download", () => ordinary && Write(false));
        Add("Цена работы для выделенных строк…", SetWork_Click, "Edit",
            () => ordinary && Write(false) && snapshot.SmrMode == "pos" && workRows && SelectionCurrent());
        Add("Цены работ из справочника (где пусто)", FillWork_Click, "Book", () => ordinary && Write(false));
        Group("Изменения проекта (спецификация)");
        Add("Обновить из новой спецификации (изм.)…", UpdateSpec_Click, "Upload", () => kp && Write(false));
        Add("Что менялось по изм. — отчёты…", SpecHistory_Click, "History", Current);
        Add("Свернуть воздуховоды и изоляцию в м² — по системам", CollapseAreaSystem_Click, "Layers",
            () => ordinary && Write(false));
        Add("Свернуть в м² — одной группой на всё КП", CollapseAreaWhole_Click, "Layers",
            () => ordinary && Write(false));
        Add("Развернуть м² обратно в строки", ExpandAreaAll_Click, "Undo", () => ordinary && Write(false));
        Group("Объект");
        Add("Список людей на допуск…", ShowPeople_Click, "Users", () => Write(true));
        Add("Приказы об ответственных…", ShowOrders_Click, "File", () => Write(true));
        Group("Прочее");
        Add("История изменений документа…", ShowHistory_Click, "History", Current);
        Add("Сохранить ссылку на расчёт в папку…", SaveDocumentLink_Click, "Folder", Settled);
        Add("Пересортировать строки КП (запись)…", Sort_Click, "Sort",
            () => ordinary && Write(false) && primary is EditableEstimateRow && SelectionCurrent());
        menu.Items.Add(new Separator());
        Add("Удалить документ…", DeleteContract_Click, "Trash", () => ordinary && Write(true), danger: true);

        _documentActionsMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_documentActionsMenu, menu)) _documentActionsMenu = null;
        };
        menu.IsOpen = true;
    }
}
