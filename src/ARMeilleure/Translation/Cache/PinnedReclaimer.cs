using System;
using System.Collections.Generic;

namespace ARMeilleure.Translation.Cache
{
    // Caller serializes lookup/publication, pinning, retirement and release with
    // the SAME lock. Valid only when dispatch never nests/tailcalls guest code.
    public sealed class PinnedReclaimer<T> where T : class
    {
        private readonly Dictionary<T, int> _pins = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<T> _retired = new(ReferenceEqualityComparer.Instance);
        private readonly Action<T> _release;
        public int Pending => _retired.Count;
        public PinnedReclaimer(Action<T> release) => _release = release;
        public bool IsPinned(T value) => _pins.ContainsKey(value);
        public bool IsRetired(T value) => _retired.Contains(value);
        public void Pin(T value)
        {
            if (_retired.Contains(value)) throw new InvalidOperationException("Cannot publish retired JIT code.");
            _pins.TryGetValue(value, out int count);
            _pins[value] = checked(count + 1);
        }
        public void Retire(T value)
        {
            if (IsPinned(value)) _retired.Add(value);
            else _release(value);
        }
        public void Unpin(T value)
        {
            if (!_pins.TryGetValue(value, out int count)) throw new InvalidOperationException("Unbalanced JIT pin.");
            if (count > 1) _pins[value] = count - 1;
            else
            {
                _pins.Remove(value);
                if (_retired.Remove(value)) _release(value);
            }
        }
    }
}
