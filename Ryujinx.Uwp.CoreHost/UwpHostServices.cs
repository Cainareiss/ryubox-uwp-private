using Ryujinx.Common.Configuration.Hid;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using System;

namespace Ryujinx.Uwp.CoreHost
{
    internal sealed class UwpHostUiTheme : IHostUITheme
    {
        public string FontFamily => "sans-serif";
        public ThemeColor DefaultBackgroundColor => new(1, 0, 0, 0);
        public ThemeColor DefaultForegroundColor => new(1, 1, 1, 1);
        public ThemeColor DefaultBorderColor => new(1, 1, 1, 1);
        public ThemeColor SelectionBackgroundColor => new(1, 1, 1, 1);
        public ThemeColor SelectionForegroundColor => new(1, 0, 0, 0);
    }

    internal sealed class UwpDynamicTextInputHandler : IDynamicTextInputHandler
    {
        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent { add { } remove { } }
        public event KeyReleasedHandler KeyReleasedEvent { add { } remove { } }
        public bool TextProcessingEnabled { get; set; }
        public void SetText(string text, int cursorBegin) { }
        public void SetText(string text, int cursorBegin, int cursorEnd) { }
        public void Dispose() { }
    }

    internal sealed class UwpHostUiHandler : IHostUIHandler
    {
        private readonly UwpHostUiTheme _theme = new();
        private int _controllerDialogAttempts;

        public IHostUITheme HostUITheme => _theme;

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            userText = args.InitialText ?? string.Empty;
            return true;
        }

        public bool DisplayMessageDialog(string title, string message) => true;

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            if (++_controllerDialogAttempts > 2)
            {
                _controllerDialogAttempts = 0;
                return false;
            }

            CoreHostExports.AutoConfigureControllers(args);
            return true;
        }

        public bool DisplayCabinetDialog(out string userText)
        {
            userText = "Ryujinx";
            return true;
        }

        public void DisplayCabinetMessageDialog() { }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null)
            => false;

        public IDynamicTextInputHandler CreateDynamicTextInputHandler() => new UwpDynamicTextInputHandler();

        public UserProfile ShowPlayerSelectDialog()
            => new(AccountManager.DefaultUserId, "Ryujinx", Array.Empty<byte>());

        public void TakeScreenshot() { }
    }
}
