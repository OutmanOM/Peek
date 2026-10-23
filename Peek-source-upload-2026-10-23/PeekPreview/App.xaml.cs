using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PeekPreview
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private static Mutex? _instanceMutex;
        private const string ActivationMessageName = "PeekPreview.Activate.2026";
        private static readonly IntPtr HwndBroadcast = new(0xFFFF);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                LogFailure(eventArgs.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                LogFailure(eventArgs.Exception);
                eventArgs.SetObserved();
            };
            UnhandledException += (_, eventArgs) =>
            {
                LogFailure(eventArgs.Exception);
                if (eventArgs.Exception is System.Runtime.InteropServices.COMException exception
                    && exception.HResult == unchecked((int)0x80004004))
                {
                    eventArgs.Handled = true;
                }
            };
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _instanceMutex = new Mutex(true, @"Local\PeekPreview.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                uint activateMessage = RegisterWindowMessage(ActivationMessageName);
                if (activateMessage != 0)
                {
                    // The preview window title includes the current filename, so an exact
                    // FindWindow("Peek") lookup fails after the first preview is hidden.
                    // Broadcast the private activation message so the existing instance can
                    // find its own window regardless of the current title or visibility.
                    PostMessage(HwndBroadcast, activateMessage, IntPtr.Zero, IntPtr.Zero);
                }
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return;
            }

            var mainWindow = new MainWindow();
            _window = mainWindow;
            mainWindow.LaunchFromCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray());
        }

        private static void LogFailure(Exception? exception)
        {
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                Directory.CreateDirectory(folder);
                File.AppendAllText(System.IO.Path.Combine(folder, "crash.log"),
                    $"[{DateTimeOffset.Now:O}] {exception?.GetType().FullName ?? "Unknown failure"}; HRESULT=0x{exception?.HResult:X8}{Environment.NewLine}");
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
