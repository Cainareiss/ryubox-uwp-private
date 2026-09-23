using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.D3D12;

// Guest IDs and native fence values are different timelines. Only completed
// native fences may advance the ID visible to guest resource reclamation.
internal sealed class D3D12SyncTracker
{
    private readonly object _sync = new();
    private readonly Queue<(ulong Id, ulong Fence)> _pending = new();
    private ulong _issued, _completed;
    private bool _hasIssued;
    internal int PendingCount { get { lock (_sync) return _pending.Count; } }

    internal void Record(ulong id, ulong fence, ulong completedFence)
    {
        lock (_sync)
        {
            if (_hasIssued && id <= _issued) throw new ArgumentException("Guest sync IDs must increase.", nameof(id));
            _issued = id; _hasIssued = true;
            _pending.Enqueue((id, fence));
            Advance(completedFence);
        }
    }
    private void Advance(ulong completedFence)
    {
        while (_pending.TryPeek(out var entry) && entry.Fence <= completedFence)
        { _completed = entry.Id; _pending.Dequeue(); }
    }
    internal ulong Current(ulong completedFence)
    { lock (_sync) { Advance(completedFence); return _completed; } }

    internal ulong FenceToWait(ulong id, ulong completedFence)
    {
        lock (_sync)
        {
            Advance(completedFence);
            if (_hasIssued && id <= _completed) return 0;
            foreach (var entry in _pending) if (entry.Id == id) return entry.Fence;
            throw new ArgumentException("Unknown guest sync ID.", nameof(id));
        }
    }
    internal void Clear()
    { lock (_sync) { _pending.Clear(); _issued = _completed = 0; _hasIssued = false; } }
}
