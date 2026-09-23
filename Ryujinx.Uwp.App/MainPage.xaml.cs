using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;

namespace Ryujinx.Uwp.App
{
    internal enum SessionPresentationState
    {
        FrontendVisible,
        Launching,
        WaitingFirstGuestFrame,
        TransitionToGame,
        GameVisible,
        ReturningToFrontend,
        Failed,
    }

    internal enum PresentationOwner
    {
        Frontend,
        Game,
    }

    public sealed partial class MainPage : Page
    {
        private readonly UwpInputHost _inputHost;
        private readonly UwpCoreSession _coreSession;
        private readonly UwpImGuiFrontendCoordinator _frontendCoordinator;
        private readonly UwpUiSoundPlayer _uiSoundPlayer;
        private readonly UwpBootScreen _bootScreen = new UwpBootScreen();
        private bool _graphicsHostInitialized;
        private bool _pageLoaded;
        private bool _initializationStarted;
        private GamepadButtons _keyboardButtons;
        private bool _pointerLeftDown;
        private bool _pointerRightDown;
        private float _pointerX = -float.MaxValue;
        private float _pointerY = -float.MaxValue;
        private float _pointerWheel;
        private bool _pointerClickPending;
        private GamepadButtons _previousUiSoundButtons;
        private GamepadButtons _previousBootErrorButtons;
        private bool _frontendInputReleaseBarrier;
        private bool _gameInputReleaseBarrier;
        private bool _coreWasRunning;
        private SessionPresentationState _presentationState = SessionPresentationState.FrontendVisible;
        private PresentationOwner _presentationOwner = PresentationOwner.Frontend;
        private ulong _presentationSessionId;
        private bool _firstGuestPresentObserved;
        private DateTimeOffset _lastBootScreenTick;
        private ulong _loadedBootAssetProgramId;

