using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Логика стеков отмены/повтора — перенос smeta/undo.py. Подключения к БД нет.</summary>
public sealed class UndoManagerTests
{
    private static UndoSnapshot Snap(int id, string marker) =>
        new(id, new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["contract"] = new() { new() { ["id"] = (long)id, ["name"] = marker } },
        });

    /// <summary>Сервер-заглушка с семантикой SqlServerUndo.Apply: сравнение, запись, верификация.</summary>
    private sealed class FakeServer
    {
        public UndoSnapshot? State;
        public int ApplyCalls;
        public UndoApplyResult? Forced;

        public FakeServer(int id) => State = Snap(id, "v0");

        public UndoManager Manager()
        {
            var state = this;
            return new UndoManager(
                _ => state.State,
                (cid, target, expected, undo) =>
                {
                    state.ApplyCalls++;
                    if (state.Forced is not null) return state.Forced.Value;
                    if (expected is not null && state.State is not null && !state.State.Equals(expected))
                        return new UndoApplyResult(UndoApplyStatus.Moved, null, state.State);
                    state.State = target;
                    return new UndoApplyResult(UndoApplyStatus.Applied, state.State, null);
                });
        }
    }

    [Fact]
    public void SameSnapshot_Twice_NoUndoStep()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        undo.Checkpoint(1);
        Assert.False(undo.CanUndo(1));
        Assert.Equal(0, db.ApplyCalls);
    }

    [Fact]
    public void Change_IsRecorded_UndoRestoresSnapshot()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);                       // v0
        db.State = Snap(1, "v1");
        undo.Checkpoint(1);                       // v1 → v0 в стек
        Assert.True(undo.CanUndo(1));
        Assert.False(undo.CanRedo(1));

        Assert.Equal(UndoManager.ApplyStatus.Applied, undo.DoUndo(1));
        Assert.False(undo.CanUndo(1));
        Assert.True(undo.CanRedo(1));
        Assert.Equal("v0", db.State!.Rows("contract")[0]["name"]);
    }

    [Fact]
    public void Redo_AppliesAgain_ThenNewChangeClearsRedo()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        db.State = Snap(1, "v1");
        undo.Checkpoint(1);
        undo.DoUndo(1);

        Assert.Equal(UndoManager.ApplyStatus.Applied, undo.DoRedo(1));
        Assert.Equal("v1", db.State!.Rows("contract")[0]["name"]);
        Assert.False(undo.CanRedo(1));
        Assert.True(undo.CanUndo(1));

        db.State = Snap(1, "v2");
        undo.Checkpoint(1);
        Assert.False(undo.CanRedo(1));            // новая правка отменяет историю повтора
        Assert.True(undo.CanUndo(1));
    }

    [Fact]
    public void MovedByColleague_ForgetsStacksAndKeepsFreshState()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        db.State = Snap(1, "v1");
        undo.Checkpoint(1);
        db.Forced = new UndoApplyResult(UndoApplyStatus.Moved, null, Snap(1, "colleague"));

        Assert.Equal(UndoManager.ApplyStatus.Moved, undo.DoUndo(1));
        Assert.False(undo.CanUndo(1));
        Assert.False(undo.CanRedo(1));
        db.State = Snap(1, "colleague");          // сервер отвечает тем же состоянием
        undo.Checkpoint(1);                       // «вкладка актуальна» — нового шага нет
        Assert.False(undo.CanUndo(1));
    }

    [Fact]
    public void FailedRestore_ForgetsStacks()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        db.State = Snap(1, "v1");
        undo.Checkpoint(1);
        db.Forced = new UndoApplyResult(UndoApplyStatus.Failed, null, Snap(1, "v1"));

        Assert.Equal(UndoManager.ApplyStatus.Failed, undo.DoUndo(1));
        Assert.False(undo.CanUndo(1));
    }

    [Fact]
    public void EmptyStack_Nothing()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        Assert.Equal(UndoManager.ApplyStatus.Nothing, undo.DoUndo(1));
        Assert.Equal(UndoManager.ApplyStatus.Nothing, undo.DoRedo(1));
    }

    [Fact]
    public void Stack_IsCapped_At30Steps()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        for (var i = 1; i <= 35; i++)
        {
            db.State = Snap(1, "v" + i);
            undo.Checkpoint(1);
        }
        for (var i = 0; i < UndoManager.MaxSteps; i++)
            Assert.Equal(UndoManager.ApplyStatus.Applied, undo.DoUndo(1));
        Assert.Equal(UndoManager.ApplyStatus.Nothing, undo.DoUndo(1)); // 5 старых шагов вытеснены
        Assert.Equal("v5", db.State!.Rows("contract")[0]["name"]);     // остался на 5-м состоянии
    }

    [Fact]
    public void Forget_ClearsEverything()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);
        db.State = Snap(1, "v1");
        undo.Checkpoint(1);
        undo.Forget(1);
        Assert.False(undo.CanUndo(1));
    }

    [Fact]
    public void RecordChange_BridgesOptimisticWrites()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        undo.Checkpoint(1);                       // v0 — текущее состояние вкладки
        var before = db.State!;                   // снимок, снятый в транзакции правки
        db.State = Snap(1, "v1");                 // наша правка прошла на сервере
        undo.RecordChange(1, before, db.State!);
        Assert.True(undo.CanUndo(1));
        Assert.Equal(UndoManager.ApplyStatus.Applied, undo.DoUndo(1));
        Assert.Equal("v0", db.State!.Rows("contract")[0]["name"]);
    }

    [Fact]
    public void CheckpointSnapshot_UsesAlreadyLoadedData_WithoutReadingDatabase()
    {
        var calls = 0;
        var manager = new UndoManager(
            _ => { calls++; return Snap(1, "unexpected SQL call"); },
            (_, target, _, _) => new UndoApplyResult(UndoApplyStatus.Applied, target, null));
        manager.CheckpointSnapshot(1, Snap(1, "before"));
        manager.CheckpointSnapshot(1, Snap(1, "after"));
        Assert.True(manager.CanUndo(1));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CheckpointSnapshot_RejectsWrongContractWithoutCorruptingHistory()
    {
        var manager = new UndoManager(
            _ => throw new Exception("UI should not fetch snapshots"),
            (_, target, _, _) => new UndoApplyResult(UndoApplyStatus.Applied, target, null));
        Assert.Throws<ArgumentException>(() => manager.CheckpointSnapshot(2, Snap(1, "wrong")));
        Assert.False(manager.CanUndo(2));
        manager.CheckpointSnapshot(2, null);
        Assert.False(manager.CanUndo(2));
    }

    [Fact]
    public void EnsureCheckpoint_ReadsDatabaseOnlyOnce()
    {
        var calls = 0;
        var manager = new UndoManager(
            id => { calls++; return Snap(id, "v0"); },
            (_, target, _, _) => new UndoApplyResult(UndoApplyStatus.Applied, target, null));
        Assert.False(manager.HasCheckpoint(1));
        Assert.True(manager.EnsureCheckpoint(1));
        Assert.True(manager.HasCheckpoint(1));
        Assert.False(manager.EnsureCheckpoint(1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EnsureCheckpoint_WithoutDocument_StaysEmpty()
    {
        var manager = new UndoManager(
            _ => null,
            (_, target, _, _) => new UndoApplyResult(UndoApplyStatus.Applied, target, null));
        Assert.False(manager.EnsureCheckpoint(1));
        Assert.False(manager.HasCheckpoint(1));
    }

    [Fact]
    public void LazyBaseline_BeforeFirstEdit_EnablesUndoAfterReload()
    {
        var db = new FakeServer(1);
        var undo = db.Manager();
        Assert.True(undo.EnsureCheckpoint(1));      // снимок «до» первой правки
        db.State = Snap(1, "v1");                   // правка прошла на сервере
        undo.CheckpointSnapshot(1, db.State!);      // перезагрузка после правки
        Assert.True(undo.CanUndo(1));
        Assert.Equal(UndoManager.ApplyStatus.Applied, undo.DoUndo(1));
        Assert.Equal("v0", db.State!.Rows("contract")[0]["name"]);
    }

    [Fact]
    public void SnapshotEquality_ByContent()
    {
        Assert.Equal(Snap(1, "x"), Snap(1, "x"));
        Assert.NotEqual(Snap(1, "x"), Snap(1, "y"));
        Assert.NotEqual(Snap(1, "x"), Snap(2, "x"));
    }
    [Fact]
    public void UnknownSqlCommitNeverReplaysUndoOrRedo()
    {
        var server = new FakeServer(1);
        var manager = server.Manager();
        manager.Checkpoint(1);
        server.State = Snap(1, "new value");
        manager.Checkpoint(1);
        server.Forced = new UndoApplyResult(UndoApplyStatus.Indeterminate, null, null);

        Assert.Equal(UndoManager.ApplyStatus.Indeterminate, manager.DoUndo(1));
        Assert.False(manager.CanUndo(1));
        Assert.False(manager.CanRedo(1));
        var calls = server.ApplyCalls;
        Assert.Equal(UndoManager.ApplyStatus.Nothing, manager.DoUndo(1));
        Assert.Equal(calls, server.ApplyCalls);
    }


    [Fact]
    public void UndoAndRedoReturnOnlyTheirOwnCommittedRevision()
    {
        var server = new FakeServer(1);
        var manager = server.Manager();
        manager.Checkpoint(1);
        server.State = Snap(1, "v1");
        manager.Checkpoint(1);
        server.Forced = new UndoApplyResult(UndoApplyStatus.Applied, Snap(1, "v0"), null)
            { Revision = "undo-commit" };
        Assert.Equal(UndoManager.ApplyStatus.Applied, manager.DoUndo(1, out var undoRevision));
        Assert.Equal("undo-commit", undoRevision);
        Assert.False(GmcSmeta.Core.Domain.EstimateUndoRevisionPolicy.IsForeignRevision(undoRevision, "undo-commit"));
        Assert.True(manager.CanRedo(1));
        server.Forced = new UndoApplyResult(UndoApplyStatus.Applied, Snap(1, "v1"), null)
            { Revision = "redo-commit" };
        Assert.Equal(UndoManager.ApplyStatus.Applied, manager.DoRedo(1, out var redoRevision));
        Assert.Equal("redo-commit", redoRevision);
        Assert.True(GmcSmeta.Core.Domain.EstimateUndoRevisionPolicy.IsForeignRevision(redoRevision, "foreign"));
    }
}
