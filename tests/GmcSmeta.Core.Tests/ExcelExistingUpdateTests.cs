using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ExcelExistingUpdateTests
{
    private static readonly string Hash = new('A', 64);

    private static ExcelExistingDbRow Old(long id = 1, string name = "Клапан", decimal quantity = 2,
        string formulas = "", bool imported = true, bool linked = false, bool signed = false,
        decimal price = 150, string note = "Сохранить ручное примечание") =>
        new(id, 7, null, "П1", "", "1", name, "шт", quantity, price,
            90, 10, note, formulas, imported ? 15 : null, "",
            linked, signed, imported);

    private static ExcelExistingSourceRow Incoming(int ordinal = 1, string name = "Клапан",
        decimal quantity = 3, string system = "П1", long? id = null,
        decimal price = 100) =>
        new(ordinal, null, system, "", "1", name, "шт", quantity,
            price, 95, 20, "Excel note", 18 + ordinal, id);

    private static ExcelExistingUpdatePlan Build(ExcelExistingDbRow[] old,
        ExcelExistingSourceRow[] newer, bool locked = false, string[]? systems = null) =>
        ExcelExistingUpdatePlan.Build(123, "rev-123", Hash, old, newer,
            systems ?? new[] { "П1" }, locked);

    [Fact]
    public void Matching_ChangesQuantityButNeverChoosesManualPricesOrNotes()
    {
        var p = Build(new[] { Old() }, new[] { Incoming() });
        var diff = Assert.Single(p.Differences);
        Assert.Equal(ExcelExistingChangeKind.Changed, diff.Kind);
        Assert.True(diff.SafeToApply);
        Assert.Contains("сохранены ручные цены", diff.Explanation);
        Assert.Contains("примечание сохранено", diff.Explanation);
        diff.Apply = true;
        Assert.Equal(ExcelExistingAction.ApplyQuantity, Assert.Single(p.Selected()).Action);
    }

    [Fact]
    public void UnverifiedLegacyQtyCannotBeSelectedInBulk()
    {
        var plan = Build(new[] { Old() }, new[] { Incoming() });
        var row = Assert.Single(plan.Differences);
        Assert.True(row.SafeToApply);
        Assert.False(row.SafeForBulk);
        row.Apply = true; // explicit review of this individual row is still possible
        Assert.Single(plan.Selected());
    }

    [Fact]
    public void VerifiedImportBaselineAllowsBulkButManualOverrideDisablesIt()
    {
        var verified = Old() with { ExtraJson = "{\"excel_imported_qty\":\"2\"}" };
        var row = Assert.Single(Build(new[] { verified }, new[] { Incoming() }).Differences);
        Assert.True(row.SafeForBulk);

        var manual = verified with { Quantity = 2.5m };
        var changed = Assert.Single(Build(new[] { manual }, new[] { Incoming() }).Differences);
        Assert.True(changed.SafeToApply);
        Assert.False(changed.SafeForBulk);

        var corrupt = verified with { ExtraJson = "{broken}" };
        var blocked = Assert.Single(Build(new[] { corrupt }, new[] { Incoming() }).Differences);
        Assert.False(blocked.SafeForBulk);
    }

    [Fact]
    public void SignedActAndDependenciesBlockVolumeChange()
    {
        foreach (var row in new[] { Old(linked: true), Old(signed: true),
            Old(formulas: "{\"qty\":\"2*1\"}"), Old(imported: false) })
        {
            var diff = Assert.Single(Build(new[] { row }, new[] { Incoming() }).Differences);
            Assert.Equal(ExcelExistingChangeKind.Conflict, diff.Kind);
            diff.Apply = true;
            Assert.Equal(ExcelExistingAction.Skip, diff.Action);
        }
    }

    [Fact]
    public void DuplicateSourceRowsAreAmbiguousEvenIfOnlyOneExistingRow()
    {
        var p = Build(new[] { Old() }, new[] { Incoming(1), Incoming(2) });
        Assert.Equal(2, p.Differences.Count(d => d.Kind == ExcelExistingChangeKind.Ambiguous));
        Assert.All(p.Differences, d => Assert.False(d.SafeToApply));
    }

    [Fact]
    public void DuplicateExistingPositionsAreNotSilentlyCombined()
    {
        var p = Build(new[] { Old(1), Old(2) }, new[] { Incoming() });
        Assert.Contains(p.Differences, d => d.Kind == ExcelExistingChangeKind.Ambiguous);
        Assert.DoesNotContain(p.Differences, d => d.SafeToApply);
    }

    [Fact]
    public void StableIdWinsWhenDuplicateNamesButMustMatchSystem()
    {
        var p = Build(new[] { Old(1), Old(2) }, new[] { Incoming(id: 2) });
        var updated = Assert.Single(p.Differences.Where(d => d.Source is not null));
        Assert.Equal(2, updated.Existing!.Id);
        Assert.True(updated.SafeToApply);
        var wrong = Build(new[] { Old() }, new[] { Incoming(system: "В1", id: 1) });
        Assert.Contains(wrong.Differences, d => d.Kind == ExcelExistingChangeKind.Ambiguous);
    }

    [Fact]
    public void MissingFromExcelIsInformationalNeverDeleted()
    {
        var p = Build(new[] { Old(), Old(2, "Решётка") }, new[] { Incoming() });
        var missing = Assert.Single(p.Differences.Where(d => d.Kind == ExcelExistingChangeKind.DeletedInSource));
        Assert.Equal(2, missing.Existing!.Id);
        Assert.False(missing.SafeToApply);
        Assert.Throws<InvalidOperationException>(() => p.Selected());
    }

    [Fact]
    public void AddedRootIsOptInButUnknownSectionAndChildAreProtected()
    {
        var ok = Build(Array.Empty<ExcelExistingDbRow>(), new[] { Incoming(name: "Новая") });
        var row = Assert.Single(ok.Differences);
        Assert.Equal(ExcelExistingChangeKind.Added, row.Kind);
        row.Apply = true;
        Assert.Equal(ExcelExistingAction.AddPosition, row.Action);
        var absent = Build(Array.Empty<ExcelExistingDbRow>(), new[]
            { Incoming(name: "Новая", system: "В1") });
        Assert.Equal(ExcelExistingChangeKind.Conflict, Assert.Single(absent.Differences).Kind);
        // ordinal должен быть уникален в выборке (guard дубликатов в Plan.Build прав):
        // родитель — 1, ребёнок — 2, ParentOrdinal ссылается на ordinal родителя.
        var child = Incoming(2, "Комплектующая") with { ParentOrdinal = 1 };
        var parent = Incoming(1, "Родитель");
        var children = Build(Array.Empty<ExcelExistingDbRow>(), new[] { parent, child });
        Assert.Equal(ExcelExistingChangeKind.Conflict,
            Assert.Single(children.Differences.Where(x => x.Source?.ParentOrdinal != null)).Kind);
    }

    [Fact]
    public void ManualDisplayRenameDoesNotBreakStoredOriginalSourceMatch()
    {
        var renamed = Old(name: "Ручное название") with
        {
            ExtraJson = "{\"src\":\"Клапан\"}"
        };
        var planned = Build(new[] { renamed }, new[] { Incoming() });
        var row = Assert.Single(planned.Differences);
        Assert.Equal(renamed.Id, row.Existing!.Id);
        Assert.True(row.SafeToApply);
        Assert.Equal("Ручное название", row.Existing.Name);
    }

    [Fact]
    public void DifferentUnitOrRenamedWithoutSourceMustNotCreateDuplicate()
    {
        var changedUnit = Old() with { Unit = "м²" };
        var p = Build(new[] { changedUnit }, new[] { Incoming() });
        var diff = Assert.Single(p.Differences.Where(x => x.Source is not null));
        Assert.Equal(ExcelExistingChangeKind.Conflict, diff.Kind);
        Assert.False(diff.SafeToApply);
        var renamed = Old(name: "Новое имя");
        var other = Build(new[] { renamed }, new[] { Incoming() });
        Assert.Equal(ExcelExistingChangeKind.Conflict,
            Assert.Single(other.Differences.Where(x => x.Source is not null)).Kind);
    }

    [Fact]
    public void DifferentExcelBlocksMayUseTheSamePhysicalFileSafely()
    {
        var p1 = ExcelExistingUpdatePlan.Build(123, "rev-123", Hash,
            Array.Empty<ExcelExistingDbRow>(), new[] { Incoming(name: "Новая") },
            new[] { "П1" }, scopeKey: "Лист1:5-50");
        var p2 = ExcelExistingUpdatePlan.Build(123, "rev-123", Hash,
            Array.Empty<ExcelExistingDbRow>(), new[] { Incoming(name: "Новая") },
            new[] { "П1" }, scopeKey: "Лист2:5-50");
        Assert.NotEqual(p1.ScopeKey, p2.ScopeKey);
        Assert.NotEqual(p1.PlanId, p2.PlanId);
    }

    [Fact]
    public void FlattenRetainsExcelBasisAndImportedCustomColumns()
    {
        var block = new ExcelImportBlock { Sheet = "ДНС", FirstRow = 1, LastRow = 20 };
        var section = new ExcelImportSection { Name = "В15", Parent = "Раздел 23" };
        var position = new ExcelImportPosition
        {
            SourceRow = 10, Number = "1", Name = "Труба",
            Unit = "м", Quantity = 3m, Price = 100m
        };
        position.Extra["basis"] = "9.14.8-01";
        position.Extra["source_code"] = "ABC";
        section.Positions.Add(position);
        block.Sections.Add(section);
        var row = Assert.Single(ExcelExistingUpdatePlan.Flatten(new[] { block }));
        Assert.Equal("Раздел 23 / В15", row.SectionPath);
        Assert.Equal("9.14.8-01", row.Basis);
        Assert.Contains("ABC", row.ExtraJson);
    }

    [Fact]
    public void ContractLockedForSupplementaryAgreementDisablesAllActions()
    {
        var plan = Build(new[] { Old() }, new[] { Incoming() }, true);
        Assert.False(Assert.Single(plan.Differences).SafeToApply);
        Assert.Throws<InvalidOperationException>(() => plan.Selected());
    }

    [Fact]
    public void NormalizeHandlesRussianWhitespaceYoAndUnitExponents()
    {
        Assert.Equal("решетка 2", ExcelExistingUpdatePlan.Normalize(" РЁШЕТКА\u00a0 2 "));
        Assert.Equal("м2", ExcelExistingUpdatePlan.Normalize(" м². ", true));
    }

    [Fact]
    public void OperationIdTracksEveryExplicitSelectionAndSourceHash()
    {
        var p = Build(new[] { Old() }, new[] { Incoming() });
        var first = p.OperationId();
        p.Differences[0].Apply = true;
        Assert.NotEqual(first, p.OperationId());
        Assert.Equal(first, Build(new[] { Old() }, new[] { Incoming() }).OperationId());
    }

    [Fact]
    public void AmbiguousRowsRequireUniqueExplicitOneToOneMappingAndFreshSelection()
    {
        var initial = Build(new[] { Old(1) }, new[] { Incoming(1), Incoming(2) });
        Assert.All(initial.Differences.Where(d => d.Source is not null),
            d => Assert.Equal(ExcelExistingChangeKind.Ambiguous, d.Kind));
        Assert.Equal(new long[] { 1 }, initial.MappingCandidates(2).Select(x => x.Id));
        var resolved = initial.WithExplicitMapping(2, 1);
        var mapped = Assert.Single(resolved.Differences.Where(d => d.Source?.Ordinal == 2));
        Assert.Equal(ExcelExistingChangeKind.Changed, mapped.Kind);
        Assert.Equal(1, mapped.Existing!.Id);
        Assert.Equal(ExcelExistingAction.Skip, mapped.Action);
        Assert.False(mapped.SafeForBulk); // legacy qty, per-row approval required
        var unresolved = Assert.Single(resolved.Differences.Where(d => d.Source?.Ordinal == 1));
        Assert.Equal(ExcelExistingChangeKind.Ambiguous, unresolved.Kind);
        Assert.False(unresolved.SafeToApply);
        Assert.Throws<InvalidOperationException>(() => resolved.Selected());
        mapped.Apply = true;
        Assert.Single(resolved.Selected());
        Assert.NotEqual(initial.PlanId, resolved.PlanId);
        Assert.NotEqual(initial.OperationId(), resolved.OperationId());
    }

    [Fact]
    public void LargeManualMappingKeepsReservedIdsOutOfOtherCandidates()
    {
        const int count = 2500;
        var old = Enumerable.Range(1, count).Select(i => Old(i)).ToArray();
        var incoming = Enumerable.Range(1, count).Select(i => Incoming(i)).ToArray();
        var mappings = Enumerable.Range(1, count - 2).ToDictionary(i => i, i => (long)i);
        var plan = ExcelExistingUpdatePlan.Build(123, "rev-123", Hash, old, incoming,
            new[] { "П1" }, explicitMappings: mappings);
        Assert.Equal(new long[] { count - 1, count }, plan.MappingCandidates(count).Select(x => x.Id));
        var resolved = plan.WithExplicitMapping(count, count);
        Assert.Equal(new long[] { count - 1 }, resolved.MappingCandidates(count - 1).Select(x => x.Id));
        Assert.All(resolved.Differences, x => Assert.False(x.Apply));
    }

    [Fact]
    public void ManualMappingRefusesManyToOneAndForeignOrInconsistentStableId()
    {
        var source = new[] { Incoming(1), Incoming(2) };
        var old = new[] { Old(1) };
        Assert.Throws<InvalidOperationException>(() =>
            ExcelExistingUpdatePlan.Build(123, "rev", Hash, old, source, new[] { "П1" },
                explicitMappings: new Dictionary<int, long> { [1] = 1, [2] = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelExistingUpdatePlan.Build(123, "rev", Hash, old, source, new[] { "П1" },
                explicitMappings: new Dictionary<int, long> { [1] = 999 }));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelExistingUpdatePlan.Build(123, "rev", Hash, old,
                new[] { Incoming(id: 2) }, new[] { "П1" },
                explicitMappings: new Dictionary<int, long> { [1] = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelExistingUpdatePlan.Build(123, "rev", Hash, old,
                new[] { Incoming() with { Unit = "кг" } }, new[] { "П1" },
                explicitMappings: new Dictionary<int, long> { [1] = 1 }));
    }

    [Fact]
    public void ExplicitMappingDoesNotBypassLinkedActOrFormulaProtections()
    {
        var source = new[] { Incoming(1), Incoming(2) };
        var old = new[] { Old(1, linked: true) };
        var resolved = Build(old, source).WithExplicitMapping(1, 1);
        var mapped = Assert.Single(resolved.Differences.Where(d => d.Source?.Ordinal == 1));
        Assert.Equal(ExcelExistingChangeKind.Conflict, mapped.Kind);
        Assert.False(mapped.SafeToApply);
        mapped.Apply = true;
        Assert.Equal(ExcelExistingAction.Skip, mapped.Action);
        Assert.Throws<InvalidOperationException>(() => resolved.Selected());
        var formulas = Build(new[] { Old(1, formulas: "{\"qty\":\"2*1\"}") }, source)
            .WithExplicitMapping(1, 1);
        Assert.False(Assert.Single(formulas.Differences.Where(d => d.Source?.Ordinal == 1)).SafeToApply);
    }

    [Theory]
    [InlineData(2_500)]
    [InlineData(10_000)]
    public void LargeEstimatePlanHasLinearOneToOneMatching(int count)
    {
        var originals = Enumerable.Range(1, count)
            .Select(i => Old(i, "Позиция " + i)).ToArray();
        var imports = Enumerable.Range(1, count)
            .Select(i => Incoming(i, "Позиция " + i, i + 0.5m)).ToArray();
        var p = Build(originals, imports);
        Assert.Equal(count, p.Differences.Count);
        Assert.All(p.Differences, d => Assert.Equal(ExcelExistingChangeKind.Changed, d.Kind));
    }

    [Fact]
    public void ValidationRejectsNegativeQuantityBeforeAnySqlWrite()
    {
        Assert.Throws<ArgumentException>(() =>
            Build(new[] { Old() }, new[] { Incoming(quantity: -1) }));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmeta;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Production;Integrated Security=true")]
    public void NewSqlServiceRejectsEveryNonPilotDatabaseBeforeConnecting(string cs)
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerExcelExistingUpdate.Read(cs, 1));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelExistingUpdate.WasCommitted(cs, 1, Hash));
    }
}
