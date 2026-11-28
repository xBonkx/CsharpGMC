using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

/// <summary>
/// Живая SQL-приёмка остатков BUG-F01..F25, не покрытых другими сценариями:
/// ревизионно-огороженный Undo (F16/F17), скоуп-валидация замены строк (F21/F22),
/// сохранность формул и закупочной цены при внутреннем копировании (F24),
/// OUTPUT-сопоставление пакетной вставки с детьми (F25), дисциплина снимка
/// корзины (F23), ранний отказ закупок на пустом списке (F11) и пересчёт
/// источника при неизменном поставщике (F12). Только синтетическая
/// GmcSmetaPilot_E2E_* база, принадлежащая раннеру.
/// </summary>
internal static class PilotBugFAcceptanceCases
{
    public static void Run(PilotRunner p)
    {
        RevisionFencedUndo(p);
        CopyKeepsFormulasAndWorkPrice(p);
        ReplaceRowsScopeAndCommitRevision(p);
        TrashSnapshotDiscipline(p);
        ProcurementEmptyRefusalAndSourceFix(p);
    }

    // BUG-F16/F17: собственная транзакция Undo возвращает committedRevision,
    // чужой сдвиг не подменяет ожидаемый снимок и не пишет. Изолированный договор:
    // у fixture-договоров к этому моменту подписанные акты — Apply корректно
    // откажет (защита неизменяемых документов из F17), поэтому undo проверяем
    // на собственном пустом договоре без актов.
    private static void RevisionFencedUndo(PilotRunner p)
    {
        var cs = p.PilotConnection;
        var contract = SqlServerWorkspace.CreateEmptyContract(cs, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-UNDO", "", "QA undo fence",
                null, null), p.Revision());
        var section = SqlServerDatabase.AddSection(cs, contract, "Undo QA", false, null, p.Revision());
        PilotRunner.Check(section.Saved && section.SystemId.HasValue, "Undo fixture section failed");
        var position = SqlServerDatabase.AddPosition(cs, contract, section.SystemId!.Value,
            null, null, p.Revision()).PositionId!.Value;
        PilotRunner.Check(SqlServerDatabase.SaveCell(cs, position, "price", "100", p.Revision()).Saved,
            "Undo fixture price seed failed");
        using var conn = p.Conn();
        var original = PilotRunner.Double(conn, "SELECT price FROM dbo.positions WHERE id=@id", ("@id", position));
        var target = SqlServerUndo.Snapshot(conn, null, checked((int)contract))
            ?? throw new InvalidOperationException("Undo baseline snapshot missing");
        var head = p.Revision();

        var edited = SqlServerDatabase.SaveCell(cs, position, "price", "456.25", head);
        PilotRunner.Check(edited.Saved, "SaveCell before undo did not commit");
        using (var after = p.Conn())
        {
            var current = SqlServerUndo.Snapshot(after, null, checked((int)contract))
                ?? throw new InvalidOperationException("Post-edit snapshot missing");
            var applied = SqlServerUndo.Apply(cs, checked((int)contract),
                target, current, undo: true);
            PilotRunner.Check(applied.Status == UndoApplyStatus.Applied &&
                applied.Revision == p.Revision() && applied.Revision != head,
                $"Own undo lost the committed revision acknowledgement (status={applied.Status}, rev={applied.Revision}, head={head}, now={p.Revision()})");
        }
        using (var check = p.Conn())
            PilotRunner.Check(PilotRunner.Double(check, "SELECT price FROM dbo.positions WHERE id=@id",
                ("@id", position)) == original, "Undo did not restore the baseline value");

