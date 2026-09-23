using Ryujinx.Audio.Integration;
using Ryujinx.Audio.Renderer.Dsp.Command;
using Ryujinx.Audio.Renderer.Utils;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Audio.Renderer.Dsp
{
    public class AudioProcessor : IDisposable
    {
        private const int MaxBufferedFrames = 5;
        private const int TargetBufferedFrames = 3;

        private enum MailboxMessage : uint
        {
            Start,
            Stop,
            RenderStart,
            RenderEnd,
        }

        private class RendererSession
        {
            public CommandList CommandList;
            public int RenderingLimit;
            public ulong AppletResourceId;
        }

        private Mailbox<MailboxMessage> _mailbox;
        private RendererSession[] _sessionCommandList;
        private Thread _workerThread;

        public IHardwareDevice[] OutputDevices { get; private set; }

        private long _lastTime;
        private long _playbackEnds;
        private readonly ManualResetEvent _event;

        private ManualResetEvent _pauseEvent;

        private long _waitCount;

        private static long _uwpSignals;
        private static long _uwpCompletedRenders;
        private static long _uwpWaits;
        private static long _uwpTimeouts;
        private static long _uwpLastProgressAt;

        public static string GetUwpTelemetry()
        {
            return string.Format(
                "audioSignals={0}, audioCompleted={1}, audioWaits={2}, audioTimeouts={3}, audioLastProgress={4}",
                Interlocked.Read(ref _uwpSignals),
                Interlocked.Read(ref _uwpCompletedRenders),
                Interlocked.Read(ref _uwpWaits),
                Interlocked.Read(ref _uwpTimeouts),
                Interlocked.Read(ref _uwpLastProgressAt));
        }

        public AudioProcessor()
        {
            _event = new ManualResetEvent(false);
        }

        private static uint GetHardwareChannelCount(IHardwareDeviceDriver deviceDriver)
        {
            // Get the real device driver (In case the compat layer is on top of it).
            deviceDriver = deviceDriver.GetRealDeviceDriver();

            if (deviceDriver.SupportsChannelCount(6))
            {
                return 6;
            }

            // NOTE: We default to stereo as this will get downmixed to mono by the compat layer if it's not compatible.
            return 2;
        }

        public void Start(IHardwareDeviceDriver deviceDriver)
        {
            VoiceMixTelemetry.Reset();
            DataSourceHelper.ResetUwpPcmStartupGuard();
            Interlocked.Exchange(ref _uwpSignals, 0);
            Interlocked.Exchange(ref _uwpCompletedRenders, 0);
            Interlocked.Exchange(ref _uwpWaits, 0);
            Interlocked.Exchange(ref _uwpTimeouts, 0);
            Interlocked.Exchange(ref _uwpLastProgressAt, Environment.TickCount64);
            OutputDevices = new IHardwareDevice[Constants.AudioRendererSessionCountMax];

            uint channelCount = GetHardwareChannelCount(deviceDriver);

            for (int i = 0; i < OutputDevices.Length; i++)
            {
                // TODO: Don't hardcode sample rate.
                OutputDevices[i] = new HardwareDeviceImpl(deviceDriver, channelCount, Constants.TargetSampleRate);
            }

            _mailbox = new Mailbox<MailboxMessage>();
            _sessionCommandList = new RendererSession[Constants.AudioRendererSessionCountMax];
            _event.Reset();
            _lastTime = PerformanceCounter.ElapsedNanoseconds;
            _pauseEvent = deviceDriver.GetPauseEvent();

            StartThread();

            _mailbox.SendMessage(MailboxMessage.Start);

            if (_mailbox.ReceiveResponse() != MailboxMessage.Start)
            {
                throw new InvalidOperationException("Audio Processor Start response was invalid!");
            }
        }

        public void Stop()
        {
            _mailbox.SendMessage(MailboxMessage.Stop);

            if (_mailbox.ReceiveResponse() != MailboxMessage.Stop)
            {
                throw new InvalidOperationException("Audio Processor Stop response was invalid!");
            }

            foreach (IHardwareDevice device in OutputDevices)
            {
                device.Dispose();
            }
        }

        public bool TryStop(int timeoutMilliseconds)
        {
            if (!_mailbox.TrySendMessage(MailboxMessage.Stop, timeoutMilliseconds))
            {
                Logger.Warning?.Print(LogClass.AudioRenderer, $"Audio processor stop message timed out after {timeoutMilliseconds} ms");
                return false;
            }

            if (!_mailbox.TryReceiveResponse(out MailboxMessage response, timeoutMilliseconds) || response != MailboxMessage.Stop)
            {
                Logger.Warning?.Print(LogClass.AudioRenderer, $"Audio processor stop response timed out after {timeoutMilliseconds} ms");
                return false;
            }

            foreach (IHardwareDevice device in OutputDevices)
            {
                device.Dispose();
            }

            return true;
        }

        public void Send(int sessionId, CommandList commands, int renderingLimit, ulong appletResourceId)
        {
            _sessionCommandList[sessionId] = new RendererSession
            {
                CommandList = commands,
                RenderingLimit = renderingLimit,
                AppletResourceId = appletResourceId,
            };
        }

        public bool HasRemainingCommands(int sessionId)
        {
            return _sessionCommandList[sessionId] != null;
        }

        public void Signal()
        {
            Interlocked.Increment(ref _uwpSignals);
            Interlocked.Exchange(ref _uwpLastProgressAt, Environment.TickCount64);
            _mailbox.SendMessage(MailboxMessage.RenderStart);
        }

        public bool Wait(int timeoutMilliseconds = Timeout.Infinite, bool applyPacing = true)
        {
            MailboxMessage response;

            if (timeoutMilliseconds == Timeout.Infinite)
            {
                response = _mailbox.ReceiveResponse();
            }
            else if (!_mailbox.TryReceiveResponse(out response, timeoutMilliseconds))
            {
                Interlocked.Increment(ref _uwpTimeouts);
                Interlocked.Exchange(ref _uwpLastProgressAt, Environment.TickCount64);
                Logger.Warning?.Print(LogClass.AudioRenderer, $"Audio processor render response timed out after {timeoutMilliseconds} ms");
                return false;
            }

            if (response != MailboxMessage.RenderEnd)
            {
                throw new InvalidOperationException("Audio Processor Wait response was invalid!");
            }

            long waitCount = Interlocked.Increment(ref _waitCount);
            Interlocked.Increment(ref _uwpWaits);
            Interlocked.Exchange(ref _uwpLastProgressAt, Environment.TickCount64);

            if (!applyPacing)
            {
                if (waitCount == 1)
                {
                    Logger.Info?.Print(LogClass.AudioRenderer, "Audio processor handshake completed; external safe clock owns pacing");
                }

                return true;
            }

            long increment = Constants.AudioProcessorMaxUpdateTimeTarget;

            long timeNow = PerformanceCounter.ElapsedNanoseconds;

            if (timeNow > _playbackEnds)
            {
                // Playback has restarted.
                _playbackEnds = timeNow;
            }

            _playbackEnds += increment;

            // The number of frames we are behind where the timer says we should be.
            long framesBehind = (timeNow - _lastTime) / increment;

            // The number of frames yet to play on the backend.
            long bufferedFrames = (_playbackEnds - timeNow) / increment + framesBehind;

            // If we've entered a situation where a lot of buffers will be queued on the backend,
            // Skip some audio frames so that playback can catch up.
            if (bufferedFrames > MaxBufferedFrames)
            {
                // Skip a few frames so that we're not too far behind. (the target number of frames)
                _lastTime += increment * (bufferedFrames - TargetBufferedFrames);
            }

            long pacingStartedAt = Environment.TickCount64;

            while (timeNow < _lastTime + increment)
            {
                _event.WaitOne(1);

                timeNow = PerformanceCounter.ElapsedNanoseconds;

                // Stopwatch/QPC should always advance, but a stalled host clock
                // must never hold the entire audio renderer forever. This also
                // protects constrained AppContainer environments during resume.
                if (Environment.TickCount64 - pacingStartedAt >= 50)
                {
                    Logger.Warning?.Print(LogClass.AudioRenderer, "Audio pacing clock stalled for 50 ms; resynchronizing renderer clock");
                    _lastTime = timeNow;
                    return true;
                }
            }

            _lastTime += increment;

            return true;
        }

        private void StartThread()
        {
            _workerThread = new Thread(Work)
            {
                Name = "AudioProcessor.Worker",
                Priority = ThreadPriority.AboveNormal,
            };

            _workerThread.Start();
        }

        private void Work()
        {
            if (_mailbox.ReceiveMessage() != MailboxMessage.Start)
            {
                throw new InvalidOperationException("Audio Processor Start message was invalid!");
            }

            _mailbox.SendResponse(MailboxMessage.Start);
            _mailbox.SendResponse(MailboxMessage.RenderEnd);

            Logger.Info?.Print(LogClass.AudioRenderer, "Starting audio processor");

            while (true)
            {
                _pauseEvent?.WaitOne();

                MailboxMessage message = _mailbox.ReceiveMessage();

                if (message == MailboxMessage.Stop)
                {
                    break;
                }

                if (message == MailboxMessage.RenderStart)
                {
                    long startTicks = PerformanceCounter.ElapsedNanoseconds;

                    for (int i = 0; i < _sessionCommandList.Length; i++)
                    {
                        if (_sessionCommandList[i] != null)
                        {
                            _sessionCommandList[i].CommandList.Process(OutputDevices[i]);
                            _sessionCommandList[i].CommandList.Dispose();
                            _sessionCommandList[i] = null;
                        }
                    }

                    VoiceMixTelemetry.FlushIfDue();

                    long endTicks = PerformanceCounter.ElapsedNanoseconds;

                    long elapsedTime = endTicks - startTicks;

                    if (Constants.AudioProcessorMaxUpdateTime < elapsedTime)
                    {
                        Logger.Debug?.Print(LogClass.AudioRenderer, $"DSP too slow (exceeded by {elapsedTime - Constants.AudioProcessorMaxUpdateTime}ns)");
                    }

                    _mailbox.SendResponse(MailboxMessage.RenderEnd);
                    Interlocked.Increment(ref _uwpCompletedRenders);
                    Interlocked.Exchange(ref _uwpLastProgressAt, Environment.TickCount64);
                }
            }

            Logger.Info?.Print(LogClass.AudioRenderer, "Stopping audio processor");
            _mailbox.SendResponse(MailboxMessage.Stop);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _event.Dispose();
                _mailbox?.Dispose();
            }
        }
    }
}
