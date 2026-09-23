using Ryujinx.Audio.Common;
using Ryujinx.Audio.Renderer.Common;
using Ryujinx.Audio.Renderer.Dsp.State;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Ryujinx.Audio.Renderer.Parameter;

namespace Ryujinx.Audio.Renderer.Dsp
{
    public static class DataSourceHelper
    {
        private const int FixedPointPrecision = 15;

        private const int UwpPcmGuardMinimumSeconds = 2;
        private const int UwpPcmGuardMinimumBytes = 1024 * 1024;
        private const int UwpPcmGuardMaximumWaitMilliseconds = 5000;
        private const int UwpPcmGuardProbeBytes = 512 * 1024;
        private const int UwpPcmGuardProbeIntervalMilliseconds = 250;

        private sealed class UwpPcmGuardState
        {
            public long FirstSeenAt;
            public long NextProbeAt;
            public int HoldCount;
            public bool StartupReleased;
            public bool CurrentSignalSeen;
            public bool ProbeSignalSeen;
            public bool RecoveryIssued;
        }

        private static readonly object _uwpPcmGuardLock = new();
        private static readonly Dictionary<(int NodeId, ulong Buffer), UwpPcmGuardState> _uwpPcmGuards = new();

        public struct WaveBufferInformation
        {
            public int NodeId;
            public uint SourceSampleRate;
            public float Pitch;
            public ulong ExtraParameter;
            public ulong ExtraParameterSize;
            public int ChannelIndex;
            public int ChannelCount;
            public DecodingBehaviour DecodingBehaviour;
            public SampleRateConversionQuality SrcQuality;
            public SampleFormat SampleFormat;
        }

        public static void ResetUwpPcmStartupGuard()
        {
            lock (_uwpPcmGuardLock)
            {
                _uwpPcmGuards.Clear();
            }
        }

