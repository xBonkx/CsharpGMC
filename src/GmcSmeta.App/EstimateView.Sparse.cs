using System;
using System.Collections.Generic;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // PERF-014: sparse readback. Only confirmed single-cell writes accumulate
    // here; any structural or non-whitelisted mutation forces the mandatory
    // full reload. State is touched from both the UI thread and the write
    // queue worker, hence the lock.
    private readonly object _sparseSync = new();
    private readonly List<long> _sparseRowIds = new();
    private bool _sparseIneligible;

    /// <summary>Called by the cell write funnel after SQL confirmed the edit.</summary>
    private void NoteSparseCellWrite(long positionId, string field)
    {
        if (!EstimateReader.IsSparseReadbackSafe(field) ||
            positionId is < 1 or > int.MaxValue)
        {
            InvalidateSparseReadback();
            return;
        }
        lock (_sparseSync) _sparseRowIds.Add(positionId);
    }

    /// <summary>
    /// Any mutation outside the single-cell funnel (insert, paste of new rows,
    /// sections, formulas fill, header, stages, undo restore, …) must call
    /// this before its write: sparse merging would silently miss such changes.
    /// </summary>
    private void InvalidateSparseReadback()
    {
        lock (_sparseSync)
        {
            _sparseRowIds.Clear();
            _sparseIneligible = true;
        }
    }

    /// <summary>
    /// Worker: drain the pending confirmed cell ids, or return null when a
    /// full reload is required. The flag resets here so the next reload after
    /// this authoritative read may try the sparse path again.
    /// </summary>
    private List<long>? TakeSparseEligibleEdits()
    {
        lock (_sparseSync)
        {
            if (_sparseIneligible)
            {
                _sparseIneligible = false;
                _sparseRowIds.Clear();
                return null;
            }
            if (_sparseRowIds.Count == 0) return null;
            var ids = new List<long>(_sparseRowIds);
            _sparseRowIds.Clear();
            return ids;
        }
    }

    /// <summary>
    /// True when a write finished while the sparse snapshot was being read:
    /// the drained list cannot cover it, so the caller must fully reload.
    /// The pending ids stay queued for the next readback.
    /// </summary>
    private bool AnySparseEditsSinceDrain()
    {
        lock (_sparseSync) return _sparseIneligible || _sparseRowIds.Count > 0;
    }
}