        public MainPage()
        {
            this.InitializeComponent();
            _inputHost = new UwpInputHost();
            _coreSession = new UwpCoreSession();
            _frontendCoordinator = new UwpImGuiFrontendCoordinator(_coreSession);
            _uiSoundPlayer = new UwpUiSoundPlayer();
            _frontendCoordinator.BootPhaseChanged += FrontendCoordinator_BootPhaseChanged;
            _frontendCoordinator.BootFailed += FrontendCoordinator_BootFailed;
            
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        private async void FrontendCoordinator_BootPhaseChanged(string phase)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (phase.StartsWith("Preparando", StringComparison.OrdinalIgnoreCase))
                {
                    _firstGuestPresentObserved = false;
                    _bootScreen.Begin("jogo");
                }
                else if (phase.StartsWith("Carregando arquivos", StringComparison.OrdinalIgnoreCase))
                {
                    _bootScreen.SetPhase(BootScreenState.LoadingAssets, phase);
                }
                else if (phase.StartsWith("Carregando shaders", StringComparison.OrdinalIgnoreCase))
                {
                    _bootScreen.SetPhase(BootScreenState.LoadingShaders, phase);
                }
                else if (phase.StartsWith("Iniciando", StringComparison.OrdinalIgnoreCase))
                {
                    _bootScreen.SetPhase(BootScreenState.StartingGuest, phase);
                }
                else
                {
                    _bootScreen.SetPhase(BootScreenState.WaitingFirstFrame, phase);
                }

                UpdateBootScreenVisual();
            });
        }

        private async void FrontendCoordinator_BootFailed(string error)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowBootError(error));
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pageLoaded)
            {
                return;
            }

            _pageLoaded = true;
            _inputHost.Start();
            _uiSoundPlayer.StartTheme();
            Window.Current.CoreWindow.PointerMoved += CoreWindow_PointerMoved;
            Window.Current.CoreWindow.PointerPressed += CoreWindow_PointerPressed;
            Window.Current.CoreWindow.PointerReleased += CoreWindow_PointerReleased;
            Window.Current.CoreWindow.PointerWheelChanged += CoreWindow_PointerWheelChanged;
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
            Window.Current.CoreWindow.KeyUp += CoreWindow_KeyUp;
            
            string error;
            UwpGraphicsHost.GetPhysicalPanelSize(GameSurfacePanel,
                out uint initialWidth, out uint initialHeight);
            if (UwpGraphicsHost.TryInitialize(GameSurfacePanel, initialWidth, initialHeight, "D3D12", out error))
            {
                _graphicsHostInitialized = true;
                if (!_initializationStarted)
                {
                    _initializationStarted = true;
                    _ = InitializeFrontendSafelyAsync();
                }
                Windows.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            }
        }

        private async System.Threading.Tasks.Task InitializeFrontendSafelyAsync()
        {
            try
            {
                await _frontendCoordinator.InitializeAsync();
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("dispatcher da inicializacao do frontend", exception);
            }
        }

        private void OnRendering(object sender, object e)
        {
            if (!_graphicsHostInitialized)
            {
                return;
            }

            UwpInputSnapshot snapshot;
            if (!_inputHost.TryRead(out snapshot))
            {
                snapshot = new UwpInputSnapshot(GamepadButtons.None, GamepadButtons.None,
                    0, 0, 0, 0, 0, 0, 0, 0);
            }

            bool isGameActive = _coreSession.IsGameActive;
            bool coreRunning = _coreSession.IsRunning;
            GamepadButtons uiButtons = snapshot.Buttons | _keyboardButtons;
            bool controlsReleased = uiButtons == GamepadButtons.None &&
                Math.Abs(snapshot.LeftThumbstickX) < 0.20 &&
                Math.Abs(snapshot.LeftThumbstickY) < 0.20 &&
                Math.Abs(snapshot.RightThumbstickX) < 0.20 &&
                Math.Abs(snapshot.RightThumbstickY) < 0.20 &&
                snapshot.LeftTrigger < 0.10 && snapshot.RightTrigger < 0.10 &&
                !_pointerLeftDown && !_pointerRightDown && !_pointerClickPending;

            TickBootScreen();

            if (_bootScreen.State == BootScreenState.Error)
            {
                HandleBootErrorInput(uiButtons);
                UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
                UwpGraphicsHost.Render();
                return;
            }

            if (isGameActive && !coreRunning && _coreWasRunning)
            {
                _uiSoundPlayer.StartTheme();
                _presentationState = SessionPresentationState.ReturningToFrontend;
                _coreSession.IsGameActive = false;
                _coreWasRunning = false;
                _gameInputReleaseBarrier = false;
                _inputHost.ResetButtonState();
                _frontendInputReleaseBarrier = true;
                _previousUiSoundButtons = GamepadButtons.None;
                _pointerClickPending = false;
                _pointerWheel = 0f;
                UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);

                string error = UwpCoreSession.ReadCoreHostError();
                bool failedBeforeFirstPresent = !_firstGuestPresentObserved && _bootScreen.IsVisible;
                if (failedBeforeFirstPresent)
                {
                    ShowBootError(string.IsNullOrWhiteSpace(error)
                        ? "O jogo encerrou antes de apresentar um quadro visivel."
                        : "O jogo encerrou antes de apresentar um quadro visivel: " + error);
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    _frontendCoordinator.ShowErrorNotification("Emulação finalizada: " + error);
                }
                else
                {
                    _frontendCoordinator.ShowErrorNotification("Emulação finalizada.");
                }

                bool graphicsHealthy = UwpGraphicsHost.TryRecoverDevice(
                    GameSurfacePanel,
                    out bool recovered,
                    out string recoveryError);
                if (!graphicsHealthy)
                {
                    _presentationState = SessionPresentationState.Failed;
                    _frontendCoordinator.ShowErrorNotification("Falha ao recuperar " + UwpGraphicsHost.ActiveBackend + ": " + recoveryError);
                }
                else
                {
                    if (!failedBeforeFirstPresent)
                    {
                        _bootScreen.Hide();
                        UpdateBootScreenVisual();
                    }
                    _presentationOwner = PresentationOwner.Frontend;
                    _presentationState = SessionPresentationState.FrontendVisible;
                    UwpFrontendLog.Write(
                        "presentation",
                        string.Format("[Session {0}] FrontendPanelVisible=true; guest detached; recoveredDevice={1}", _presentationSessionId, recovered));
                    if (recovered)
                    {
                        _ = InitializeFrontendSafelyAsync();
                    }
                    UwpGraphicsHost.Render();
                }
                return;
            }

            if (coreRunning || isGameActive)
            {
                if (!_coreWasRunning)
                {
                    _uiSoundPlayer.StopTheme();
                    // Do not leave the launch press queued in ImGui underneath
                    // gameplay. The shell receives input again only after every
                    // controller/keyboard control has returned to neutral.
                    UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                    UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
                    _coreWasRunning = true;
                    _gameInputReleaseBarrier = true;
                    _presentationSessionId = _coreSession.SessionId;
                    _presentationOwner = PresentationOwner.Game;
                    _presentationState = SessionPresentationState.WaitingFirstGuestFrame;
                    UwpFrontendLog.Write(
                        "presentation",
                        string.Format("[Session {0}] GuestSwapChainAttached; FrontendRenderLoopSuspended; FrontendPanelVisible=false", _presentationSessionId));
                }

                _frontendInputReleaseBarrier = true;
                _previousUiSoundButtons = GamepadButtons.None;
                UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
                if (coreRunning)
                {
                    UpdateBootAssets();
                    if (!_firstGuestPresentObserved && _coreSession.HasFirstVisiblePresent)
                    {
                        _firstGuestPresentObserved = true;
                        _bootScreen.FirstVisiblePresent();
                        UpdateBootScreenVisual();
                        UwpFrontendLog.Write("bootscreen", string.Format(
                            "[Session {0}] FirstVisiblePresent=true; starting 500 ms fade.",
                            _presentationSessionId));
                    }

                    _presentationState = _firstGuestPresentObserved
                        ? SessionPresentationState.GameVisible
                        : SessionPresentationState.WaitingFirstGuestFrame;
                    if (_gameInputReleaseBarrier)
                    {
                        // Do not leak the A/Enter press that launched the game
                        // into its first frames. Wait for a neutral sample and
                        // only forward input from the following frame onward.
                        _coreSession.SetInput(default(UwpInputSnapshot));
                        if (controlsReleased)
                        {
                            _gameInputReleaseBarrier = false;
                        }
                    }
                    else
                    {
                        _coreSession.SetInput(snapshot);
                    }
                }
                return;
            }

            // Starting a game can await brokered-file access before the
            // CoreHost thread appears. The library remains inert underneath
            // the boot screen throughout that period.
            if (_bootScreen.IsVisible)
            {
                UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
                UwpGraphicsHost.Render();
                return;
            }

            if (_coreWasRunning)
            {
                _coreWasRunning = false;
                _inputHost.ResetButtonState();
            }

            if (_frontendInputReleaseBarrier || _frontendCoordinator.IsActionInProgress)
            {
                if (_frontendCoordinator.IsActionInProgress)
                {
                    _frontendInputReleaseBarrier = true;
                }
                UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot), GamepadButtons.None);
                UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
                _pointerClickPending = false;
                _pointerWheel = 0;
                if (_frontendInputReleaseBarrier && controlsReleased && !_frontendCoordinator.IsActionInProgress)
                {
                    _frontendInputReleaseBarrier = false;
                }
                UwpGraphicsHost.Render();
                return;
            }
            GamepadButtons uiSoundMask = GamepadButtons.DPadUp |
                GamepadButtons.DPadDown |
                GamepadButtons.DPadLeft |
                GamepadButtons.DPadRight |
                GamepadButtons.A |
                GamepadButtons.B |
                GamepadButtons.X |
                GamepadButtons.Y |
                GamepadButtons.Menu |
                GamepadButtons.View |
                GamepadButtons.LeftShoulder |
                GamepadButtons.RightShoulder;
            bool buttonSound = ((uiButtons & ~_previousUiSoundButtons) & uiSoundMask) != 0;
            bool stickSound = snapshot.CrossedStickXThreshold(0.60) ||
                snapshot.CrossedStickYThreshold(0.60);
            if (buttonSound || stickSound || _pointerClickPending)
            {
                _uiSoundPlayer.Play();
            }
            _previousUiSoundButtons = uiButtons;

            UwpGraphicsHost.SetNativeImGuiInput(snapshot, _keyboardButtons);
            UwpGraphicsHost.SetNativeImGuiPointer(_pointerX, _pointerY,
                _pointerLeftDown || _pointerClickPending, _pointerRightDown, _pointerWheel);
            _pointerClickPending = false;
            _pointerWheel = 0;
            _frontendCoordinator.Pump();
            UwpGraphicsHost.Render();
        }

        private void TickBootScreen()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_lastBootScreenTick != default(DateTimeOffset))
            {
                _bootScreen.Tick(now - _lastBootScreenTick);
            }
            _lastBootScreenTick = now;
            UpdateBootScreenVisual();
        }

        private void ShowBootError(string error)
        {
            _bootScreen.Fail(error);
            // The error can be raised on the same frame as the launch button.
            // Require it to be released before either action is accepted.
            _previousBootErrorButtons = GamepadButtons.A | GamepadButtons.B | _keyboardButtons;
            UpdateBootScreenVisual();
            UwpFrontendLog.Write("bootscreen", "Launch failed before first visible present: " + error);
        }

        private void HandleBootErrorInput(GamepadButtons buttons)
        {
            GamepadButtons pressed = buttons & ~_previousBootErrorButtons;
            _previousBootErrorButtons = buttons;

            if ((pressed & GamepadButtons.A) != 0)
            {
                _firstGuestPresentObserved = false;
                _bootScreen.Begin("jogo");
                UpdateBootScreenVisual();
                _ = _frontendCoordinator.RetryLastGameAsync();
            }
            else if ((pressed & GamepadButtons.B) != 0)
            {
                _bootScreen.Hide();
                _frontendInputReleaseBarrier = true;
                _inputHost.ResetButtonState();
                UpdateBootScreenVisual();
            }
        }

        private void UpdateBootScreenVisual()
        {
            if (BootOverlay == null)
            {
                return;
            }

            bool visible = _bootScreen.IsVisible;
            BootOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            BootOverlay.Opacity = _bootScreen.Opacity;
            BootStatus.Text = _bootScreen.Status;
            bool isError = _bootScreen.State == BootScreenState.Error;
            BootSpinner.IsActive = visible && !isError;
            BootSpinner.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
            BootError.Text = _bootScreen.Error;
            BootError.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBootAssets()
        {
            ulong programId = _coreSession.ActiveProgramId;
            if (programId == _loadedBootAssetProgramId || programId == 0)
            {
                return;
            }

            _loadedBootAssetProgramId = programId;
            int mask = _coreSession.BootAssetMask;
            string titleId = programId.ToString("x16");

            if ((mask & 1) != 0)
            {
                BootLogo.Source = new BitmapImage(new Uri(
                    "ms-appdata:///local/boot-assets/" + titleId + "/NintendoLogo.png"));
            }
            else
            {
                BootLogo.Source = new BitmapImage(new Uri("ms-appx:///Assets/RyuboxLogoMaster.png"));
            }

            if ((mask & 2) != 0)
            {
                // UWP's GIF decoder plays frames using the delays embedded in
                // the source file; Uniform prevents any stretch on Xbox.
                BootStartupMovie.Source = new BitmapImage(new Uri(
                    "ms-appdata:///local/boot-assets/" + titleId + "/StartupMovie.gif"));
                BootStartupMovie.Visibility = Visibility.Visible;
            }
            else
            {
                BootStartupMovie.Source = null;
                BootStartupMovie.Visibility = Visibility.Collapsed;
            }
        }

        private void CoreWindow_PointerMoved(CoreWindow sender, PointerEventArgs args)
        {
            UpdateCorePointer(args);
        }

        private void CoreWindow_PointerPressed(CoreWindow sender, PointerEventArgs args)
        {
            UpdateCorePointer(args);
            _pointerClickPending = true;
            UwpFrontendLog.Write("input", string.Format("Pointer pressed at {0:F0},{1:F0}", _pointerX, _pointerY));
        }

        private void CoreWindow_PointerReleased(CoreWindow sender, PointerEventArgs args)
        {
            UpdateCorePointer(args);
        }

        private void CoreWindow_PointerWheelChanged(CoreWindow sender, PointerEventArgs args)
        {
            UpdateCorePointer(args);
            _pointerWheel += args.CurrentPoint.Properties.MouseWheelDelta / 120.0f;
        }

        private void UpdateCorePointer(PointerEventArgs args)
        {
            var point = args.CurrentPoint;
            _pointerX = (float)(point.Position.X * Math.Max(1.0, GameSurfacePanel.CompositionScaleX));
            _pointerY = (float)(point.Position.Y * Math.Max(1.0, GameSurfacePanel.CompositionScaleY));
            _pointerLeftDown = point.Properties.IsLeftButtonPressed;
            _pointerRightDown = point.Properties.IsRightButtonPressed;
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            _keyboardButtons |= MapKeyboardButton(args.VirtualKey);
        }

        private void CoreWindow_KeyUp(CoreWindow sender, KeyEventArgs args)
        {
            _keyboardButtons &= ~MapKeyboardButton(args.VirtualKey);
        }

        private static GamepadButtons MapKeyboardButton(VirtualKey key)
        {
            switch (key)
            {
                case VirtualKey.Up: case VirtualKey.W: return GamepadButtons.DPadUp;
                case VirtualKey.Down: case VirtualKey.S: return GamepadButtons.DPadDown;
                case VirtualKey.Left: case VirtualKey.A: return GamepadButtons.DPadLeft;
                case VirtualKey.Right: case VirtualKey.D: return GamepadButtons.DPadRight;
                case VirtualKey.Enter: case VirtualKey.Space: return GamepadButtons.A;
                case VirtualKey.Escape: return GamepadButtons.B;
                case VirtualKey.X: return GamepadButtons.X;
                case VirtualKey.Y: return GamepadButtons.Y;
                case VirtualKey.M: return GamepadButtons.Menu;
                case VirtualKey.V: return GamepadButtons.View;
                case VirtualKey.Q: return GamepadButtons.LeftShoulder;
                case VirtualKey.E: return GamepadButtons.RightShoulder;
                default: return GamepadButtons.None;
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_pageLoaded)
            {
                return;
            }

            _pageLoaded = false;
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Window.Current.CoreWindow.PointerMoved -= CoreWindow_PointerMoved;
            Window.Current.CoreWindow.PointerPressed -= CoreWindow_PointerPressed;
            Window.Current.CoreWindow.PointerReleased -= CoreWindow_PointerReleased;
            Window.Current.CoreWindow.PointerWheelChanged -= CoreWindow_PointerWheelChanged;
            Window.Current.CoreWindow.KeyDown -= CoreWindow_KeyDown;
            Window.Current.CoreWindow.KeyUp -= CoreWindow_KeyUp;
            _frontendCoordinator.BootPhaseChanged -= FrontendCoordinator_BootPhaseChanged;
            _frontendCoordinator.BootFailed -= FrontendCoordinator_BootFailed;

            _inputHost.Dispose();
            _coreSession.Dispose();
            _uiSoundPlayer.Dispose();
            
            if (_graphicsHostInitialized)
            {
                UwpGraphicsHost.Shutdown();
                _graphicsHostInitialized = false;
            }
        }

        internal void HandleAppSuspending()
        {
            UwpFrontendLog.Write("session", "Windows requested app suspension; stopping the active CoreHost session.");
            if (_coreSession.IsRunning)
            {
                _coreSession.Stop();
            }
            _inputHost.ResetButtonState();
        }

        internal void HandleAppResuming()
        {
            _inputHost.ResetButtonState();
        }

        private void GameSurfacePanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_graphicsHostInitialized) return;

            UwpGraphicsHost.GetPhysicalPanelSize(GameSurfacePanel,
                out uint width, out uint height);
            UwpGraphicsHost.Resize(width, height);
            
            string scaleError;
            UwpGraphicsHost.SetCompositionScale(GameSurfacePanel, out scaleError);
            UwpGraphicsHost.Render();
        }

        private void GameSurfacePanel_CompositionScaleChanged(SwapChainPanel sender, object args)
        {
            if (!_graphicsHostInitialized) return;

            string error;
            UwpGraphicsHost.SetCompositionScale(GameSurfacePanel, out error);
        }
    }
}
