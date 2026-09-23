using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.XAudio2Uwp
{
    /// <summary>
    /// Native XAudio2 output for the UWP/Xbox host. The native shim copies
    /// every submitted buffer before this method returns, so managed arrays
    /// never remain pinned while the console audio engine consumes them.
    /// </summary>
    public sealed class XAudio2UwpHardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ConcurrentDictionary<XAudio2UwpHardwareDeviceSession, byte> _sessions = new();
        private float _volume = 1f;
        private int _disposeState;

        public static bool IsSupported => true;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                foreach (XAudio2UwpHardwareDeviceSession session in _sessions.Keys)
                {
                    session.UpdateMasterVolume(_volume);
                }
            }
        }

        public IHardwareDeviceSession OpenDeviceSession(
            Direction direction,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            if (direction != Direction.Output)
            {
                throw new NotSupportedException("XAudio2 UWP input capture is not implemented.");
            }

            sampleRate = sampleRate == 0 ? Constants.TargetSampleRate : sampleRate;
            channelCount = channelCount == 0 ? 2u : channelCount;

            XAudio2UwpHardwareDeviceSession session = new(
                this,
                memoryManager,
                sampleFormat,
                sampleRate,
                channelCount,
                _updateRequiredEvent);

            _sessions.TryAdd(session, 0);
            session.UpdateMasterVolume(_volume);
            return session;
        }

        internal void Unregister(XAudio2UwpHardwareDeviceSession session)
        {
            _sessions.TryRemove(session, out _);
        }

        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;
        public ManualResetEvent GetPauseEvent() => _pauseEvent;
        public bool SupportsDirection(Direction direction) => direction == Direction.Output;
        public bool SupportsSampleRate(uint sampleRate) => sampleRate is >= 8000 and <= 192000;
        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat is SampleFormat.PcmInt8 or SampleFormat.PcmInt16 or SampleFormat.PcmInt32 or SampleFormat.PcmFloat;
        // The Switch mixer normally requests 5.1. The Xbox UWP XAudio2 path is
        // substantially more reliable with a stereo source voice, so let the
        // existing compatibility layer perform its tested 5.1 -> stereo mix.
        public bool SupportsChannelCount(uint channelCount) => channelCount is >= 1 and <= 2;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            foreach (XAudio2UwpHardwareDeviceSession session in _sessions.Keys)
            {
                session.Dispose();
            }

            _pauseEvent.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    internal sealed unsafe class XAudio2UwpHardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly XAudio2UwpHardwareDeviceDriver _driver;
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly object _bufferLock = new();
        private readonly Dictionary<AudioBuffer, ulong> _bufferIdentifiers = new();
        private IntPtr _nativeSession;
        private long _nextIdentifier;
        private long _queueSequence;
        private long _fallbackPlayedSamples;
        private long _consecutiveSilentBuffers;
        private bool _hasObservedSignal;
        private float _sessionVolume = 1f;
        private float _masterVolume = 1f;
        private int _disposeState;

        public XAudio2UwpHardwareDeviceSession(
            XAudio2UwpHardwareDeviceDriver driver,
            IVirtualMemoryManager memoryManager,
            SampleFormat requestedSampleFormat,
            uint requestedSampleRate,
            uint requestedChannelCount,
            ManualResetEvent updateRequiredEvent)
            : base(memoryManager, requestedSampleFormat, requestedSampleRate, requestedChannelCount)
        {
            _driver = driver;
            _updateRequiredEvent = updateRequiredEvent;

            GetNativeFormat(requestedSampleFormat, out uint bitsPerSample, out int floatingPoint);
            int result = NativeCreateSession(
                requestedSampleRate,
                requestedChannelCount,
                bitsPerSample,
                floatingPoint,
                updateRequiredEvent.SafeWaitHandle.DangerousGetHandle(),
                out _nativeSession);

            if (result < 0 || _nativeSession == IntPtr.Zero)
            {
                _nativeSession = IntPtr.Zero;
                Logger.Error?.Print(
                    LogClass.Audio,
                    $"XAudio2 UWP session creation failed (HRESULT 0x{result:X8}); audio timing fallback remains active.");
            }
            else
            {
                Logger.Info?.Print(
                    LogClass.Audio,
                    $"XAudio2 UWP session created: {requestedSampleRate} Hz, {requestedChannelCount} channel(s), {requestedSampleFormat}.");
            }
        }

        private static void GetNativeFormat(SampleFormat format, out uint bitsPerSample, out int floatingPoint)
        {
            floatingPoint = 0;
            bitsPerSample = format switch
            {
                SampleFormat.PcmInt8 => 8,
                SampleFormat.PcmInt16 => 16,
                SampleFormat.PcmInt32 => 32,
                SampleFormat.PcmFloat => 32,
                _ => throw new NotSupportedException($"Unsupported XAudio2 sample format {format}."),
            };

            if (format == SampleFormat.PcmFloat)
            {
                floatingPoint = 1;
            }
        }

        private ulong GetOrCreateIdentifier(AudioBuffer buffer)
        {
            lock (_bufferLock)
            {
                if (!_bufferIdentifiers.TryGetValue(buffer, out ulong identifier))
                {
                    identifier = unchecked((ulong)Interlocked.Increment(ref _nextIdentifier));
                    _bufferIdentifiers.Add(buffer, identifier);
                }

                return identifier;
            }
        }

        public override bool RegisterBuffer(AudioBuffer buffer, byte[] samples)
        {
            if (!base.RegisterBuffer(buffer, samples))
            {
                return false;
            }

            GetOrCreateIdentifier(buffer);
            return true;
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            byte[] samples = buffer.Data;
            if (samples == null || samples.Length == 0 || _nativeSession == IntPtr.Zero)
            {
                Interlocked.Add(ref _fallbackPlayedSamples, unchecked((long)GetSampleCount(buffer)));
                _updateRequiredEvent.Set();
                return;
            }

            ulong identifier = GetOrCreateIdentifier(buffer);
            int byteCount = checked((int)Math.Min(buffer.DataSize, (ulong)samples.Length));
            long sequence = Interlocked.Increment(ref _queueSequence);
            fixed (byte* samplePointer = samples)
            {
                int nonZeroBytes = -1, peakInt16 = -1;
                // Sample diagnostics, never PCM submission or consumed callbacks.
                // Startup remains fully observed; sustained playback samples 1/32.
                bool analyzeSignal = sequence <= 8 || (sequence & 31) == 0;
                if (analyzeSignal) GetSignalTelemetry(samples, byteCount, out nonZeroBytes, out peakInt16);

                if (analyzeSignal && nonZeroBytes == 0)
                {
                    long silentBuffers = Interlocked.Increment(ref _consecutiveSilentBuffers);
                    if (_hasObservedSignal && (silentBuffers == 400 || silentBuffers % 1000 == 0))
                    {
                        Logger.Warning?.Print(
                            LogClass.Audio,
                            $"XAudio2 UWP sampled silence: consecutiveSilentSamples={silentBuffers}, " +
                            $"samplingStride=32, sequence={sequence}.");
                    }
                }
                else if (analyzeSignal)
                {
                    long silentBuffers = Interlocked.Exchange(ref _consecutiveSilentBuffers, 0);
                    if (!_hasObservedSignal || silentBuffers >= 100)
                    {
                        Logger.Info?.Print(
                            LogClass.Audio,
                            $"XAudio2 UWP signal {( _hasObservedSignal ? "resumed" : "first detected")}: " +
                            $"sequence={sequence}, previousSilentBuffers={silentBuffers}, " +
                            $"nonZeroBytes={nonZeroBytes}, peakInt16={peakInt16}.");
                    }

                    _hasObservedSignal = true;
                }

                int result = NativeSubmit(_nativeSession, identifier, samplePointer, (uint)byteCount);
                if (result < 0)
                {
                    Logger.Error?.Print(
                        LogClass.Audio,
                        $"XAudio2 UWP SubmitSourceBuffer failed (HRESULT 0x{result:X8}, bytes={byteCount}).");
                    Interlocked.Add(ref _fallbackPlayedSamples, unchecked((long)GetSampleCount(buffer)));
                    _updateRequiredEvent.Set();
                }

                if (sequence <= 8 || (sequence & 0xff) == 0 || result < 0)
                {
                    ulong* stats = stackalloc ulong[6];
                    int statsResult = NativeGetBufferStats(_nativeSession, stats, 6);
                    Ryujinx.Common.Logging.RuntimeDiagnosticLog.Write("audio-buffers",
                        $"submit=0x{result:X8} bytes={byteCount} stats=0x{statsResult:X8} inFlight={stats[0]} " +
                        $"inFlightBytes={stats[1]} poolCount={stats[2]} poolBytes={stats[3]} poolHits={stats[4]} packets={stats[5]} " +
                        $"workingSet={Environment.WorkingSet} managed={GC.GetTotalMemory(false)}");
                    Logger.Info?.Print(
                        LogClass.Audio,
                        $"XAudio2 UWP buffer #{sequence}: bytes={byteCount}, nonZeroBytes={nonZeroBytes}, " +
                        $"peakInt16={peakInt16}, format={RequestedSampleFormat}, channels={RequestedChannelCount}, " +
                        $"submit=0x{result:X8}, playedFrames={NativeGetPlayedFrameCount(_nativeSession)}, " +
                        $"voiceError=0x{NativeGetLastError(_nativeSession):X8}.");
                }
            }
        }

        private static void GetSignalTelemetry(byte[] samples, int byteCount, out int nonZeroBytes, out int peakInt16)
        {
            nonZeroBytes = 0;
            peakInt16 = 0;
            int count = Math.Min(byteCount, samples.Length);

            for (int index = 0; index < count; index++)
            {
                if (samples[index] != 0)
                {
                    nonZeroBytes++;
                }
            }

            for (int index = 0; index + 1 < count; index += 2)
            {
                int sample = (short)(samples[index] | (samples[index + 1] << 8));
                peakInt16 = Math.Max(peakInt16, sample == short.MinValue ? 32768 : Math.Abs(sample));
            }
        }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            if (_nativeSession == IntPtr.Zero)
            {
                return true;
            }

            lock (_bufferLock)
            {
                return !_bufferIdentifiers.TryGetValue(buffer, out ulong identifier)
                    || NativeIsConsumed(_nativeSession, identifier) != 0;
            }
        }

        public override void UnregisterBuffer(AudioBuffer buffer)
        {
            lock (_bufferLock)
            {
                if (_bufferIdentifiers.Remove(buffer, out ulong identifier) && _nativeSession != IntPtr.Zero)
                {
                    NativeReleasePacket(_nativeSession, identifier);
                }
            }
        }

        public override void Start()
        {
            if (_nativeSession != IntPtr.Zero)
            {
                int result = NativeStart(_nativeSession);
                LogNativeFailure("Start", result);
                Logger.Info?.Print(LogClass.Audio,
                    $"XAudio2 UWP Start: HRESULT=0x{result:X8}, voiceError=0x{NativeGetLastError(_nativeSession):X8}, " +
                    $"channels={RequestedChannelCount}, volume={_masterVolume * _sessionVolume:0.000}.");
            }
        }

        public override void Stop()
        {
            if (_nativeSession != IntPtr.Zero)
            {
                LogNativeFailure("Stop", NativeStop(_nativeSession));
            }
        }

        public override void PrepareToClose() => Stop();

        public override void SetVolume(float volume)
        {
            _sessionVolume = Math.Clamp(volume, 0f, 1f);
            ApplyVolume();
        }

        internal void UpdateMasterVolume(float volume)
        {
            _masterVolume = Math.Clamp(volume, 0f, 1f);
            ApplyVolume();
        }

        private void ApplyVolume()
        {
            if (_nativeSession != IntPtr.Zero)
            {
                LogNativeFailure("SetVolume", NativeSetVolume(_nativeSession, _masterVolume * _sessionVolume));
            }
        }

        public override float GetVolume() => _sessionVolume;

        public override ulong GetPlayedSampleCount()
        {
            ulong native = _nativeSession != IntPtr.Zero ? NativeGetPlayedFrameCount(_nativeSession) : 0;
            return native + unchecked((ulong)Math.Max(0, Interlocked.Read(ref _fallbackPlayedSamples)));
        }

        private static void LogNativeFailure(string operation, int result)
        {
            if (result < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"XAudio2 UWP {operation} failed (HRESULT 0x{result:X8}).");
            }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            _driver.Unregister(this);
            if (_nativeSession != IntPtr.Zero)
            {
                NativeDestroySession(_nativeSession);
                _nativeSession = IntPtr.Zero;
            }

            lock (_bufferLock)
            {
                _bufferIdentifiers.Clear();
            }
        }

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_CreateSession", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeCreateSession(uint sampleRate, uint channelCount, uint bitsPerSample, int floatingPoint, IntPtr updateEvent, out IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_DestroySession", CallingConvention = CallingConvention.StdCall)]
        private static extern void NativeDestroySession(IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_Submit", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeSubmit(IntPtr session, ulong identifier, void* samples, uint byteCount);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_IsConsumed", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeIsConsumed(IntPtr session, ulong identifier);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_ReleasePacket", CallingConvention = CallingConvention.StdCall)]
        private static extern void NativeReleasePacket(IntPtr session, ulong identifier);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_Start", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeStart(IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_Stop", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeStop(IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_SetVolume", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeSetVolume(IntPtr session, float volume);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_GetPlayedFrameCount", CallingConvention = CallingConvention.StdCall)]
        private static extern ulong NativeGetPlayedFrameCount(IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_GetLastError", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeGetLastError(IntPtr session);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpAudio_GetBufferStats", CallingConvention = CallingConvention.StdCall)]
        private static extern int NativeGetBufferStats(IntPtr session, ulong* values, uint count);
    }
}
