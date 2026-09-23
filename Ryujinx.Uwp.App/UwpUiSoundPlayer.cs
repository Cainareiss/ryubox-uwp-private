using System;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Ryujinx.Uwp.App
{
    internal sealed class UwpUiSoundPlayer : IDisposable
    {
        private readonly MediaPlayer _player;
        private readonly MediaSource _source;
        private readonly MediaPlayer _themePlayer;
        private readonly MediaSource _themeSource;
        private bool _failed;
        private bool _themeFailed;

        internal UwpUiSoundPlayer()
        {
            try
            {
                _source = MediaSource.CreateFromUri(
                    new Uri("ms-appx:///Assets/Sfx/ryubox-nav.wav"));
                _player = new MediaPlayer
                {
                    AutoPlay = false,
                    IsLoopingEnabled = false,
                    Volume = 0.45,
                    Source = _source,
                };
                UwpFrontendLog.Write("audio", "UI SFX loaded from Assets/Sfx/ryubox-nav.wav");

                _themeSource = MediaSource.CreateFromUri(
                    new Uri("ms-appx:///Assets/Music/SongOfStorms.mp3"));
                _themePlayer = new MediaPlayer
                {
                    AutoPlay = false,
                    IsLoopingEnabled = true,
                    Volume = 0.32,
                    Source = _themeSource,
                };
                UwpFrontendLog.Write("audio", "Frontend theme loaded from Assets/Music/SongOfStorms.mp3");
            }
            catch (Exception exception)
            {
                // Keep the navigation sound and theme independent: a missing
                // optional asset must not disable all frontend audio.
                if (_player == null)
                {
                    _failed = true;
                    UwpFrontendLog.Error("carregando SFX da interface", exception);
                }
                else
                {
                    _themeFailed = true;
                    UwpFrontendLog.Error("carregando música tema da interface", exception);
                }
            }
        }

        internal void StartTheme()
        {
            if (_themeFailed || _themePlayer == null) return;
            try
            {
                _themePlayer.PlaybackSession.Position = TimeSpan.Zero;
                _themePlayer.Play();
            }
            catch (Exception exception)
            {
                _themeFailed = true;
                UwpFrontendLog.Error("iniciando música tema da interface", exception);
            }
        }

        internal void StopTheme()
        {
            if (_themePlayer == null) return;
            try { _themePlayer.Pause(); }
            catch (Exception exception) { UwpFrontendLog.Error("parando música tema da interface", exception); }
        }

        internal void Play()
        {
            if (_failed || _player == null)
            {
                return;
            }

            try
            {
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();
            }
            catch (Exception exception)
            {
                // Avoid flooding the log or letting an unavailable audio
                // endpoint interfere with controller navigation.
                _failed = true;
                UwpFrontendLog.Error("reproduzindo SFX da interface", exception);
            }
        }

        public void Dispose()
        {
            _player?.Pause();
            if (_player != null)
            {
                _player.Source = null;
                _player.Dispose();
            }
            _source?.Dispose();
            _themePlayer?.Pause();
            if (_themePlayer != null)
            {
                _themePlayer.Source = null;
                _themePlayer.Dispose();
            }
            _themeSource?.Dispose();
        }
    }
}
