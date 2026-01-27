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
using System.Timers;

namespace DynamicWin
{
    public partial class DynamicWinMain : Application
    {
        public static MMDevice defaultDevice;
        public static MMDevice defaultMicrophone;

        public static string Version => "v1.5.1b3";
        public static Channel ReleaseStream => Channel.Canary;

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
        private System.Timers.Timer topmostTimer; // use System.Timers.Timer to avoid creating Win32 dispatcher timers
        private MainForm mainForm;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Dispatcher.UnhandledException += Dispatcher_UnhandledException;

            bool result;
            mutex = new Mutex(true, "59xa.DynamicWin", out result);

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

            // Ensure media manager is initialised early on an STA thread to avoid races when UI queries media
            try
            {
                MediaInfo.Initialize();
            }
            catch { }

            mainForm = new MainForm
            {
                Width = 800,
                Height = 400,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = false,
            };

            mainForm.SizeToContent = SizeToContent.Manual;

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            mainForm.Left = (screenWidth - mainForm.Width) / 2;

            mainForm.Top = 0;

            mainForm.Show();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ForceTopMost(mainForm);
            }), DispatcherPriority.ApplicationIdle);

            // Use a System.Timers.Timer instead of DispatcherTimer to avoid exhausting Win32 timer handles
            try
            {
                CreateTopmostTimer();
            }
            catch { }

            // Subscribe to system power events to handle suspend/resume gracefully
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);

            try
            {
                topmostTimer?.Stop();
                topmostTimer?.Dispose();
                topmostTimer = null;
            }
            catch { }

            SaveManager.SaveAll();
            HardwareMonitor.Stop();
            MainForm.Instance.DisposeTrayIcon();
            KeyHandler.Stop();
            GC.KeepAlive(mutex);

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
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
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    HandleSuspend();
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    HandleResume();
                }
            }
            catch { }
        }

        private void HandleSuspend()
        {
            try
            {
                // Stop and dispose the periodic topmost enforcement timer to free native handles
                try { topmostTimer?.Stop(); topmostTimer?.Dispose(); topmostTimer = null; } catch { }

                // Pause rendering loop in main form
                try { MainForm.Instance?.PauseRendering(); } catch { }

                // Stop hardware monitoring and background services
                try { HardwareMonitor.Stop(); } catch { }

                try { MediaInfo.Reset(); } catch { }

                try { WeatherAPI.Default.StopFetching(); } catch { }

#if DEBUG
                Debug.WriteLine("[SYSTEM] Suspend handled: paused timers and background workers.");
#endif
            }
            catch { }
        }

        private void HandleResume()
        {
            try
            {
                // Recreate and start topmost timer if needed
                try { if (topmostTimer == null) CreateTopmostTimer(); else topmostTimer.Start(); } catch { }

                // Resume main rendering loop
                try { MainForm.Instance?.ResumeRendering(); } catch { }

                // Re-initialise media manager and hardware monitor
                try { MediaInfo.Initialize(); } catch { }

                try { new HardwareMonitor(); } catch { }

                // Force window to topmost once after resume
                try { ForceTopMost(mainForm); } catch { }

#if DEBUG
                Debug.WriteLine("[SYSTEM] Resume handled: restarted timers and background workers.");
#endif
            }
            catch { }
        }

        private void CreateTopmostTimer()
        {
            // Ensure existing timer is cleaned up first
            try { topmostTimer?.Stop(); topmostTimer?.Dispose(); topmostTimer = null; } catch { }

            topmostTimer = new System.Timers.Timer(500);
            topmostTimer.AutoReset = true;
            topmostTimer.Elapsed += (s, args) =>
            {
                try
                {
                    // Marshal to UI thread to call ForceTopMost
                    Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        try { ForceTopMost(mainForm); } catch { }
                    }), DispatcherPriority.Normal);
                }
                catch { }
            };

            topmostTimer.Start();
        }
    }
}