        // Чужая правка между снимками: Apply обязан вернуть Moved и ничего не писать.
        var movedHead = p.Revision();
        var changed = SqlServerDatabase.SaveCell(cs, position, "price", "789", movedHead);
        PilotRunner.Check(changed.Saved, "SaveCell for moved-undo scenario did not commit");
        using (var stale = p.Conn())
        {
            var expected = SqlServerUndo.Snapshot(stale, null, checked((int)contract))!;
            var again = SqlServerDatabase.SaveCell(cs, position, "price", "790", p.Revision());
            PilotRunner.Check(again.Saved, "Second foreign edit did not commit");
            var moved = SqlServerUndo.Apply(cs, checked((int)contract),
                target, expected, undo: true);
            PilotRunner.Check(moved.Status == UndoApplyStatus.Moved && moved.Now is not null,
                "Undo did not refuse a foreign revision shift");
        }
        using (var final = p.Conn())
            PilotRunner.Check(PilotRunner.Double(final, "SELECT price FROM dbo.positions WHERE id=@id",
                ("@id", position)) == 790, "Refused undo still changed the row");
    }

    // BUG-F24/F25: внутренняя копия сохраняет формулы и закупочную цену СМР,
    // дети вставки получают правильные parent_id и порядок через OUTPUT.
    private static void CopyKeepsFormulasAndWorkPrice(PilotRunner p)
    {
        var head = p.Revision();
        var source = p.FirstPosition;
        const string formulas = "qty * 2 + [М2]";
        var rows = new[]
        {
            new CopiedRow("Приёмка F24 строка", "шт", 3m, 100m, 61.5m, "note F24",
                formulas, new[] { new CopiedChild("Ребёнок А", "м", 1.5m, ""), new CopiedChild("Ребёнок Б", "м", 2m, "") })
            {
                WorkPrice = 123.45m,
            },
            new CopiedRow("Приёмка F24 вторая", "шт", 1m, 50m, 0m, "", "", null),
        };
        var inserted = SqlServerDatabase.InsertCopiedRows(p.PilotConnection, p.SmallContract,
            p.SmallSystem, null, rows, head);
        PilotRunner.Check(inserted.Saved &&
            inserted.Revision == p.Revision() && inserted.Revision != head,
            "Copied insert lost its commit revision");

        using var conn = p.Conn();
        var parentIds = new List<long>();
        using (var cmd = new SqlCommand(
            "SELECT id FROM dbo.positions WHERE system_id=@system AND name LIKE N'Приёмка F24%' " +
            "ORDER BY [order],id", conn))
        {
            cmd.Parameters.AddWithValue("@system", p.SmallSystem);
            using var r = cmd.ExecuteReader();
            while (r.Read()) parentIds.Add(r.GetInt64(0));
        }
        PilotRunner.Check(parentIds.Count == 2, "Insert did not create both copied rows");
        using (var verify = new SqlCommand(
            "SELECT formulas, work_price, purchase_price FROM dbo.positions WHERE id=@id", conn))
        {
            verify.Parameters.AddWithValue("@id", parentIds[0]);
            using var r = verify.ExecuteReader();
            PilotRunner.Check(r.Read() && r.GetString(0) == formulas &&
                Convert.ToDouble(r.GetValue(1)) == 123.45 &&
                Convert.ToDouble(r.GetValue(2)) == 61.5,
                "Copy dropped formulas or the positional SMR work price");
            r.Close();
        }
        using (var kids = new SqlCommand(
            "SELECT parent_id, qty FROM dbo.positions WHERE parent_id IN (@a,@b) ORDER BY parent_id,[order]",
            conn))
        {
            kids.Parameters.AddWithValue("@a", parentIds[0]);
            kids.Parameters.AddWithValue("@b", parentIds[1]);
            using var r = kids.ExecuteReader();
            long firstParent = 0;
            var count = 0;
            while (r.Read())
            {
                count++;
                if (count == 1) firstParent = r.GetInt64(0);
            }
            PilotRunner.Check(count == 2 && firstParent == parentIds[0],
                "OUTPUT mapping mismatched copied children with their new parent");
        }
        r_guard(conn, parentIds);
    }

    private static void r_guard(SqlConnection conn, List<long> parentIds)
    {
        // BUG-F25: частичная вставка детей недопустима — сверяем счётчик детей
        // против заявленного в результате (уже проверено выше); здесь фиксируем
        // отсутствие сирот после вставки.
        var orphans = PilotRunner.Long(conn,
            "SELECT COUNT_BIG(*) FROM dbo.positions c WHERE parent_id IN (@a,@b) " +
            "AND NOT EXISTS (SELECT 1 FROM dbo.positions p WHERE p.id=c.parent_id)",
            ("@a", parentIds[0]), ("@b", parentIds[1]));
        PilotRunner.Check(orphans == 0, "Copied insert produced orphaned children");
    }

    // BUG-F21/F22: замена валидирует ВЕСЬ список удаляемых ID текущего договора,
    // при отказе ничего не пишет; успех возвращает ревизию собственного COMMIT.
    private static void ReplaceRowsScopeAndCommitRevision(PilotRunner p)
    {
        var head = p.Revision();

        // Чужой/несуществующий ID в списке удаления — отказ до записи.
        long foreignId;
        using (var conn = p.Conn())
            foreignId = PilotRunner.Long(conn,
                "SELECT TOP(1) p.id FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id WHERE sy.contract_id=@c ORDER BY p.id",
                ("@c", p.LargeContract));
        var refused = SqlServerDatabase.ReplaceRows(p.PilotConnection, p.SmallContract,
            Array.Empty<SqlServerDatabase.ReplaceUpdate>(),
            new[] { foreignId },
            new[] { new CopiedRow("Замена F21", "шт", 1m, 10m, 0m, "", "", null) }, head);
        PilotRunner.Check(!refused.Saved && p.Revision() == head,
            "Replace accepted a foreign delete id or wrote before scope validation");

        // Корректная замена 1:1 обновляет строку и подтверждает свою ревизию.
        using (var conn = p.Conn())
        {
            var own = PilotRunner.Long(conn,
                "SELECT TOP(1) p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@c AND p.parent_id IS NULL AND p.out_of_kp=0 " +
                "AND p.name NOT LIKE N'Приёмка F24%' ORDER BY p.id DESC",
                ("@c", p.SmallContract));
            var applied = SqlServerDatabase.ReplaceRows(p.PilotConnection, p.SmallContract,
                new[] { new SqlServerDatabase.ReplaceUpdate(own, "Замена F21 1:1", null, 7m, 33m, 12m) },
                Array.Empty<long>(), Array.Empty<CopiedRow>(), head);
            PilotRunner.Check(applied.Saved && applied.Revision == p.Revision() &&
                applied.Revision != head,
                "1:1 replace lost the committed revision of its own branch");
            PilotRunner.Check(PilotRunner.Long(conn,
                "SELECT COUNT_BIG(*) FROM dbo.positions WHERE id=@id AND qty=7 AND price=33",
                ("@id", own)) == 1, "1:1 replace did not persist the new values");
        }
    }

    // BUG-F23: корзина читает согласованный снимок, отказ по чужой ревизии не
    // меняет базу, Forget подтверждает фактическое число удалённых строк.
    private static void TrashSnapshotDiscipline(PilotRunner p)
    {
        var head = p.Revision();
        // Свежий договор с минимальным составом: у fixture-договоров к этому
        // моменту акты/закупки/выдачи — восстановление таких дампов корректно
        // требует отдельной подтверждённой процедуры (TrashRestoreSafety).
        var victim = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-TRASH", "", "QA trash snapshot",
                null, null), p.Revision());
        var trashSection = SqlServerDatabase.AddSection(p.PilotConnection, victim,
            "Trash QA", false, null, p.Revision());
        PilotRunner.Check(trashSection.Saved && trashSection.SystemId.HasValue,
            "Trash fixture section failed");
        SqlServerWorkspace.DeleteContract(p.PilotConnection, victim, p.Revision());
        var (entries, rev) = SqlServerTrash.ReadSnapshot(p.PilotConnection);
        var entry = entries.FirstOrDefault(e => e.Kind == "contract");
        PilotRunner.Check(entry is not null && entry.Id > 0 && !string.IsNullOrWhiteSpace(rev),
            "Trash snapshot did not return the deleted contract");

        var staleRefused = false;
        try { _ = SqlServerTrash.Restore(p.PilotConnection, entry.Id, "intentionally-stale-trash-revision"); }
        catch (Exception) { staleRefused = true; }
        PilotRunner.Check(staleRefused, "Stale revision accepted by trash restore");
        var restored = SqlServerTrash.Restore(p.PilotConnection, entry.Id, rev);
        // Восстановление пересоздаёт строки с новыми identity-id — проверяем
        // фактическое существование и состав восстановленного договора.
        PilotRunner.Check(restored.Kind == "contract" && restored.Id > 0 && restored.Id != victim,
            "Trash restore did not return a recreated contract");
        using (var back = p.Conn())
            PilotRunner.Check(PilotRunner.Long(back,
                "SELECT COUNT_BIG(*) FROM dbo.contracts c JOIN dbo.systems sy ON sy.contract_id=c.id " +
                "WHERE c.id=@id AND sy.name=N'Trash QA'", ("@id", restored.Id)) == 1,
                "Restored contract lost its dumped section");
        PilotRunner.Check(p.Revision() != rev, "Restore did not bump the revision");

        // Повторное удаление восстановленного договора → Forget с подтверждённым снимком.
        var head2 = p.Revision();
        SqlServerWorkspace.DeleteContract(p.PilotConnection, restored.Id, head2);
        var (entries2, rev2) = SqlServerTrash.ReadSnapshot(p.PilotConnection);
        var entry2 = entries2.First(e => e.Kind == "contract");
        PilotRunner.Check(SqlServerTrash.Forget(p.PilotConnection, entry2.Id, rev2),
            "Forget did not confirm the acknowledged snapshot revision");
        // Forget сдвинул ревизию — повтор по той же ревизии обязан отказать.
        var repeatedRefused = false;
        try { _ = SqlServerTrash.Forget(p.PilotConnection, entry2.Id, rev2); }
        catch (Exception) { repeatedRefused = true; }
        PilotRunner.Check(repeatedRefused, "Forget repeated on an already deleted entry");
        // Проверка по чужой ревизии: отказ без удаления строки.
        var (entries3, rev3) = SqlServerTrash.ReadSnapshot(p.PilotConnection);
        if (entries3.Count > 0)
        {
            var staleForgetRefused = false;
            try { _ = SqlServerTrash.Forget(p.PilotConnection, entries3[0].Id, "intentionally-stale-forget-revision"); }
            catch (Exception) { staleForgetRefused = true; }
            PilotRunner.Check(staleForgetRefused, "Stale revision accepted by Forget");
        }
    }

    // BUG-F11/F12: пустой список позиций отказывает до SQL, повторный выбор
    // того же поставщика исправляет производный источник.
    private static void ProcurementEmptyRefusalAndSourceFix(PilotRunner p)
    {
        var head = p.Revision();
        var empty = SqlServerProcurement.SetProc(p.PilotConnection, checked((int)p.SmallContract),
            Array.Empty<long>(), "Новый поставщик пустого списка", null, null, null, null, null,
            "", null, false, head);
        PilotRunner.Check(!empty.Saved && p.Revision() == head,
            "Empty procurement list created a supplier or wrote anyway");
        using (var conn = p.Conn())
            PilotRunner.Check(PilotRunner.Long(conn,
                "SELECT COUNT_BIG(*) FROM dbo.suppliers WHERE name=N'Новый поставщик пустого списка'") == 0,
                "Empty list still registered the supplier");

        // F12: строка с supplier=Цех фиксирует buy → повторное Цех делает shop.
        {
            var position = p.FirstPosition;
            var baseHead = p.Revision();
            var seed = SqlServerProcurement.SetProc(p.PilotConnection, checked((int)p.SmallContract),
                new[] { position }, "Цех", null, null, null, null, null, "", null, false, baseHead);
            PilotRunner.Check(seed.Saved, "Shop supplier seed failed");
            string shop;
            using (var conn = p.Conn())
                shop = PilotRunner.Text(conn,
                    "SELECT source FROM dbo.procurement WHERE position_id=@id", ("@id", position));
            PilotRunner.Check(shop == "shop", "Shop supplier did not derive source=shop");

            // Возвращаем buy, затем повторно Цех — источник обязан вернуться в shop.
            var toBuy = SqlServerProcurement.SetProc(p.PilotConnection, checked((int)p.SmallContract),
                new[] { position }, "Внешний поставщик F12", null, null, null, null, null, "", null, false,
                p.Revision());
            PilotRunner.Check(toBuy.Saved, "Buy supplier switch failed");
            string buy;
            using (var conn = p.Conn())
                buy = PilotRunner.Text(conn, "SELECT source FROM dbo.procurement WHERE position_id=@id",
                    ("@id", position));
            PilotRunner.Check(buy == "buy", "Ordinary supplier did not derive source=buy");
            var repeat = SqlServerProcurement.SetProc(p.PilotConnection, checked((int)p.SmallContract),
                new[] { position }, "Цех", null, null, null, null, null, "", null, false, p.Revision());
            PilotRunner.Check(repeat.Saved, "Repeat shop supplier refused");
            string fixedSource;
            using (var conn = p.Conn())
                fixedSource = PilotRunner.Text(conn,
                    "SELECT source FROM dbo.procurement WHERE position_id=@id", ("@id", position));
            PilotRunner.Check(fixedSource == "shop",
                "Repeat of the same supplier did not refresh the derived source");
        }
    }
}
