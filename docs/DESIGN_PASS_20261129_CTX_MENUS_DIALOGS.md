# 29.11.2026 — дизайн-проход 2: CTX-меню/диалоги, AGR-02, ARCH-SIZE (zcode-sess-design)

Бронь: 508b323c (9 ID: CTX-036/038/039/040/042/046.01, AGR-02.01, ARCH-SIZE-03.04/06.01). База: 60829799. **XAML-правок в этом проходе нет** — все закрываемые ID закрыты сверкой уже реализованного с доказательствами; две декомпозиции — объективный блокер. Python-эталона (`xBonkx/gmcsmeta`) локально нет — иконные CTX-задачи (037.01/037.02, 014.02) не брались: точный паритет иконок без свежего `theme.py` запрещён правилом владельца от 25.11. Рекомендация: клонировать эталон в следующем проходе.

## CODE READY — сверка уже реализованного (в минимум «новых» не входят)

| ID | Доказательство (текущий main) |
|---|---|
| CTX-036.01 | App.xaml:302–313 `MenuPopupChrome`: radius 10, padding 6, border #E4E8EF, тень 18/3/270/.18 с компенсацией Margin 10,10,10,14; пункты Padding 12,7,12,7 (App.xaml:434), колонка иконки/стрелка — записанная композиция прохода dc85096/5bf68da; Python-метрики зафиксированы в CONTEXT_MENU_TODO (aa6b34d). Физика рендера — A CTX-036.02 |
| CTX-038.01 | Локальные красные `Foreground` на MenuItem отсутствуют (rg = 0); danger — красный значок, не текст (fb4bb4f/981dc25). Исключения записаны: пункты ContextMenu без иконок до CTX-037.02; красные заголовки диалогов (#DC2626: ConfirmDeleteDialog:18, DeletePositionsDialog:98, ObjectDialog:40) — семантические предупреждения, отдельная роль |
| CTX-039.01 | App.xaml:316–338 ContextMenu: offsets −10/−6, ItemsHost в ScrollViewer, MenuPopupChrome-обёртка; check/icon взаимоисключение и shortcut-роли в шаблоне пункта (запись 27.11). Попадание курсором через gap и края экрана — A-приёмка |
| CTX-040.01 | Единый стиль: `{x:Static MenuItem.SeparatorStyleKey}` (App.xaml:442–450) и `MenuSeparator` (App.xaml:453–460) — идентичные шаблоны 1px #E4E8EF margin 8,5 (Python QMenu::separator). Использования: ActsEditorView:120–126 (между постоянными пунктами; ActMoreMenu_Opened переключает только IsEnabled — висячих нет), MainWindow:488 (PtoModeToggle всегда видим, Collapsed-пункт после него без разделителя), KsRequisitesWindow — разделители форм с LayoutFormGroupSeparatorGap, отдельная роль (обоснование в LayoutMetrics). Двойных/висячих не найдено |
| CTX-042.01 | MainWindow.xaml:94–100 `SidebarMenuHeader` BasedOn `{x:Type MenuItem}` — наследует единый шаблон пункта; меню «Действия» сметы использует те же карточные метрики и красный значок только у удаления (f31c9d2, cf2167a, e538654, 007a3e8). Сверено на текущем main |
| CTX-046.01 | Все реальные Window имеют `WindowStartupLocation="CenterOwner"` (проверено циклом по 46 XAML; UserControls/панели не в счёте). Owner устанавливается кодом (ActsEditorView*, AllContractActsWindow и др.). Min-размеры у всех окон; корневые поля через LayoutFormDialogMargin/LayoutDialogMargin/LayoutCompareDialogMargin; scroll и перенос длинного текста (UiSecondaryText/TextWrapping) присутствуют. Исключение: окно PeopleWindow собирается в .cs (PeopleWindow.cs) без явного StartupLocation — вне XAML-линии, записано в DESIGN_NOTES |
| AGR-02.01 | MainWindow.xaml: «Новый договор» ×2, «Поиск договора» ×2; старых «Новый объект»/«Поиск объекта»/«Итого по объекту» нет. Карточные подписи «Приложения и ДС»/«Документы договора» — MainWindow.ObjectCards.cs (2 соответствия, режимные подписи прохода 27.11 base 11cfe3d). Режим КП/архива различается — сверка подписей подтверждена |

## BLOCKED — декомпозиция XAML

| ID | Причина |
|---|---|
| ARCH-SIZE-03.04 | Source-тесты парсят `EstimateView.xaml` и утверждают определения ресурсов **на месте**: EstimateGridGeometrySourceTests (`Style("EstimateCell")`, `Style("EstimateRow")`, `Style("DocumentNameDisplay")`, `Assert.Single`), EstimateMoneyCardSourceTests, EstimateFooterSourceTests, EstimateHeaderCountSourceTests (x:Name-инварианты). Перенос UserControl.Resources (EstimateView.xaml:13–262) в отдельный словарь ломает `Assert.Single`/инварианты. Обновление тестов — `*.cs`, вне дизайн-линии (правило lane). Запрос: координированный сплит + правка тестов одним коммитом основного агента |
| ARCH-SIZE-06.01 | То же для App.xaml (1773 строки): ButtonFocusVisualSourceTests и другие source-тесты загружают App.xaml напрямую и требуют ключи стилей (`DangerButton`, `PrimaryButton`, `PythonTabAction`…) в этом файле. Сплит по тематическим словарям без координированного обновления тестов невозможен. Запрос аналогичный |

## Недостача по минимуму «5 новых»

Новых реализаций в этом проходе: **0** (все 7 закрытий — сверка ранее реализованного, что по правилу владельца в минимум не засчитывается; CTX-046.01 — новая только верификация, правок XAML не потребовалось). Причины — объективные: (1) иконные CTX-037.01/037.02, CTX-014.02 — нет локального Python-эталона (запрещено делать без него); (2) ARCH-SIZE-03.04/06.01 — контракты source-тестов вне моей линии; (3) CTX-045.02 требует парного реестра вкладок/состояний, AGR-03.02 — UI-проекции в .cs. Дизайн-раздел очереди фактически исчерпан: оставшееся — приёмки (A), иконки с эталоном и координированные сплиты.

## Верификация

Изменений кода нет — build не требуется; репо не менялось до финального документационного коммита. Сверки: rg/циклы по 46 XAML, чтение App.xaml:302–460, MainWindow.xaml:94–100/484–492, ActsEditorView.xaml:113–158 + handler ActsEditorView.xaml.cs:1775–1799, MainWindow.Pto.cs:24–30.
