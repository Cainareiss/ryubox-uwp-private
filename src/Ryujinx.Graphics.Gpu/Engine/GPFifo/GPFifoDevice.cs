using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Common.Logging;
using System.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    public static class UwpGpfifoTelemetry
    {
        internal static long EntriesQueued;
        internal static long HostBuffersQueued;
        internal static long Signals;
        internal static long Waits;
        internal static long WaitHits;
        internal static long Dispatches;
        internal static long BuffersProcessed;
        internal static long LastProgressAt;
        internal static long DispatchCompleted;
        internal static long ActiveEntryAddress;
        internal static long LastDispatchStartedAt;
        internal static long LastDispatchCompletedAt;
        internal static int QueueDepth;
        internal static int MaximumQueueDepth;
        internal static int ActiveStage;
        internal static int ActiveThread;
        internal static int ActiveEntryCount;
        internal static int ActiveWordIndex;
        internal static int ActiveMethod;
        internal static int ActiveSubChannel;
        internal static int ActiveArgument;
        internal static int ActiveIsLastCall;
        internal static readonly bool DetailEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_UWP_GPFIFO_DETAIL_TELEMETRY") == "1";

        public static string GetSnapshot()
        {
            return string.Format(
                "gpfifoEntries={0}, hostBuffers={1}, signals={2}, waits={3}, waitHits={4}, dispatches={5}, dispatchCompleted={6}, buffersProcessed={7}, queueDepth={8}, maxQueueDepth={9}, activeStage={10}, activeThread={11}, activeAddress=0x{12:X}, activeWords={13}, activeWord={14}, activeMethod=0x{15:X}, activeSubChannel={16}, activeArgument=0x{17:X8}, activeLast={18}, dispatchStarted={19}, dispatchCompletedAt={20}, lastProgress={21}",
                Interlocked.Read(ref EntriesQueued),
                Interlocked.Read(ref HostBuffersQueued),
                Interlocked.Read(ref Signals),
                Interlocked.Read(ref Waits),
                Interlocked.Read(ref WaitHits),
                Interlocked.Read(ref Dispatches),
                Interlocked.Read(ref DispatchCompleted),
                Interlocked.Read(ref BuffersProcessed),
                Volatile.Read(ref QueueDepth),
                Volatile.Read(ref MaximumQueueDepth),
                Volatile.Read(ref ActiveStage),
                Volatile.Read(ref ActiveThread),
                Interlocked.Read(ref ActiveEntryAddress),
                Volatile.Read(ref ActiveEntryCount),
                Volatile.Read(ref ActiveWordIndex),
                Volatile.Read(ref ActiveMethod),
                Volatile.Read(ref ActiveSubChannel),
                Volatile.Read(ref ActiveArgument),
                Volatile.Read(ref ActiveIsLastCall),
                Interlocked.Read(ref LastDispatchStartedAt),
                Interlocked.Read(ref LastDispatchCompletedAt),
                Interlocked.Read(ref LastProgressAt)) + "; " + RuntimeGpuTelemetry.Snapshot();
        }

        internal static void Reset()
        {
            RuntimeGpuTelemetry.Reset();
            Interlocked.Exchange(ref EntriesQueued, 0);
            Interlocked.Exchange(ref HostBuffersQueued, 0);
            Interlocked.Exchange(ref Signals, 0);
            Interlocked.Exchange(ref Waits, 0);
            Interlocked.Exchange(ref WaitHits, 0);
            Interlocked.Exchange(ref Dispatches, 0);
            Interlocked.Exchange(ref BuffersProcessed, 0);
            Interlocked.Exchange(ref LastProgressAt, 0);
            Interlocked.Exchange(ref DispatchCompleted, 0);
            Interlocked.Exchange(ref ActiveEntryAddress, 0);
            Interlocked.Exchange(ref LastDispatchStartedAt, 0);
            Interlocked.Exchange(ref LastDispatchCompletedAt, 0);
            Volatile.Write(ref QueueDepth, 0);
            Volatile.Write(ref MaximumQueueDepth, 0);
            Volatile.Write(ref ActiveStage, 0);
            Volatile.Write(ref ActiveThread, 0);
            Volatile.Write(ref ActiveEntryCount, 0);
            Volatile.Write(ref ActiveWordIndex, -1);
            Volatile.Write(ref ActiveMethod, -1);
            Volatile.Write(ref ActiveSubChannel, -1);
            Volatile.Write(ref ActiveArgument, 0);
            Volatile.Write(ref ActiveIsLastCall, 0);
        }

        internal static void SetQueueDepth(int depth)
        {
            Volatile.Write(ref QueueDepth, depth);
            int maximum = Volatile.Read(ref MaximumQueueDepth);
            while (depth > maximum)
            {
                int observed = Interlocked.CompareExchange(ref MaximumQueueDepth, depth, maximum);
                if (observed == maximum)
                {
                    break;
                }
                maximum = observed;
            }
        }
    }

    /// <summary>
    /// Represents a GPU General Purpose FIFO device.
    /// </summary>
    public sealed class GPFifoDevice : IDisposable
    {
        /// <summary>
        /// Indicates if the command buffer has pre-fetch enabled.
        /// </summary>
        private enum CommandBufferType
        {
            Prefetch,
            NoPrefetch,
        }

        /// <summary>
        /// Command buffer data.
        /// </summary>
        private struct CommandBuffer
        {
            /// <summary>
            /// Processor used to process the command buffer. Contains channel state.
            /// </summary>
            public GPFifoProcessor Processor;

            /// <summary>
            /// The type of the command buffer.
            /// </summary>
            public CommandBufferType Type;

            /// <summary>
            /// Fetched data.
            /// </summary>
            public int[] Words;

            /// <summary>
            /// The GPFIFO entry address (used in <see cref="CommandBufferType.NoPrefetch"/> mode).
            /// </summary>
            public ulong EntryAddress;

            /// <summary>
            /// The count of entries inside this GPFIFO entry.
            /// </summary>
            public uint EntryCount;

            /// <summary>
            /// Get the entries for the command buffer from memory.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The fetched data</returns>
            private readonly ReadOnlySpan<int> GetWords(MemoryManager memoryManager, bool flush)
            {
                return MemoryMarshal.Cast<byte, int>(memoryManager.GetSpan(EntryAddress, (int)EntryCount * 4, flush));
            }

            /// <summary>
            /// Prefetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            public void Prefetch(MemoryManager memoryManager)
            {
                Words = GetWords(memoryManager, true).ToArray();
            }

            /// <summary>
            /// Fetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The command buffer words</returns>
            public readonly ReadOnlySpan<int> Fetch(MemoryManager memoryManager, bool flush)
            {
                return Words ?? GetWords(memoryManager, flush);
            }
        }

        private readonly ConcurrentQueue<CommandBuffer> _commandBufferQueue;

        private GPFifoProcessor _prevChannelProcessor;

        private readonly bool _ibEnable;
        private readonly GpuContext _context;
        private readonly AutoResetEvent _event;

        private bool _interrupt;
        private int _flushSkips;
        private int _disposed;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO device.
        /// </summary>
        /// <param name="context">GPU context that the GPFIFO belongs to</param>
        internal GPFifoDevice(GpuContext context)
        {
            UwpGpfifoTelemetry.Reset();
            _commandBufferQueue = new ConcurrentQueue<CommandBuffer>();
            _ibEnable = true;
            _context = context;
            _event = new AutoResetEvent(false);
        }

        /// <summary>
        /// Signal the FIFO that there are new entries to process.
        /// </summary>
        public void SignalNewEntries()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref UwpGpfifoTelemetry.Signals);
            Interlocked.Exchange(ref UwpGpfifoTelemetry.LastProgressAt, Environment.TickCount64);
            try
            {
                _event.Set();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                // Shutdown raced the final nvservices submission.
            }
        }

        /// <summary>
        /// Push a GPFIFO entry in the form of a prefetched command buffer.
        /// It is intended to be used by nvservices to handle special cases.
        /// </summary>
        /// <param name="processor">Processor used to process <paramref name="commandBuffer"/></param>
        /// <param name="commandBuffer">The command buffer containing the prefetched commands</param>
        internal void PushHostCommandBuffer(GPFifoProcessor processor, int[] commandBuffer)
        {
            _commandBufferQueue.Enqueue(new CommandBuffer
            {
                Processor = processor,
                Type = CommandBufferType.Prefetch,
                Words = commandBuffer,
                EntryAddress = ulong.MaxValue,
                EntryCount = (uint)commandBuffer.Length,
            });
            Interlocked.Increment(ref UwpGpfifoTelemetry.HostBuffersQueued);
            UwpGpfifoTelemetry.SetQueueDepth(_commandBufferQueue.Count);
            Interlocked.Exchange(ref UwpGpfifoTelemetry.LastProgressAt, Environment.TickCount64);
        }

        /// <summary>
        /// Create a CommandBuffer from a GPFIFO entry.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffer pointed to by <paramref name="entry"/></param>
        /// <param name="entry">The GPFIFO entry</param>
        /// <returns>A new CommandBuffer based on the GPFIFO entry</returns>
        private static CommandBuffer CreateCommandBuffer(GPFifoProcessor processor, GPEntry entry)
        {
            CommandBufferType type = CommandBufferType.Prefetch;

            if (entry.Entry1Sync == Entry1Sync.Wait)
            {
                type = CommandBufferType.NoPrefetch;
            }

            ulong startAddress = ((ulong)entry.Entry0Get << 2) | ((ulong)entry.Entry1GetHi << 32);

            return new CommandBuffer
            {
                Processor = processor,
                Type = type,
                Words = null,
                EntryAddress = startAddress,
                EntryCount = (uint)entry.Entry1Length,
            };
        }

        /// <summary>
        /// Pushes GPFIFO entries.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffers pointed to by <paramref name="entries"/></param>
        /// <param name="entries">GPFIFO entries</param>
        internal void PushEntries(GPFifoProcessor processor, ReadOnlySpan<ulong> entries)
        {
            bool beforeBarrier = true;

            for (int index = 0; index < entries.Length; index++)
            {
                ulong entry = entries[index];

                CommandBuffer commandBuffer = CreateCommandBuffer(processor, Unsafe.As<ulong, GPEntry>(ref entry));

                if (beforeBarrier && commandBuffer.Type == CommandBufferType.Prefetch)
                {
                    commandBuffer.Prefetch(processor.MemoryManager);
                }

                if (commandBuffer.Type == CommandBufferType.NoPrefetch)
                {
                    beforeBarrier = false;
                }

                _commandBufferQueue.Enqueue(commandBuffer);
            }

            Interlocked.Add(ref UwpGpfifoTelemetry.EntriesQueued, entries.Length);
            UwpGpfifoTelemetry.SetQueueDepth(_commandBufferQueue.Count);
            Interlocked.Exchange(ref UwpGpfifoTelemetry.LastProgressAt, Environment.TickCount64);
        }

        /// <summary>
        /// Waits until commands are pushed to the FIFO.
        /// </summary>
        /// <returns>True if commands were received, false if wait timed out</returns>
        public bool WaitForCommands()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref UwpGpfifoTelemetry.Waits);
            bool hasCommands;
            try
            {
                hasCommands = !_commandBufferQueue.IsEmpty || (_event.WaitOne(8) && !_commandBufferQueue.IsEmpty);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }
            if (hasCommands)
            {
                Interlocked.Increment(ref UwpGpfifoTelemetry.WaitHits);
            }
            return hasCommands;
        }

        /// <summary>
        /// Processes commands pushed to the FIFO.
        /// </summary>
        public void DispatchCalls()
        {
            long busyStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref UwpGpfifoTelemetry.Dispatches);
            Interlocked.Exchange(ref UwpGpfifoTelemetry.LastDispatchStartedAt, Environment.TickCount64);
            Volatile.Write(ref UwpGpfifoTelemetry.ActiveThread, Environment.CurrentManagedThreadId);
            Volatile.Write(ref UwpGpfifoTelemetry.ActiveStage, 1);
            try
            {
                // Use this opportunity to also dispose any pending channels that were closed.
                _context.RunDeferredActions();

                // Process command buffers.
                while (_ibEnable && !_interrupt && _commandBufferQueue.TryDequeue(out CommandBuffer entry))
                {
                    UwpGpfifoTelemetry.SetQueueDepth(_commandBufferQueue.Count);
                    Interlocked.Exchange(ref UwpGpfifoTelemetry.ActiveEntryAddress, unchecked((long)entry.EntryAddress));
                    Volatile.Write(ref UwpGpfifoTelemetry.ActiveEntryCount, checked((int)entry.EntryCount));
                    Volatile.Write(ref UwpGpfifoTelemetry.ActiveStage, 2);
                    bool flushCommandBuffer = true;

                    if (_flushSkips != 0)
                    {
                        _flushSkips--;
                        flushCommandBuffer = false;
                    }

                    ReadOnlySpan<int> words = entry.Fetch(entry.Processor.MemoryManager, flushCommandBuffer);
                    Volatile.Write(ref UwpGpfifoTelemetry.ActiveStage, 3);

                    // If we are changing the current channel,
                    // we need to force all the host state to be updated.
                    if (_prevChannelProcessor != entry.Processor)
                    {
                        _prevChannelProcessor = entry.Processor;
                        entry.Processor.ForceAllDirty();
                    }

                    Volatile.Write(ref UwpGpfifoTelemetry.ActiveStage, 4);
                    entry.Processor.Process(entry.EntryAddress, words);
                    Interlocked.Increment(ref UwpGpfifoTelemetry.BuffersProcessed);
                    Interlocked.Exchange(ref UwpGpfifoTelemetry.LastProgressAt, Environment.TickCount64);
                }
            }
            finally
            {
                Interlocked.Increment(ref UwpGpfifoTelemetry.DispatchCompleted);
                RuntimeGpuTelemetry.RecordFifo(busyStart);
                Interlocked.Exchange(ref UwpGpfifoTelemetry.LastDispatchCompletedAt, Environment.TickCount64);
                UwpGpfifoTelemetry.SetQueueDepth(_commandBufferQueue.Count);
                Volatile.Write(ref UwpGpfifoTelemetry.ActiveStage, 0);
                Volatile.Write(ref UwpGpfifoTelemetry.ActiveThread, 0);
                Volatile.Write(ref UwpGpfifoTelemetry.ActiveEntryCount, 0);
                Interlocked.Exchange(ref UwpGpfifoTelemetry.ActiveEntryAddress, 0);
                _interrupt = false;
            }
        }

        /// <summary>
        /// Sets the number of flushes that should be skipped for subsequent command buffers.
        /// </summary>
        /// <remarks>
        /// This can improve performance when command buffer data only needs to be consumed by the GPU.
        /// </remarks>
        /// <param name="count">The amount of flushes that should be skipped</param>
        internal void SetFlushSkips(int count)
        {
            _flushSkips = count;
        }

        /// <summary>
        /// Interrupts command processing. This will break out of the DispatchCalls loop.
        /// </summary>
        public void Interrupt()
        {
            _interrupt = true;
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _event.Set();
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                }
            }
        }

        /// <summary>
        /// Disposes of resources used for GPFifo command processing.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _event.Set();
                }
                catch (ObjectDisposedException)
                {
                }

                _event.Dispose();
            }
        }
    }
}
