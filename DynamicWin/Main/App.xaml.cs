using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.Utils;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DynamicWin
{
    public partial class DynamicWinMain : Application
    {
        public static MMDevice defaultDevice;
        public static MMDevice defaultMicrophone;

        public static string Version => "v1.5.0a7";
        public static string ReleaseStream => "canary";

        [STAThread]
        public static void Main()
        {
            DynamicWinMain m = new DynamicWinMain();
            m.Run();
        }

        public static void UpdateStartup()
        {
            try
            {
                if (Settings.RunOnStartup)
                {
                    StartupShortcutManager.CreateShortcut();
                }
                else
                {
                    StartupShortcutManager.RemoveShortcut();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add application to startup: {ex.Message}");
            }
        }

        Mutex mutex;
        private DispatcherTimer topmostTimer; // timer to keep window absolutely topmost
        private MainForm mainForm;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Dispatcher.UnhandledException += Dispatcher_UnhandledException;

            bool result;
            mutex = new Mutex(true, "FlorianButz.DynamicWin", out result);

            if (!result)
            {
                ErrorForm errorForm = new ErrorForm();
                errorForm.Show();
                return;
            }

            try
            {
                var devEnum = new MMDeviceEnumerator();
                defaultDevice = devEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultMicrophone = devEnum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch
            {
                defaultDevice = null;
                defaultMicrophone = null;
            }

            SaveManager.LoadData();
            Res.Load();
            KeyHandler.Start();
            new Theme();
            new HardwareMonitor();
            Settings.InitializeSettings();
            Migrations.MakeSmallWidgetMigrations();
            UpdateStartup();

            mainForm = new MainForm
            {
                Width = 600,
                Height = 600,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            mainForm.Left = (screenWidth - mainForm.Width) / 2;

            mainForm.Top = 0;

            mainForm.Show();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ForceTopMost(mainForm);
            }), DispatcherPriority.ApplicationIdle);

            topmostTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            topmostTimer.Tick += (_, _) => ForceTopMost(mainForm);
            topmostTimer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);

            topmostTimer?.Stop();
            SaveManager.SaveAll();
            HardwareMonitor.Stop();
            MainForm.Instance.DisposeTrayIcon();
            KeyHandler.Stop();
            GC.KeepAlive(mutex);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"Unhandled exception: {e.ExceptionObject}");
        }

        private void Dispatcher_UnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"Unhandled exception: {e.Exception}");
            e.Handled = true;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private void ForceTopMost(Window window)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }
    }
}
