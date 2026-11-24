namespace GmcSmeta.Core.Db;

/// <summary>
/// Отмена/повтор (Ctrl+Z / Ctrl+Y) для всего, что сделано внутри одного документа —
/// перенос smeta/undo.py. После каждой перезагрузки страницы документа берётся
/// снимок; при первом отличии предыдущий уходит в стек отмены. Отмена пишет
/// сохранённые строки обратно с их исходными id, поэтому ссылки (подпозиции,
/// каталог) остаются верными. Ограничения как в Python: только память работающей
/// программы (последние 30 шагов на документ); удаление документа или объекта
/// не отменяется — оно спрашивает подтверждение.
/// </summary>
public sealed class UndoManager
{
    public const int MaxSteps = 30;

    public enum ApplyStatus { Applied, Nothing, Moved, Failed, Indeterminate }

    /// <summary>Текущий снимок документа; снимает состояние вне транзакции (checkpoint).</summary>
    private readonly Func<int, UndoSnapshot?> _snapshot;

    /// <summary>
    /// Сравнение и восстановление под одной блокировкой записи: возвращает Applied
    /// с новым текущим снимком, Moved (документ ушёл вперёд — стеки сброшены)
    /// или Failed (восстановление не совпало/конфликт — операция откатилась).
    /// </summary>
    private readonly Func<int, UndoSnapshot, UndoSnapshot?, bool, UndoApplyResult> _applyDb;

    private readonly Dictionary<int, UndoSnapshot> _current = new();
    private readonly Dictionary<int, List<UndoSnapshot>> _undo = new();
    private readonly Dictionary<int, List<UndoSnapshot>> _redo = new();
    private bool _restoring;

    public UndoManager(Func<int, UndoSnapshot?> snapshot,
        Func<int, UndoSnapshot, UndoSnapshot?, bool, UndoApplyResult> applyDb)
    {
        _snapshot = snapshot;
        _applyDb = applyDb;
    }

    /// <summary>Вызывается после каждой (пере)загрузки страницы документа.</summary>
    public void Checkpoint(int contractId)
    {
        if (contractId <= 0 || _restoring) return;
        CheckpointSnapshot(contractId, _snapshot(contractId));
    }

    /// <summary>
    /// Ленивый базовый снимок: снимается один раз — перед первой локальной
    /// правкой документа, а не при каждом открытии/фоновой перезагрузке.
    /// </summary>
    public bool EnsureCheckpoint(int contractId)
    {
        if (contractId <= 0 || _restoring || _current.ContainsKey(contractId)) return false;
        var snapshot = _snapshot(contractId);
        if (snapshot is null) return false;
        CheckpointSnapshot(contractId, snapshot);
        return true;
    }

    public bool HasCheckpoint(int contractId) => _current.ContainsKey(contractId);

    /// <summary>
    /// Record a snapshot already captured by the background document loader.
    /// The UI thread never has to call Snapshot() again after rendering.
    /// </summary>
    public void CheckpointSnapshot(int contractId, UndoSnapshot? snapshot)
    {
        if (contractId <= 0 || _restoring || snapshot is null) return;
        if (snapshot.ContractId != contractId)
            throw new ArgumentException("Undo snapshot belongs to a different contract.", nameof(snapshot));
        Remember(contractId, snapshot);
    }

    /// <summary>Снимки до/после подтверждённой правки, снятые в одной транзакции с ней.</summary>
    public void RecordChange(int contractId, UndoSnapshot before, UndoSnapshot after)
    {
        if (!_current.TryGetValue(contractId, out var cur) || !cur.Equals(before))
            Forget(contractId);
        _current[contractId] = before;
        Remember(contractId, after);
    }

    /// <summary>Кто-то другой изменил документ: наши шаги перезаписали бы его правки.</summary>
    public void Forget(int contractId)
    {
        _undo.Remove(contractId);
        _redo.Remove(contractId);
        _current.Remove(contractId);
    }

    public bool CanUndo(int contractId) => _undo.TryGetValue(contractId, out var stack) && stack.Count > 0;

    public bool CanRedo(int contractId) => _redo.TryGetValue(contractId, out var stack) && stack.Count > 0;

    public ApplyStatus DoUndo(int contractId) => DoUndo(contractId, out _);
    public ApplyStatus DoRedo(int contractId) => DoRedo(contractId, out _);

    public ApplyStatus DoUndo(int contractId, out string? committedRevision) =>
        Apply(contractId, _undo, _redo, true, out committedRevision);

    public ApplyStatus DoRedo(int contractId, out string? committedRevision) =>
        Apply(contractId, _redo, _undo, false, out committedRevision);

    // ---------------------------------------------------------------- внутреннее

    private void Remember(int contractId, UndoSnapshot snap)
    {
        if (_current.TryGetValue(contractId, out var prev))
        {
            if (prev.Equals(snap)) return;         // «вкладка актуальна» — ничего не менялось
            var stack = _undo.TryGetValue(contractId, out var list)
                ? list : _undo[contractId] = new List<UndoSnapshot>();
            stack.Add(prev);
            if (stack.Count > MaxSteps) stack.RemoveAt(0);
            _redo.Remove(contractId);              // новая правка отменяет историю повтора
        }
        _current[contractId] = snap;
    }

    private ApplyStatus Apply(int contractId, Dictionary<int, List<UndoSnapshot>> source,
        Dictionary<int, List<UndoSnapshot>> target, bool undo, out string? committedRevision)
    {
        committedRevision = null;
        if (!source.TryGetValue(contractId, out var stack) || stack.Count == 0)
            return ApplyStatus.Nothing;
        _current.TryGetValue(contractId, out var expected);
        _restoring = true;
        try
        {
            var result = _applyDb(contractId, stack[^1], expected, undo);
            switch (result.Status)
            {
                case UndoApplyStatus.Moved:
                    // Правки коллег: наши шаги откатили бы их работу.
                    Forget(contractId);
                    if (result.Now is not null) _current[contractId] = result.Now;
                    return ApplyStatus.Moved;
                case UndoApplyStatus.Failed:
                    Forget(contractId);
                    if (result.Now is not null) _current[contractId] = result.Now;
                    return ApplyStatus.Failed;
                case UndoApplyStatus.Indeterminate:
                    // COMMIT may have succeeded, so never replay Undo/Redo blindly.
                    Forget(contractId);
                    return ApplyStatus.Indeterminate;
                case UndoApplyStatus.Nothing:
                    return ApplyStatus.Nothing;
            }
            stack.RemoveAt(stack.Count - 1);
            if (expected is not null)
            {
                var redoStack = target.TryGetValue(contractId, out var list)
                    ? list : target[contractId] = new List<UndoSnapshot>();
                redoStack.Add(expected);
            }
            _current[contractId] = result.Restored!;
            committedRevision = result.Revision;
            return ApplyStatus.Applied;
        }
        finally
        {
            _restoring = false;
        }
    }
}
