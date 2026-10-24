using Ryujinx.Audio.Common;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Audio.Renderer.Dsp
{
    /// <summary>
    /// Low-overhead Xbox/UWP diagnostic that observes samples where each guest
    /// voice is decoded and where it is added to a mix buffer. All calls are
    /// made by the single AudioProcessor worker, so no DSP-path lock is needed.
    /// </summary>
    internal static class VoiceMixTelemetry
    {
        private sealed class VoiceSample
        {
            public SampleFormat Format;
            public uint SampleRate;
            public uint Channels;
            public float Pitch;
            public float VoiceVolume;
            public int MixId;
            public uint WaveBufferIndex;
            public uint WaveBufferCount;
            public uint WaveBuffersConsumed;
            public ulong PlayedSamples;
            public int Offset;
            public int DataCalls;
            public int DataNonZeroCalls;
            public float DataPeak;
            public int MixCalls;
            public int MixNonZeroCalls;
            public float MixInputPeak;
            public float MixOutputPeak;
            public float GainMin = float.MaxValue;
            public float GainMax = float.MinValue;
        }

        private static readonly Dictionary<int, VoiceSample> _voices = new();
        private static long _intervalStartedAt;
        private static bool _enabled;

        public static void Reset()
        {
            _voices.Clear();
            _intervalStartedAt = Environment.TickCount64;
            AppContext.TryGetSwitch("Ryujinx.Uwp.DeepTrace", out _enabled);
        }

        private static float GetPeak(ReadOnlySpan<float> samples)
        {
            float peak = 0f;

            for (int index = 0; index < samples.Length; index++)
            {
                float value = MathF.Abs(samples[index]);
                if (value > peak)
                {
                    peak = value;
                }
            }

            return peak;
        }

        private static VoiceSample GetVoice(int nodeId)
        {
            if (!_voices.TryGetValue(nodeId, out VoiceSample sample))
            {
                sample = new VoiceSample();
                _voices.Add(nodeId, sample);
            }

            return sample;
        }

        public static void RecordDataSource(
            int nodeId,
            SampleFormat format,
            uint sampleRate,
            uint channels,
            float pitch,
            float voiceVolume,
            int mixId,
            uint waveBufferIndex,
            uint waveBufferCount,
            uint waveBuffersConsumed,
            ulong playedSamples,
            int offset,
            ReadOnlySpan<float> samples)
        {
            if (!_enabled)
            {
                return;
            }

            VoiceSample voice = GetVoice(nodeId);
            float peak = GetPeak(samples);

            voice.Format = format;
            voice.SampleRate = sampleRate;
            voice.Channels = channels;
            voice.Pitch = pitch;
            voice.VoiceVolume = voiceVolume;
            voice.MixId = mixId;
            voice.WaveBufferIndex = waveBufferIndex;
            voice.WaveBufferCount = waveBufferCount;
            voice.WaveBuffersConsumed = waveBuffersConsumed;
            voice.PlayedSamples = playedSamples;
            voice.Offset = offset;
            voice.DataCalls++;
            voice.DataPeak = MathF.Max(voice.DataPeak, peak);
            if (peak > 0f)
            {
                voice.DataNonZeroCalls++;
            }
        }

        public static void RecordMix(
            int nodeId,
            ReadOnlySpan<float> input,
            ReadOnlySpan<float> output,
            float volume0,
            float volume1)
        {
            if (!_enabled)
            {
                return;
            }

            VoiceSample voice = GetVoice(nodeId);
            float inputPeak = GetPeak(input);
            float outputPeak = GetPeak(output);
            float gainMin = MathF.Min(volume0, volume1);
            float gainMax = MathF.Max(volume0, volume1);

            voice.MixCalls++;
            voice.MixInputPeak = MathF.Max(voice.MixInputPeak, inputPeak);
            voice.MixOutputPeak = MathF.Max(voice.MixOutputPeak, outputPeak);
            voice.GainMin = MathF.Min(voice.GainMin, gainMin);
            voice.GainMax = MathF.Max(voice.GainMax, gainMax);
            if (inputPeak > 0f && (volume0 != 0f || volume1 != 0f))
            {
                voice.MixNonZeroCalls++;
            }
        }

        public static void FlushIfDue()
        {
            if (!_enabled)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - _intervalStartedAt < 1000)
            {
                return;
            }

            foreach ((int nodeId, VoiceSample voice) in _voices)
            {
                string gain = voice.MixCalls == 0
                    ? "n/a"
                    : $"{voice.GainMin:0.0000}..{voice.GainMax:0.0000}";

                Logger.Info?.Print(
                    LogClass.AudioRenderer,
                    $"VoiceMix node={nodeId}: source={voice.DataNonZeroCalls}/{voice.DataCalls}, " +
                    $"sourcePeak={voice.DataPeak:0.000000}, mix={voice.MixNonZeroCalls}/{voice.MixCalls}, " +
                    $"mixInputPeak={voice.MixInputPeak:0.000000}, mixOutputPeak={voice.MixOutputPeak:0.000000}, " +
                    $"gain={gain}, voiceVolume={voice.VoiceVolume:0.0000}, format={voice.Format}, " +
                    $"rate={voice.SampleRate}, channels={voice.Channels}, pitch={voice.Pitch:0.0000}, " +
                    $"mixId={voice.MixId}, waveIndex={voice.WaveBufferIndex}, configuredWaveBuffers={voice.WaveBufferCount}, " +
                    $"consumedWaveBuffers={voice.WaveBuffersConsumed}, " +
                    $"played={voice.PlayedSamples}, offset={voice.Offset}.");
            }

            _voices.Clear();
            _intervalStartedAt = now;
        }
    }
}
