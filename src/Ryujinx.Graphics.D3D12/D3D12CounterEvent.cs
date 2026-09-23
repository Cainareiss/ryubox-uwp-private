using System;
using System.Collections.Generic;
using System.Linq;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

internal sealed class D3D12QuerySegment
{
    internal ulong Id, Fence;
    internal ulong? Value;
}

public sealed class D3D12CounterEvent : ICounterEvent
{
    private readonly D3D12Renderer _renderer;
    private D3D12QuerySegment[] _segments;
    private readonly ulong _base;
    private readonly float _divisor;
    private EventHandler<ulong> _handler;
    private readonly object _sync = new();
    private bool _complete, _reserved;
    private bool _invalid;
    public bool Invalid { get { lock (_sync) return _invalid; } set { lock (_sync) _invalid = value; } }
    internal D3D12CounterEvent(D3D12Renderer renderer, ulong value, IEnumerable<D3D12QuerySegment> segments,
        EventHandler<ulong> handler, float divisor, bool reserved)
    {
        if (!float.IsFinite(divisor) || divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        _renderer = renderer; _base = value; _segments = segments.ToArray(); _handler = handler; _divisor = divisor; _reserved = reserved;
    }
    public bool ReserveForHostAccess()
    { lock (_sync) { ObjectDisposedException.ThrowIf(_segments == null, this); _reserved = true; return true; } }
    internal bool TryComplete(ulong completed)
    {
        lock (_sync)
        {
            if (_segments == null || _complete) return true;
            foreach (var segment in _segments) if (!_renderer.Resolve(segment, completed)) return false;
            ulong value = _base;
            foreach (var segment in _segments) value = checked(value + segment.Value.Value);
            _complete = true;
            var handler = _handler; _handler = null;
            // Serialize callback delivery with Dispose/Invalid. Dispose returning
            // guarantees that this event cannot deliver a new callback afterward.
            if (!_invalid) handler?.Invoke(this, (ulong)(value / (double)_divisor));
            return true;
        }
    }
    public void Flush()
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_segments == null, this);
        // Never hold the event lock while waiting for the GPU dispatcher.
        _renderer.RunOnGpuThread(() => { ulong fence = _renderer.Flush(); _renderer.Device.Wait(fence); TryComplete(fence); });
    }
    public void Dispose() { lock (_sync) { _segments = null; _handler = null; _reserved = false; } }
}
