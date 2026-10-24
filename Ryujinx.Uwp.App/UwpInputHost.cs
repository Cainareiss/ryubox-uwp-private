using System;
using System.Linq;
using Windows.Gaming.Input;

namespace Ryujinx.Uwp.App
{
    internal sealed class UwpInputHost : IDisposable
    {
        private Gamepad _activeGamepad;
        private GamepadButtons _previousButtons;
        private double _previousStickX;
        private double _previousStickY;
        private bool _started;

        public event Action<Gamepad> ActiveGamepadChanged;

        public Gamepad ActiveGamepad
        {
            get { return _activeGamepad; }
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            try
            {
                _started = true;
                Gamepad.GamepadAdded += Gamepad_GamepadAdded;
                Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
                SetActiveGamepad(Gamepad.Gamepads.FirstOrDefault());
                UwpFrontendLog.Write("input", "Gamepad input host started");
            }
            catch (Exception exception)
            {
                _started = false;
                UwpFrontendLog.Error("inicializando gamepad", exception);
            }
        }

        public bool TryRead(out UwpInputSnapshot snapshot)
        {
            snapshot = default(UwpInputSnapshot);

            if (_activeGamepad == null)
            {
                return false;
            }

            GamepadReading reading;
            try
            {
                reading = _activeGamepad.GetCurrentReading();
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("lendo gamepad", exception);
                SetActiveGamepad(Gamepad.Gamepads.FirstOrDefault());
                return false;
            }

            snapshot = new UwpInputSnapshot(
                reading.Buttons,
                _previousButtons,
                reading.LeftThumbstickX,
                reading.LeftThumbstickY,
                reading.RightThumbstickX,
                reading.RightThumbstickY,
                reading.LeftTrigger,
                reading.RightTrigger,
                _previousStickX,
                _previousStickY);

            _previousButtons = reading.Buttons;
            _previousStickX = reading.LeftThumbstickX;
            _previousStickY = reading.LeftThumbstickY;
            return true;
        }

        public void ResetButtonState()
        {
            _previousButtons = GamepadButtons.None;
            _previousStickX = 0;
            _previousStickY = 0;
        }

        public void Dispose()
        {
            if (!_started)
            {
                return;
            }

            Gamepad.GamepadAdded -= Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved;
            _started = false;
            SetActiveGamepad(null);
            UwpFrontendLog.Write("input", "Gamepad input host stopped");
        }

        private void Gamepad_GamepadAdded(object sender, Gamepad gamepad)
        {
            UwpFrontendLog.Write("input", "Gamepad added");
            if (_activeGamepad == null)
            {
                SetActiveGamepad(gamepad);
            }
        }

        private void Gamepad_GamepadRemoved(object sender, Gamepad gamepad)
        {
            UwpFrontendLog.Write("input", "Gamepad removed");
            if (_activeGamepad == gamepad)
            {
                SetActiveGamepad(Gamepad.Gamepads.FirstOrDefault());
            }
        }

        private void SetActiveGamepad(Gamepad gamepad)
        {
            _activeGamepad = gamepad;
            _previousButtons = GamepadButtons.None;
            _previousStickX = 0;
            _previousStickY = 0;
            UwpFrontendLog.Write("input", gamepad == null ? "No active gamepad" : "Active gamepad connected");
            ActiveGamepadChanged?.Invoke(gamepad);
        }
    }

    internal readonly struct UwpInputSnapshot
    {
        public UwpInputSnapshot(
            GamepadButtons buttons,
            GamepadButtons previousButtons,
            double leftThumbstickX,
            double leftThumbstickY,
            double rightThumbstickX,
            double rightThumbstickY,
            double leftTrigger,
            double rightTrigger,
            double previousStickX,
            double previousStickY)
        {
            Buttons = buttons;
            PreviousButtons = previousButtons;
            LeftThumbstickX = leftThumbstickX;
            LeftThumbstickY = leftThumbstickY;
            RightThumbstickX = rightThumbstickX;
            RightThumbstickY = rightThumbstickY;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            PreviousStickX = previousStickX;
            PreviousStickY = previousStickY;
        }

        public GamepadButtons Buttons { get; }
        public GamepadButtons PreviousButtons { get; }
        public double LeftThumbstickX { get; }
        public double LeftThumbstickY { get; }
        public double RightThumbstickX { get; }
        public double RightThumbstickY { get; }
        public double LeftTrigger { get; }
        public double RightTrigger { get; }

        public bool WasPressed(GamepadButtons button)
        {
            return (Buttons & button) == button && (PreviousButtons & button) != button;
        }

        public bool CrossedStickXThreshold(double threshold)
        {
            return (LeftThumbstickX > threshold && PreviousStickX <= threshold)
                || (LeftThumbstickX < -threshold && PreviousStickX >= -threshold);
        }

        public bool CrossedStickYThreshold(double threshold)
        {
            return (LeftThumbstickY > threshold && PreviousStickY <= threshold)
                || (LeftThumbstickY < -threshold && PreviousStickY >= -threshold);
        }

        private double PreviousStickX { get; }
        private double PreviousStickY { get; }
    }
}
