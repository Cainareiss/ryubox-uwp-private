using System;
using System.Collections.Generic;
using System.Threading;

namespace ARMeilleure.Translation.Cache
{
    // A boundary is valid only after ALL nested guest calls have returned.
    // Sleeping in a supervisor call is NOT a boundary: its return stack is live.
    public sealed class EpochReclaimer<T>
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<ulong, long> _readers = new();
        private readonly Queue<(long Epoch, T Value)> _retired = new();
        private readonly Action<T> _release;
        private long _epoch;
        private int _pending;
        public int Pending => Volatile.Read(ref _pending);

        public EpochReclaimer(Action<T> release) => _release = release;

        public void Enter(ulong reader)
        {
            lock (_sync) _readers.Add(reader, _epoch);
        }

        // Caller must remove every public dispatch reference BEFORE retiring.
        public void Retire(T value)
        {
            lock (_sync)
            {
                _retired.Enqueue((++_epoch, value));
                Volatile.Write(ref _pending, _retired.Count);
                Collect();
            }
        }

        public void Boundary(ulong reader)
        {
            if (Pending == 0) return;
            lock (_sync)
            {
                if (!_readers.ContainsKey(reader)) throw new InvalidOperationException("Unregistered JIT reader.");
                _readers[reader] = _epoch;
                Collect();
            }
        }

        public void Exit(ulong reader)
        {
            lock (_sync)
            {
                _readers.Remove(reader);
                Collect();
            }
        }

        private void Collect()
        {
            long oldest = _epoch;
            foreach (long epoch in _readers.Values) oldest = Math.Min(oldest, epoch);
            while (_retired.TryPeek(out var item) && item.Epoch <= oldest)
            {
                // Release synchronously, so shutdown cannot dispose the arena
                // while a detached collector is still accessing it.
                _release(item.Value);
                _retired.Dequeue();
            }
            Volatile.Write(ref _pending, _retired.Count);
        }
    }
}
