using System;

namespace Ryujinx.Uwp.App
{
    internal enum BootScreenState
    {
        Hidden,
        Preparing,
        LoadingAssets,
        LoadingShaders,
        StartingGuest,
        WaitingFirstFrame,
        FadeOut,
        Gameplay,
        Error,
    }

    // State-only controller: rendering is kept in MainPage so the same state
    // works for Xbox SwapChainPanel and a desktop validation surface. There is
    // intentionally no "startup timeout": only a real present may dismiss it.
    internal sealed class UwpBootScreen
    {
        private const double FadeDurationMilliseconds = 500.0;
        private double _fadeElapsedMilliseconds;

        internal BootScreenState State { get; private set; } = BootScreenState.Hidden;
        internal string Status { get; private set; } = string.Empty;
        internal string Error { get; private set; } = string.Empty;
        internal double Opacity { get; private set; }
        internal bool IsVisible => State != BootScreenState.Hidden && State != BootScreenState.Gameplay;

        internal void Begin(string title)
        {
            State = BootScreenState.Preparing;
            Status = string.IsNullOrWhiteSpace(title) ? "Preparando jogo..." : "Preparando " + title + "...";
            Error = string.Empty;
            Opacity = 1.0;
            _fadeElapsedMilliseconds = 0.0;
        }

        internal void SetPhase(BootScreenState phase, string status)
        {
            if (State == BootScreenState.Hidden || State == BootScreenState.Error || State == BootScreenState.FadeOut || State == BootScreenState.Gameplay)
            {
                return;
            }

            State = phase;
            if (!string.IsNullOrWhiteSpace(status))
            {
                Status = status;
            }
        }

        internal void FirstVisiblePresent()
        {
            if (State == BootScreenState.Hidden || State == BootScreenState.Gameplay || State == BootScreenState.Error || State == BootScreenState.FadeOut)
            {
                return;
            }

            State = BootScreenState.FadeOut;
            Status = "";
            _fadeElapsedMilliseconds = 0.0;
        }

        internal void Fail(string error)
        {
            State = BootScreenState.Error;
            Error = string.IsNullOrWhiteSpace(error) ? "Falha ao iniciar o jogo." : error;
            Status = "[A] Tentar novamente     [B] Voltar a biblioteca";
            Opacity = 1.0;
            _fadeElapsedMilliseconds = 0.0;
        }

        internal void Hide()
        {
            State = BootScreenState.Hidden;
            Status = string.Empty;
            Error = string.Empty;
            Opacity = 0.0;
            _fadeElapsedMilliseconds = 0.0;
        }

        internal void Tick(TimeSpan elapsed)
        {
            if (State != BootScreenState.FadeOut)
            {
                return;
            }

            _fadeElapsedMilliseconds += Math.Max(0.0, elapsed.TotalMilliseconds);
            Opacity = Math.Max(0.0, 1.0 - (_fadeElapsedMilliseconds / FadeDurationMilliseconds));
            if (_fadeElapsedMilliseconds >= FadeDurationMilliseconds)
            {
                // Gameplay belongs to the shared game surface, not to this
                // overlay. Keeping the state hidden also prevents a stale
                // overlay from intercepting the next library session.
                State = BootScreenState.Hidden;
                Opacity = 0.0;
            }
        }
    }
}
