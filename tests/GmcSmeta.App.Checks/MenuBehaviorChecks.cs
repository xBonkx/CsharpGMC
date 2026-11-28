using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation;
using System.Windows.Controls;
using GmcSmeta.App;

internal static class MenuBehaviorChecks
{
    internal static ContextMenu CreateFixture()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextMenuIcons.Create("Копировать", "Copy", shortcut: "Ctrl+C"));
        menu.Items.Add(new Separator());
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextMenuIcons.Create("Вставить с сохранением исходных настроек", "Duplicate",
            enabled: false, shortcut: "Ctrl+Shift+V"));
        var sub = ContextMenuIcons.Create("Переместить", "ArrowForward");
        sub.Items.Add(ContextMenuIcons.Create("В другой раздел", "Folder"));
        menu.Items.Add(sub);
        menu.Items.Add(ContextMenuIcons.Create("Удалить", "Trash", shortcut: "Del", danger: true));
        menu.Items.Add(new Separator());
        ContextMenuIcons.NormalizeSeparators(menu);
        return menu;
    }

    internal static void Run()
    {
        var menu = CreateFixture();
        var separators = menu.Items.OfType<Separator>().ToArray();
        if (separators.Length != 4 ||
            separators[0].Visibility != Visibility.Collapsed ||
            separators[1].Visibility != Visibility.Visible ||
            separators[2].Visibility != Visibility.Collapsed ||
            separators[3].Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Menu separator normalization changed.");
        var disabled = menu.Items.OfType<MenuItem>().Single(i => !i.IsEnabled);
        var clicks = 0;
        disabled.Click += (_, _) => clicks++;
        var peer = new MenuItemAutomationPeer(disabled);
        if (peer.IsEnabled())
            throw new InvalidOperationException("Disabled menu item is enabled in UI Automation.");
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Expected Invoke pattern for leaf menu item.");
        try
        {
            invoke.Invoke();
            throw new InvalidOperationException("Disabled menu Invoke should be rejected.");
        }
        catch (ElementNotEnabledException) { }
        if (clicks != 0)
            throw new InvalidOperationException("Disabled menu executed via UI Automation.");
        // This verifies the actual provider, not physical Shift+F10/keyboard routing.
        Console.WriteLine("Menu normalization and disabled UI Automation invoke checked (no SQL).");
    }
}
