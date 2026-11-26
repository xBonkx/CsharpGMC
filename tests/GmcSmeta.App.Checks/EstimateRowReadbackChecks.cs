using GmcSmeta.App;
using GmcSmeta.Core.Domain;

internal static class EstimateRowReadbackChecks
{
    internal static void Run()
    {
        var rows = Enumerable.Range(1, 25000).Select(id =>
            new EditableEstimateRow(new EstimateRow(RowKind.Position, 1, id, id,
                false, id.ToString(), "Воздуховод", "шт", 2m, 123.45m, 246.90m,
                50m, false, "note") { SystemId = 1, WorkPrice = 12m, AreaManual = 1.25 })).ToArray();

        int notifications = 0;
        foreach (var row in rows)
        {
            row.FillArea();
            row.PropertyChanged += (_, _) => notifications++;
        }

        foreach (var row in rows)
        {
            var qty = row.QtyText;
            var price = row.PriceText;
            var purchase = row.PurchaseText;
            var work = row.WorkText;
            if (!row.TryReuseAuthoritative(row.Source with { }))
                throw new Exception("Equal authoritative estimate row was not reused.");
            row.FillArea();
            if (!ReferenceEquals(qty, row.QtyText) || !ReferenceEquals(price, row.PriceText) ||
                !ReferenceEquals(purchase, row.PurchaseText) || !ReferenceEquals(work, row.WorkText))
                throw new Exception("Unchanged formatted text was replaced.");
        }
        if (notifications != 0)
            throw new Exception("Unchanged 25000 estimate rows emitted notifications.");

        var first = rows[0];
        var expected = new EditableEstimateRow(first.Source);
        expected.FillArea();
        var changed = new HashSet<string>();
        first.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        first.QtyText = "99";
        first.PriceText = "1";
        first.PurchaseText = "2";
        first.WorkText = "3";
        first.AreaUnitEditText = "999";
        changed.Clear();
        if (!first.TryReuseAuthoritative(first.Source with { }))
            throw new Exception("Equal source with optimistic text must be reusable.");
        first.FillArea();
        if (first.QtyText != expected.QtyText || first.PriceText != expected.PriceText ||
            first.PurchaseText != expected.PurchaseText || first.WorkText != expected.WorkText ||
            first.AreaUnitEditText != expected.AreaUnitEditText ||
            first.DisplayAmountText != expected.DisplayAmountText ||
            !changed.Contains(nameof(first.DisplayAmountText)) ||
            !changed.Contains(nameof(first.AreaUnitEditText)))
            throw new Exception("Authoritative readback did not restore edited values and bindings.");

        first.Unit = "м";
        first.Name = "Другое название";
        first.FillArea();
        var area = AreaCellPresentation.For(first.Name, first.Unit, first.Source.Qty,
            first.Source.AreaManual, first.Source.IsAreaSummary);
        if (first.AreaUnitTip != area.UnitTip || first.AreaTotalTip != area.TotalTip)
            throw new Exception("Area cache retained old name/unit inputs.");
        first.TryReuseAuthoritative(first.Source);
        first.FillArea();
        if (first.AreaUnitTip != expected.AreaUnitTip || first.AreaTotalTip != expected.AreaTotalTip)
            throw new Exception("Area cache did not restore authoritative inputs.");
        if (first.TryReuseAuthoritative(first.Source with { Qty = 3m }) ||
            first.TryReuseAuthoritative(first.Source with { AreaManual = 2.5 }))
            throw new Exception("Changed structural or area source must create a new row.");

        changed.Clear();
        first.TryReuseAuthoritative(first.Source);
        first.FillArea();
        if (changed.Count != 0) throw new Exception("Repeated clean readback emitted notifications.");
        CheckMissingWorkPrice(first.Source);
        CheckStagePresentation(rows);
        CheckParsedNumericText(first.Source);
        Console.WriteLine("Estimate formatted readback and area notifications OK (25000 rows).");
    }

