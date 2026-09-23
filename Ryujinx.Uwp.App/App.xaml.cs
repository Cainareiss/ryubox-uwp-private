using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.System.Profile;

namespace Ryujinx.Uwp.App
{
    sealed partial class App : Application
    {
        private bool _systemBackSuppressionInstalled;

        public App()
        {
            InitializeComponent();
            // Xbox must stay in directional focus mode. Without this setting,
            // UWP can translate the stick into a synthetic mouse cursor.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            if (IsXboxDevice())
            {
                ApplicationView.PreferredLaunchWindowingMode =
                    ApplicationViewWindowingMode.FullScreen;
            }
            Suspending += OnSuspending;
            Resuming += OnResuming;
            UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            UwpFrontendLog.Write("errors", "UnhandledException: " + e.Exception);
            // Keep diagnostic startup failures observable. Without marking the
            // event handled, UWP tears down the process before the frontend
            // log can be inspected, which turns a recoverable setup error
            // into an opaque KERNELBASE crash.
            e.Handled = true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            InstallSystemBackSuppression();

            // Xbox defaults UWP content to the 90% title-safe rectangle. That
            // produced the measured 1728x972 guest panel and made the TV scale
            // the image a second time. Request the complete CoreWindow so a
            // 16:9 swap chain maps edge-to-edge at 1920x1080.
            bool fullBounds = ApplicationView.GetForCurrentView().SetDesiredBoundsMode(
                ApplicationViewBoundsMode.UseCoreWindow);
            UwpFrontendLog.Write("graphics", "CoreWindow full-bounds request=" + fullBounds);

            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();

            // Use the actual display area on desktop too. SetDesiredBoundsMode
            // only removes Xbox's title-safe inset; it does not hide the PC
            // title bar/taskbar by itself.
            ApplicationView view = ApplicationView.GetForCurrentView();
            bool isXbox = string.Equals(
                AnalyticsInfo.VersionInfo.DeviceFamily,
                "Windows.Xbox",
                System.StringComparison.OrdinalIgnoreCase);
            if (isXbox)
            {
                view.SuppressSystemOverlays = true;
                view.SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            }
            bool enteredFullScreen = isXbox &&
                (view.IsFullScreenMode || view.TryEnterFullScreenMode());
            UwpFrontendLog.Write("graphics", string.Format(
                "ApplicationView fullscreen={0}, deviceFamily={1}",
                enteredFullScreen,
                AnalyticsInfo.VersionInfo.DeviceFamily));
        }

        private void InstallSystemBackSuppression()
        {
            if (_systemBackSuppressionInstalled)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += OnSystemBackRequested;
            _systemBackSuppressionInstalled = true;
            UwpFrontendLog.Write("input", "Xbox/UWP system back suppression installed");
        }

        private static void OnSystemBackRequested(object sender, BackRequestedEventArgs e)
        {
            // On Xbox, gamepad B is projected as the UWP system Back command
            // in addition to Windows.Gaming.Input. Consume only that system
            // command; the raw B state still reaches the emulated controller.
            e.Handled = true;
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                MainPage page = GetMainPage();
                if (page != null)
                {
                    page.HandleAppSuspending();
                }
            }
            catch (System.Exception exception)
            {
                UwpFrontendLog.Error("tratando suspensao", exception);
            }
            finally
            {
                // Xbox gives UWP apps only a short suspension deadline.  The
                // deferral must always be completed even if diagnostics fail.
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e)
        {
            if (IsXboxDevice())
            {
                ApplicationView view = ApplicationView.GetForCurrentView();
                view.SuppressSystemOverlays = true;
                view.SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
                if (!view.IsFullScreenMode)
                {
                    view.TryEnterFullScreenMode();
                }
            }
            MainPage page = GetMainPage();
            if (page != null)
            {
                page.HandleAppResuming();
            }
        }

        private static MainPage GetMainPage()
        {
            Frame rootFrame = Window.Current.Content as Frame;
            return rootFrame == null ? null : rootFrame.Content as MainPage;
        }

        private static bool IsXboxDevice()
        {
            return string.Equals(
                AnalyticsInfo.VersionInfo.DeviceFamily,
                "Windows.Xbox",
                System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
