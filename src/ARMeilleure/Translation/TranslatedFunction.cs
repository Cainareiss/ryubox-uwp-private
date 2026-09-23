using ARMeilleure.Common;

namespace ARMeilleure.Translation
{
    class TranslatedFunction
    {
        private readonly GuestFunction _func; // Ensure that this delegate will not be garbage collected.

        public nint FuncPointer { get; }
        public Counter<uint> CallCounter { get; }
        public ulong GuestSize { get; }
        public bool HighCq { get; }
        public ulong GuestAddress { get; set; }
        public long LastLookupTick;

        public TranslatedFunction(GuestFunction func, nint funcPointer, Counter<uint> callCounter, ulong guestSize, bool highCq)
        {
            _func = func;
            FuncPointer = funcPointer;
            CallCounter = callCounter;
            GuestSize = guestSize;
            HighCq = highCq;
            LastLookupTick = System.Environment.TickCount64;
        }

        public ulong Execute(State.ExecutionContext context)
        {
            return _func(context.NativeContextPtr);
        }

        public ulong Execute(WrapperFunction dispatcher, State.ExecutionContext context)
        {
            return dispatcher(context.NativeContextPtr, (ulong)FuncPointer);
        }
    }
}