    private static void CheckParsedNumericText(EstimateRow source)
    {
        var row = new EditableEstimateRow(source);
        foreach (var qty in new[] { "2", "1,25", "=2*3", "ошибка", "0", "2" })
        foreach (var price in new[] { "1 234,50", "=4+5", "0", "", "2/0", "1 234,50" })
        {
            row.QtyText = qty;
            row.PriceText = price;
            var q = GmcSmeta.Core.NumberText.Parse(qty);
            var p = GmcSmeta.Core.NumberText.Parse(price);
            var amount = q.HasValue && p.HasValue ? q.Value * p.Value : source.DisplayAmount;
            var missing = string.IsNullOrWhiteSpace(price) || p == 0m;
            for (int repeat = 0; repeat < 3; repeat++)
                if (row.DisplayAmount != (amount == 0m ? null : amount) || row.HasNoPrice != missing)
                    throw new Exception("Cached estimate number differs from current text.");
        }
        foreach (var text in new[] { "", "0", "=2*3", "не число", "0" })
        {
            row.WorkText = text;
            var missing = string.IsNullOrWhiteSpace(text) || GmcSmeta.Core.NumberText.Parse(text) == 0m;
            if (row.HasNoWorkPrice != missing || row.WorkDisplayText != (missing ? "—" : text))
                throw new Exception("Work price cache retained an old value.");
        }
        if (!row.TryReuseAuthoritative(source))
            throw new Exception("Numeric readback failed.");
        var fresh = new EditableEstimateRow(source);
        if (row.DisplayAmount != fresh.DisplayAmount || row.DisplayAmountText != fresh.DisplayAmountText ||
            row.HasNoPrice != fresh.HasNoPrice || row.HasNoWorkPrice != fresh.HasNoWorkPrice)
            throw new Exception("Numeric cache did not follow authoritative readback.");
    }

    private static void CheckStagePresentation(EditableEstimateRow[] rows)
    {
        var labels = StageAllocationPresentation.CellLabels(new decimal[] { 1, 0 }, 2);
        foreach (var row in rows) row.PublishStagePresentation(1, labels, false);
        var changed = 0;
        foreach (var row in rows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EditableEstimateRow.StageDisplay) or nameof(EditableEstimateRow.StageOverrun))
                    changed++;
            };
        foreach (var row in rows)
        {
            if (!row.HasStagePresentation(1) || row.HasStagePresentation(2))
                throw new Exception("Stage cache reused wrong version.");
            row.PublishStagePresentation(2, labels.ToArray(), false);
        }
        if (changed != 0) throw new Exception("Equal stage labels emitted change notifications.");
        var first = rows[0];
        first.PublishStagePresentation(3, StageAllocationPresentation.CellLabels(new decimal[] { 2, 1 }, -1), true);
        if (changed != 2 || !first.HasStagePresentation(3) || !first.StageOverrun)
            throw new Exception("Stage readback did not publish changed cells.");
        first.StageDisplay = Array.Empty<string>();
        first.StageOverrun = false;
        if (first.HasStagePresentation(3) || first.StageDisplay.Count != 0 || first.StageOverrun)
            throw new Exception("Stage mode exit did not invalidate the cache.");
    }

    private static void CheckMissingWorkPrice(EstimateRow source)
    {
        foreach (var missing in new decimal?[] { null, 0m })
        {
            var row = new EditableEstimateRow(source with { WorkPrice = missing });
            if (row.WorkDisplayText != "—" || !row.HasNoWorkPrice || row.DisplayWorkAmount.HasValue)
                throw new Exception("Missing work price must display a dash and no work amount.");
            row.WorkText = "12";
            if (row.HasNoWorkPrice || row.WorkDisplayText != "12")
                throw new Exception("Typed work price did not clear the missing-price presentation.");
            row.TryReuseAuthoritative(row.Source);
            if (!row.HasNoWorkPrice || row.WorkDisplayText != "—")
                throw new Exception("Readback did not restore missing work price.");
        }
        var priced = new EditableEstimateRow(source with { WorkPrice = 12m });
        if (priced.HasNoWorkPrice || priced.DisplayWorkAmount != source.Qty * 12m)
            throw new Exception("Existing work price presentation changed.");
        var extra = new EditableEstimateRow(source with { WorkPrice = null, OutOfKp = true });
        if (extra.HasNoWorkPrice || extra.WorkDisplayText != "—")
            throw new Exception("Out-of-KP work should not have a missing-price warning.");
    }
}
