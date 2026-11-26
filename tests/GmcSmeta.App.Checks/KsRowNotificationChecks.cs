using GmcSmeta.App;

internal static class KsRowNotificationChecks
{
    // Same sequence of mutable fields as the authoritative quantity readback.
    private static void Apply(KsRowVm row, decimal qty, bool over)
    {
        row.Before = "2";
        row.Now = qty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.NowValue = qty;
        row.NowRaw = "К15*2";
        row.NowToolTip = "К15*2";
        row.Left = over ? "-1" : "1";
        row.LeftRaw = over ? -1m : 1m;
        row.RestRaw = 3m;
        row.Issued = "5";
        row.Price = "100";
        row.Amount = (qty * 100m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.InAct = qty != 0m;
        row.Warn = over;
        row.OverLeft = over;
    }

    public static void Run()
    {
        var rows = Enumerable.Range(1, 25000).Select(id => new KsRowVm { PositionId = id }).ToArray();
        foreach (var row in rows) Apply(row, 2m, false);
        var notifications = new List<string?>();
        foreach (var row in rows)
            row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        foreach (var row in rows) Apply(row, 2m, false);
        if (notifications.Count != 0)
            throw new Exception("Unchanged authoritative KS values must not notify WPF.");

        var changed = rows[1234];
        Apply(changed, 4m, true);
        var expected = new[]
        {
            nameof(KsRowVm.Now), nameof(KsRowVm.NowValue), nameof(KsRowVm.Left),
            nameof(KsRowVm.LeftRaw), nameof(KsRowVm.Amount), nameof(KsRowVm.Warn),
            nameof(KsRowVm.NowCellFore), nameof(KsRowVm.OverLeft),
            nameof(KsRowVm.LeftCellFore), nameof(KsRowVm.LeftToolTip)
        };
        if (!notifications.SequenceEqual(expected) || changed.NowValue != 4m ||
            changed.LeftToolTip != "Больше, чем по смете!" || !changed.Warn)
            throw new Exception("Changed KS values or dependent notifications are incomplete.");
        notifications.Clear();
        changed.NowToolTip = "К15*3";
        changed.NowRaw = "К15*3";
        changed.InAct = false;
        changed.OverLeft = false;
        if (!notifications.SequenceEqual(new[]
            {
                nameof(KsRowVm.NowToolTip), nameof(KsRowVm.NowRaw),
                nameof(KsRowVm.InAct), nameof(KsRowVm.NowCellWeight),
                nameof(KsRowVm.OverLeft), nameof(KsRowVm.LeftCellFore), nameof(KsRowVm.LeftToolTip)
            }) || changed.LeftToolTip.Length != 0)
            throw new Exception("KS formula, weight or warning tooltip did not update correctly.");

        notifications.Clear();
        changed.NowToolTip = "К15*3";
        changed.NowRaw = "К15*3";
        changed.InAct = false;
        changed.OverLeft = false;
        if (notifications.Count != 0)
            throw new Exception("Repeated KS changes must not notify WPF.");
        Console.WriteLine("KS row notifications: 25000 unchanged rows, changed fields and dependent bindings OK.");
    }
}