        private static bool ContainsNonZeroPcm16(ReadOnlySpan<short> samples)
        {
            for (int index = 0; index < samples.Length; index++)
            {
                if (samples[index] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldHoldOrRecoverUwpPcm16(
            IVirtualMemoryManager memoryManager,
            ref WaveBufferInformation info,
            ref WaveBuffer waveBuffer,
            ref VoiceState voiceState,
            ReadOnlySpan<short> currentSamples,
            int targetWaveBufferSampleCount)
        {
            AppContext.TryGetSwitch("Ryujinx.Uwp.AudioPcmStartupGuard", out bool enabled);

            if (!enabled || info.ChannelIndex != 0 || waveBuffer.Buffer == 0 || waveBuffer.BufferSize < UwpPcmGuardMinimumBytes ||
                targetWaveBufferSampleCount < (long)info.SourceSampleRate * UwpPcmGuardMinimumSeconds)
            {
                return false;
            }

            long now = Environment.TickCount64;
            bool currentHasSignal = ContainsNonZeroPcm16(currentSamples);
            bool shouldHold = false;
            bool shouldRecover = false;
            int holdCount = 0;
            long waitedMilliseconds = 0;

            lock (_uwpPcmGuardLock)
            {
                (int NodeId, ulong Buffer) key = (info.NodeId, waveBuffer.Buffer);

                if (!_uwpPcmGuards.TryGetValue(key, out UwpPcmGuardState guard))
                {
                    // Bound stale diagnostic state when games create many transient voices.
                    if (_uwpPcmGuards.Count >= 512)
                    {
                        _uwpPcmGuards.Clear();
                    }

                    guard = new UwpPcmGuardState
                    {
                        FirstSeenAt = now,
                        NextProbeAt = now,
                    };
                    _uwpPcmGuards.Add(key, guard);
                }

                if (currentHasSignal)
                {
                    guard.CurrentSignalSeen = true;
                    guard.ProbeSignalSeen = true;
                    guard.StartupReleased = true;
                    return false;
                }

                if (now >= guard.NextProbeAt)
                {
                    guard.NextProbeAt = now + UwpPcmGuardProbeIntervalMilliseconds;

                    try
                    {
                        int probeBytes = (int)Math.Min((uint)UwpPcmGuardProbeBytes, waveBuffer.BufferSize) & ~1;
                        ReadOnlySpan<short> probe = MemoryMarshal.Cast<byte, short>(memoryManager.GetSpan(waveBuffer.Buffer, probeBytes));
                        guard.ProbeSignalSeen |= ContainsNonZeroPcm16(probe);
                    }
                    catch (Exception exception)
                    {
                        guard.StartupReleased = true;
                        Logger.Warning?.Print(
                            LogClass.AudioRenderer,
                            $"Xbox PCM startup guard probe failed for node={info.NodeId}, buffer=0x{waveBuffer.Buffer:X}: {exception.GetType().Name}: {exception.Message}");
                        return false;
                    }
                }

                waitedMilliseconds = now - guard.FirstSeenAt;

                if (guard.ProbeSignalSeen)
                {
                    // The Xbox may expose a long PCM wave buffer to the renderer
                    // before its guest producer has populated it. If data appeared
                    // behind a cursor that only consumed zeroes, rewind exactly once.
                    if (!guard.CurrentSignalSeen && !guard.RecoveryIssued &&
                        voiceState.WaveBufferConsumed == 0 && voiceState.Offset > 0)
                    {
                        guard.RecoveryIssued = true;
                        guard.StartupReleased = true;
                        shouldRecover = true;
                    }
                    else
                    {
                        guard.StartupReleased = true;
                    }
                }
                else if (!guard.StartupReleased && voiceState.WaveBufferConsumed == 0 &&
                    voiceState.Offset == 0 && voiceState.PlayedSampleCount == 0 &&
                    waitedMilliseconds < UwpPcmGuardMaximumWaitMilliseconds)
                {
                    guard.HoldCount++;
                    holdCount = guard.HoldCount;
                    shouldHold = true;
                }
                else
                {
                    guard.StartupReleased = true;
                }
            }

            if (shouldRecover)
            {
                Logger.Warning?.Print(
                    LogClass.AudioRenderer,
                    $"Xbox PCM late-fill recovery: node={info.NodeId}, buffer=0x{waveBuffer.Buffer:X}, size={waveBuffer.BufferSize}, oldOffset={voiceState.Offset}, waitedMs={waitedMilliseconds}.");
                voiceState.Offset = 0;
                voiceState.PlayedSampleCount = 0;
                voiceState.Fraction = 0;
                voiceState.Pitch.AsSpan().Clear();
                return true;
            }

            if (shouldHold)
            {
                if (holdCount == 1 || holdCount % 200 == 0)
                {
                    Logger.Info?.Print(
                        LogClass.AudioRenderer,
                        $"Xbox PCM startup guard holding empty long buffer: node={info.NodeId}, buffer=0x{waveBuffer.Buffer:X}, size={waveBuffer.BufferSize}, holds={holdCount}, waitedMs={waitedMilliseconds}.");
                }

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetPitchLimitBySrcQuality(SampleRateConversionQuality quality)
        {
            return quality switch
            {
                SampleRateConversionQuality.Default or SampleRateConversionQuality.Low => 4,
                SampleRateConversionQuality.High => 8,
                _ => throw new ArgumentException(quality.ToString()),
            };
        }

        public static void ProcessWaveBuffers(IVirtualMemoryManager memoryManager, Span<float> outputBuffer, ref WaveBufferInformation info, Span<WaveBuffer> wavebuffers, ref VoiceState voiceState, uint targetSampleRate, int sampleCount)
        {
            const int TempBufferSize = 0x3F00;

            Span<short> tempBuffer = stackalloc short[TempBufferSize];

            float sampleRateRatio = (float)info.SourceSampleRate / targetSampleRate * info.Pitch;

            float fraction = voiceState.Fraction;
            int waveBufferIndex = (int)voiceState.WaveBufferIndex;
            ulong playedSampleCount = voiceState.PlayedSampleCount;
            int offset = voiceState.Offset;
            uint waveBufferConsumed = voiceState.WaveBufferConsumed;

            int pitchMaxLength = GetPitchLimitBySrcQuality(info.SrcQuality);

            int totalNeededSize = (int)MathF.Truncate(fraction + sampleRateRatio * sampleCount);

            if (totalNeededSize + pitchMaxLength <= TempBufferSize && totalNeededSize >= 0)
            {
                int sourceSampleCountToProcess = sampleCount;

                int maxSampleCountPerIteration = Math.Min((int)MathF.Truncate((TempBufferSize - fraction) / sampleRateRatio), sampleCount);

                bool isStarving = false;

                int i = 0;

                while (i < sourceSampleCountToProcess)
                {
                    int tempBufferIndex = 0;

                    if ((info.DecodingBehaviour & DecodingBehaviour.SkipPitchAndSampleRateConversion) != DecodingBehaviour.SkipPitchAndSampleRateConversion)
                    {
                        voiceState.Pitch.AsSpan()[..pitchMaxLength].CopyTo(tempBuffer);
                        tempBufferIndex += pitchMaxLength;
                    }

                    int sampleCountToProcess = Math.Min(sourceSampleCountToProcess, maxSampleCountPerIteration);

                    int y = 0;

                    int sampleCountToDecode = (int)MathF.Truncate(fraction + sampleRateRatio * sampleCountToProcess);

                    while (y < sampleCountToDecode)
                    {
                        if (waveBufferIndex >= Constants.VoiceWaveBufferCount)
                        {
                            waveBufferIndex = 0;
                            playedSampleCount = 0;
                        }

                        if (!voiceState.IsWaveBufferValid[waveBufferIndex])
                        {
                            isStarving = true;
                            break;
                        }

                        ref WaveBuffer waveBuffer = ref wavebuffers[waveBufferIndex];

                        if (offset == 0 && info.SampleFormat == SampleFormat.Adpcm && waveBuffer.Context != 0)
                        {
                            voiceState.LoopContext = memoryManager.Read<AdpcmLoopContext>(waveBuffer.Context);
                        }

                        Span<short> tempSpan = tempBuffer[(tempBufferIndex + y)..];

                        int decodedSampleCount = -1;

                        int targetSampleStartOffset;
                        int targetSampleEndOffset;

                        if (voiceState.LoopCount > 0 && waveBuffer.LoopStartSampleOffset != 0 && waveBuffer.LoopEndSampleOffset != 0 && waveBuffer.LoopStartSampleOffset <= waveBuffer.LoopEndSampleOffset)
                        {
                            targetSampleStartOffset = (int)waveBuffer.LoopStartSampleOffset;
                            targetSampleEndOffset = (int)waveBuffer.LoopEndSampleOffset;
                        }
                        else
                        {
                            targetSampleStartOffset = (int)waveBuffer.StartSampleOffset;
                            targetSampleEndOffset = (int)waveBuffer.EndSampleOffset;
                        }

                        int targetWaveBufferSampleCount = targetSampleEndOffset - targetSampleStartOffset;

                        switch (info.SampleFormat)
                        {
                            case SampleFormat.Adpcm:
                                ReadOnlySpan<byte> waveBufferAdpcm = ReadOnlySpan<byte>.Empty;

                                if (waveBuffer.Buffer != 0 && waveBuffer.BufferSize != 0)
                                {
                                    // TODO: we are possibly copying a lot of unneeded data here, we should only take what we need.
                                    waveBufferAdpcm = memoryManager.GetSpan(waveBuffer.Buffer, (int)waveBuffer.BufferSize);
                                }

                                ReadOnlySpan<short> coefficients = MemoryMarshal.Cast<byte, short>(memoryManager.GetSpan(info.ExtraParameter, (int)info.ExtraParameterSize));
                                decodedSampleCount = AdpcmHelper.Decode(tempSpan, waveBufferAdpcm, targetSampleStartOffset, targetSampleEndOffset, offset, sampleCountToDecode - y, coefficients, ref voiceState.LoopContext);
                                break;
                            case SampleFormat.PcmInt16:
                                ReadOnlySpan<short> waveBufferPcm16 = ReadOnlySpan<short>.Empty;

                                if (waveBuffer.Buffer != 0 && waveBuffer.BufferSize != 0)
                                {
                                    ulong bufferOffset = waveBuffer.Buffer + PcmHelper.GetBufferOffset<short>(targetSampleStartOffset, offset, info.ChannelCount);
                                    int bufferSize = PcmHelper.GetBufferSize<short>(targetSampleStartOffset, targetSampleEndOffset, offset, sampleCountToDecode - y) * info.ChannelCount;

                                    waveBufferPcm16 = MemoryMarshal.Cast<byte, short>(memoryManager.GetSpan(bufferOffset, bufferSize));
                                }

                                if (ShouldHoldOrRecoverUwpPcm16(
                                    memoryManager,
                                    ref info,
                                    ref waveBuffer,
                                    ref voiceState,
                                    waveBufferPcm16,
                                    targetWaveBufferSampleCount))
                                {
                                    isStarving = true;
                                    break;
                                }

                                decodedSampleCount = PcmHelper.Decode(tempSpan, waveBufferPcm16, targetSampleStartOffset, targetSampleEndOffset, info.ChannelIndex, info.ChannelCount);
                                break;
                            case SampleFormat.PcmFloat:
                                ReadOnlySpan<float> waveBufferPcmFloat = ReadOnlySpan<float>.Empty;

                                if (waveBuffer.Buffer != 0 && waveBuffer.BufferSize != 0)
                                {
                                    ulong bufferOffset = waveBuffer.Buffer + PcmHelper.GetBufferOffset<float>(targetSampleStartOffset, offset, info.ChannelCount);
                                    int bufferSize = PcmHelper.GetBufferSize<float>(targetSampleStartOffset, targetSampleEndOffset, offset, sampleCountToDecode - y) * info.ChannelCount;

                                    waveBufferPcmFloat = MemoryMarshal.Cast<byte, float>(memoryManager.GetSpan(bufferOffset, bufferSize));
                                }

                                decodedSampleCount = PcmHelper.Decode(tempSpan, waveBufferPcmFloat, targetSampleStartOffset, targetSampleEndOffset, info.ChannelIndex, info.ChannelCount);
                                break;
                            default:
                                Logger.Error?.Print(LogClass.AudioRenderer, "Unsupported sample format " + info.SampleFormat);
                                break;
                        }

                        Debug.Assert(decodedSampleCount <= sampleCountToDecode);

                        if (decodedSampleCount < 0)
                        {
                            Logger.Warning?.Print(LogClass.AudioRenderer, "Decoding failed, skipping WaveBuffer");

                            voiceState.MarkEndOfBufferWaveBufferProcessing(ref waveBuffer, ref waveBufferIndex, ref waveBufferConsumed, ref playedSampleCount);
                            decodedSampleCount = 0;
                        }

                        y += decodedSampleCount;
                        offset += decodedSampleCount;
                        playedSampleCount += (uint)decodedSampleCount;

                        if (offset >= targetWaveBufferSampleCount || decodedSampleCount == 0)
                        {
                            offset = 0;

                            if (waveBuffer.Looping)
                            {
                                voiceState.LoopCount++;

                                if (waveBuffer.LoopCount >= 0)
                                {
                                    if (decodedSampleCount == 0 || voiceState.LoopCount > waveBuffer.LoopCount)
                                    {
                                        voiceState.MarkEndOfBufferWaveBufferProcessing(ref waveBuffer, ref waveBufferIndex, ref waveBufferConsumed, ref playedSampleCount);
                                    }
                                }

                                if (decodedSampleCount == 0)
                                {
                                    isStarving = true;
                                    break;
                                }

                                if ((info.DecodingBehaviour & DecodingBehaviour.PlayedSampleCountResetWhenLooping) == DecodingBehaviour.PlayedSampleCountResetWhenLooping)
                                {
                                    playedSampleCount = 0;
                                }
                            }
                            else
                            {
                                voiceState.MarkEndOfBufferWaveBufferProcessing(ref waveBuffer, ref waveBufferIndex, ref waveBufferConsumed, ref playedSampleCount);
                            }
                        }
                    }

                    Span<int> outputSpanInt = MemoryMarshal.Cast<float, int>(outputBuffer[i..]);

                    if ((info.DecodingBehaviour & DecodingBehaviour.SkipPitchAndSampleRateConversion) == DecodingBehaviour.SkipPitchAndSampleRateConversion)
                    {
                        for (int j = 0; j < y; j++)
                        {
                            outputBuffer[j] = tempBuffer[j];
                        }
                    }
                    else
                    {
                        Span<short> tempSpan = tempBuffer[(tempBufferIndex + y)..];

                        tempSpan[..(sampleCountToDecode - y)].Clear();

                        ToFloat(outputBuffer, outputSpanInt, sampleCountToProcess);

                        ResamplerHelper.Resample(outputBuffer, tempBuffer, sampleRateRatio, ref fraction, sampleCountToProcess, info.SrcQuality, y != sourceSampleCountToProcess || info.Pitch != 1.0f);

                        tempBuffer.Slice(sampleCountToDecode, pitchMaxLength).CopyTo(voiceState.Pitch.AsSpan());
                    }

                    i += sampleCountToProcess;
                }

                Debug.Assert(sourceSampleCountToProcess == i || !isStarving);

                voiceState.WaveBufferConsumed = waveBufferConsumed;
                voiceState.Offset = offset;
                voiceState.PlayedSampleCount = playedSampleCount;
                voiceState.WaveBufferIndex = (uint)waveBufferIndex;
                voiceState.Fraction = fraction;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ToFloatAvx(Span<float> output, ReadOnlySpan<int> input, int sampleCount)
        {
            ReadOnlySpan<Vector256<int>> inputVec = MemoryMarshal.Cast<int, Vector256<int>>(input);
            Span<Vector256<float>> outputVec = MemoryMarshal.Cast<float, Vector256<float>>(output);

            int sisdStart = inputVec.Length * 8;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = Avx.ConvertToVector256Single(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ToFloatSse2(Span<float> output, ReadOnlySpan<int> input, int sampleCount)
        {
            ReadOnlySpan<Vector128<int>> inputVec = MemoryMarshal.Cast<int, Vector128<int>>(input);
            Span<Vector128<float>> outputVec = MemoryMarshal.Cast<float, Vector128<float>>(output);

            int sisdStart = inputVec.Length * 4;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = Sse2.ConvertToVector128Single(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ToFloatAdvSimd(Span<float> output, ReadOnlySpan<int> input, int sampleCount)
        {
            ReadOnlySpan<Vector128<int>> inputVec = MemoryMarshal.Cast<int, Vector128<int>>(input);
            Span<Vector128<float>> outputVec = MemoryMarshal.Cast<float, Vector128<float>>(output);

            int sisdStart = inputVec.Length * 4;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = AdvSimd.ConvertToSingle(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToFloatSlow(Span<float> output, ReadOnlySpan<int> input, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                output[i] = input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToFloat(Span<float> output, ReadOnlySpan<int> input, int sampleCount)
        {
            if (Avx.IsSupported)
            {
                ToFloatAvx(output, input, sampleCount);
            }
            else if (Sse2.IsSupported)
            {
                ToFloatSse2(output, input, sampleCount);
            }
            else if (AdvSimd.IsSupported)
            {
                ToFloatAdvSimd(output, input, sampleCount);
            }
            else
            {
                ToFloatSlow(output, input, sampleCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToIntAvx(Span<int> output, ReadOnlySpan<float> input, int sampleCount)
        {
            ReadOnlySpan<Vector256<float>> inputVec = MemoryMarshal.Cast<float, Vector256<float>>(input);
            Span<Vector256<int>> outputVec = MemoryMarshal.Cast<int, Vector256<int>>(output);

            int sisdStart = inputVec.Length * 8;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = Avx.ConvertToVector256Int32(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = (int)input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToIntSse2(Span<int> output, ReadOnlySpan<float> input, int sampleCount)
        {
            ReadOnlySpan<Vector128<float>> inputVec = MemoryMarshal.Cast<float, Vector128<float>>(input);
            Span<Vector128<int>> outputVec = MemoryMarshal.Cast<int, Vector128<int>>(output);

            int sisdStart = inputVec.Length * 4;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = Sse2.ConvertToVector128Int32(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = (int)input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToIntAdvSimd(Span<int> output, ReadOnlySpan<float> input, int sampleCount)
        {
            ReadOnlySpan<Vector128<float>> inputVec = MemoryMarshal.Cast<float, Vector128<float>>(input);
            Span<Vector128<int>> outputVec = MemoryMarshal.Cast<int, Vector128<int>>(output);

            int sisdStart = inputVec.Length * 4;

            for (int i = 0; i < inputVec.Length; i++)
            {
                outputVec[i] = AdvSimd.ConvertToInt32RoundToZero(inputVec[i]);
            }

            for (int i = sisdStart; i < sampleCount; i++)
            {
                output[i] = (int)input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToIntSlow(Span<int> output, ReadOnlySpan<float> input, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                output[i] = (int)input[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToInt(Span<int> output, ReadOnlySpan<float> input, int sampleCount)
        {
            if (Avx.IsSupported)
            {
                ToIntAvx(output, input, sampleCount);
            }
            else if (Sse2.IsSupported)
            {
                ToIntSse2(output, input, sampleCount);
            }
            else if (AdvSimd.IsSupported)
            {
                ToIntAdvSimd(output, input, sampleCount);
            }
            else
            {
                ToIntSlow(output, input, sampleCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemapLegacyChannelEffectMappingToChannelResourceMapping(bool isSupported, Span<ushort> bufferIndices, uint channelCount)
        {
            if (!isSupported && channelCount == 6)
            {
                ushort backLeft = bufferIndices[2];
                ushort backRight = bufferIndices[3];
                ushort frontCenter = bufferIndices[4];
                ushort lowFrequency = bufferIndices[5];

                bufferIndices[2] = frontCenter;
                bufferIndices[3] = lowFrequency;
                bufferIndices[4] = backLeft;
                bufferIndices[5] = backRight;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemapChannelResourceMappingToLegacy(bool isSupported, Span<ushort> bufferIndices, uint channelCount)
        {
            if (isSupported && channelCount == 6)
            {
                ushort frontCenter = bufferIndices[2];
                ushort lowFrequency = bufferIndices[3];
                ushort backLeft = bufferIndices[4];
                ushort backRight = bufferIndices[5];

                bufferIndices[2] = backLeft;
                bufferIndices[3] = backRight;
                bufferIndices[4] = frontCenter;
                bufferIndices[5] = lowFrequency;
            }
        }
    }
}
