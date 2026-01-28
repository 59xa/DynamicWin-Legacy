using DynamicWin.Resources;
using DynamicWin.UI.Menu;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.Utils;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace DynamicWin.Main
{
    public partial class MainForm : Window
    {
        private static MainForm instance;
        public static MainForm Instance { get => instance; }

        public static Action<System.Windows.Input.MouseWheelEventArgs> onScrollEvent;

        private readonly Forms.NotifyIcon _trayIcon;

        internal Forms.ToolStripMenuItem _settingsTrayItem;

        private DateTime _lastRenderTime;
        // Target interval driven by monitor refresh rate (set in ctor)
        private TimeSpan _targetElapsedTime;

        public Action onMainFormRender;

        // Mouse/motion tracking for idle detection
        private System.Windows.Point _lastMousePos = new System.Windows.Point(-1, -1);
        private DateTime _lastMouseMoveTime = DateTime.MinValue;
        private readonly TimeSpan _idleMouseThreshold = TimeSpan.FromSeconds(1.0);

        // Rendering pause flag (used for suspend/hibernate)
        private bool _renderPaused = false;

        #region Win32 API Definitions

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr window, int idx, int val);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr window, int idx);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Constants for Z-Order and Window Styles
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int WM_WINDOWPOSCHANGING = 0x0046;

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;

        #endregion

        public MainForm()
        {
            InitializeComponent();

            _trayIcon = new Forms.NotifyIcon();

            // Initialise mouse tracking
            _lastMouseMoveTime = DateTime.UtcNow;

            // Compute initial target frame interval from monitor refresh rate
            try
            {
                int refresh = DisplayHelper.GetRefreshRate();
                if (refresh <= 0) refresh = 60;
                _targetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / refresh);
                Debug.WriteLine($"[MAIN FORM] Initial target frame interval: {_targetElapsedTime.TotalMilliseconds} ms ({refresh} Hz)");
            }
            catch
            {
                _targetElapsedTime = TimeSpan.FromMilliseconds(16);
            }

            CompositionTarget.Rendering += OnRendering;

            instance = this;

            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;
            this.Topmost = true;
            this.AllowsTransparency = true;
            this.ShowInTaskbar = false;
            this.Title = "DynamicWin-Legacy Island";
            this.Icon = BitmapFrame.Create(new Uri(DynamicWinMain.ReleaseStream.GetIconPath(), UriKind.Relative));

            // Centre window horizontally on size change
            this.SizeChanged += (s, e) => CenterHorizontallyOnScreen();

            // Setup Win32 styles and initial topmost state
            this.Loaded += (s, e) =>
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int winStyle = GetWindowLong(handle, GWL_EXSTYLE);

                // Apply WS_EX_TOOLWINDOW and remove WS_EX_APPWINDOW
                winStyle = (winStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                SetWindowLong(handle, GWL_EXSTYLE, winStyle);

                // Placement happens after style is set
                SetMonitor(Settings.ScreenIndex);
                ForceTopMost();
            };

            // Re-assert Topmost if the user clicks away, ensuring it stays on top of other apps
            this.Deactivated += (s, e) => { ForceTopMost(); };

            AddRenderer();

            Res.extensions.ForEach((x) => x.LoadExtension());
            MainForm.Instance.AllowDrop = true;

            // Tray icon setup
            _trayIcon.Icon = new System.Drawing.Icon("Resources/icons/cog.ico");
            _trayIcon.Text = "DynamicWin-Legacy";

            _trayIcon.ContextMenuStrip = new Forms.ContextMenuStrip();

            _trayIcon.ContextMenuStrip.Opening += (s, e) =>
            {
                this.Topmost = false;
                Activate();
            };

            _trayIcon.ContextMenuStrip.Closing += (s, e) =>
            {
                this.Topmost = true;
                ForceTopMost();
            };

            _trayIcon.ContextMenuStrip.Items.Add("Restart Control", ContextMenuUtils.LoadTrayBitmap("Resources/icons/context/refresh.png"), (x, y) =>
            {
                if (RendererMain.Instance != null) RendererMain.Instance.Destroy();
                this.Content = new Grid();
                AddRenderer();
            });

            _settingsTrayItem = new Forms.ToolStripMenuItem("Settings");
            _settingsTrayItem.Image = ContextMenuUtils.LoadTrayBitmap("Resources/icons/context/cog.png");
            _settingsTrayItem.Click += (x, y) =>
            {
                MenuManager.OpenMenu(new SettingsMenu());
            };

            _trayIcon.ContextMenuStrip.Items.Add(_settingsTrayItem);

            _trayIcon.ContextMenuStrip.Items.Add("Exit", ContextMenuUtils.LoadTrayBitmap("Resources/icons/context/exit.png"), (x, y) =>
            {
                SaveManager.SaveAll();
                Process.GetCurrentProcess().Kill();
            });

            _trayIcon.Visible = true;
        }

        /// <summary>
        /// Forcefully sets the window to the top of the Z-order using Win32
        /// Call this on load, monitor change, or focus loss
        /// </summary>
        public void ForceTopMost()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        public void UpdateTrayButtons()
        {
            if (MenuManager.Instance.ActiveMenu is UpdaterMenu)
            {
                _settingsTrayItem.Enabled = false;
            }
            else
            {
                _settingsTrayItem.Enabled = true;
            }
        }

        public void SetMonitor(int monitorIndex)
        {
            int screenCount = GetMonitorCount();
            int validatedIndex = Math.Clamp(monitorIndex, 0, screenCount - 1);
            Settings.ScreenIndex = validatedIndex;

            var screen = Forms.Screen.AllScreens[validatedIndex];

            if (screen != null)
            {
                // Temporarily drop Topmost to allow the OS to move the window freely
                this.Topmost = false;

                if (!this.IsLoaded)
                    this.WindowStartupLocation = WindowStartupLocation.Manual;

                this.WindowState = WindowState.Normal;
                var workingArea = screen.WorkingArea;

                this.Width = 800;
                this.Height = 500;
                this.Top = workingArea.Top;

                CenterHorizontallyOnScreen();

                // Re-assert WPF Topmost
                this.Topmost = true;

                // Immediately Force Win32 Z-Order to the top of the new monitor
                ForceTopMost();
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (this.WindowState == WindowState.Normal)
            {
                CenterHorizontallyOnScreen();
            }
        }

        /// <summary>
        /// Ensures the window is horizontally centered on the current monitor.
        /// </summary>
        public void CenterHorizontallyOnScreen()
        {
            int screenIndex = Settings.ScreenIndex;
            var screens = Forms.Screen.AllScreens;
            if (screenIndex < 0 || screenIndex >= screens.Length) screenIndex = 0;
            var screen = screens[screenIndex];
            var workingArea = screen.WorkingArea;
            this.Left = workingArea.Left + (workingArea.Width / 2.0) - (this.Width / 2.0);
        }

        public static int GetMonitorCount()
        {
            return Forms.Screen.AllScreens.Length;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            // Skip rendering when paused (e.g., during system suspend/hibernate)
            if (_renderPaused) return;

            ForceTopMost();
            var now = DateTime.UtcNow;

            // Track mouse movement to detect idle while hovering the island
            try
            {
                var pos = System.Windows.Input.Mouse.GetPosition(this);
                if (pos.X != _lastMousePos.X || pos.Y != _lastMousePos.Y)
                {
                    _lastMouseMoveTime = now;
                    _lastMousePos = pos;
                }
            }
            catch { }

            // Decide refresh rate dynamically based on settings and idle state
            try
            {
                TimeSpan desiredInterval = TimeSpan.FromMilliseconds(16);

                if (Settings.ToggleHighRefreshRate)
                {
                    int displayRefresh = DisplayHelper.GetRefreshRate();
                    if (displayRefresh <= 0) displayRefresh = 60;

                    int targetHz = displayRefresh;

                    if (Settings.LimitRefreshRateWhenIdle)
                    {
                        bool islandHover = false;
                        try
                        {
                            islandHover = RendererMain.Instance?.MainIsland?.IsHovering ?? false;
                        }
                        catch { }

                        bool idle = !islandHover ||
                                    ((now - _lastMouseMoveTime) > _idleMouseThreshold);

                        if (idle)
                            targetHz = 60;
                    }

                    desiredInterval = TimeSpan.FromMilliseconds(1000.0 / targetHz);
                }

                _targetElapsedTime = desiredInterval;
            }
            catch { }

            var currentTime = DateTime.Now;
            if (currentTime - _lastRenderTime >= _targetElapsedTime)
            {
                _lastRenderTime = currentTime;

                onMainFormRender?.Invoke();
            }
        }

        public bool isDragging = false;

        public void OnScroll(object? sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            onScrollEvent?.Invoke(e);
        }

        public void AddRenderer()
        {
            if (RendererMain.Instance != null) RendererMain.Instance.Destroy();

            var customControl = new RendererMain();

            var parent = new Grid();
            parent.Children.Add(customControl);

            this.Content = parent;

            // Ensure the new renderer is called from the centralised, throttled MainForm loop
            onMainFormRender += customControl.Frame;
        }

        // Allow external modules to pause/resume the rendering loop during suspend/hibernate
        public void PauseRendering()
        {
            _renderPaused = true;
        }

        public void ResumeRendering()
        {
            _renderPaused = false;
            _lastRenderTime = DateTime.Now; // Reset timing to avoid immediate large update
        }

        public void MainForm_DragEnter(object? sender, DragEventArgs e)
        {
            isDragging = true;
            e.Effects = DragDropEffects.Copy;

            if (!(MenuManager.Instance.ActiveMenu is DropFileMenu)
                && !(MenuManager.Instance.ActiveMenu is ConfigureShortcutMenu))
            {
                MenuManager.OpenMenu(new DropFileMenu());
            }
        }

        public void MainForm_DragLeave(object? sender, EventArgs e)
        {
            isDragging = false;

            if (MenuManager.Instance.ActiveMenu is ConfigureShortcutMenu) return;
            MenuManager.OpenMenu(Res.HomeMenu);
        }

        bool isLocalDrag = false;

        internal void StartDrag(string[] files, Action callback)
        {
            if (isLocalDrag) return;

            Array.ForEach(files, file => { System.Diagnostics.Debug.WriteLine(file); });

            if (files == null) return;
            else if (files.Length <= 0) return;

            try
            {
                isLocalDrag = true;

                DataObject dataObject = new DataObject(DataFormats.FileDrop, files);
                var effects = DragDrop.DoDragDrop((DependencyObject)this, dataObject, DragDropEffects.Move | DragDropEffects.Copy);

                if (RendererMain.Instance != null) RendererMain.Instance.Destroy();
                this.Content = new Grid();
                AddRenderer();

                callback?.Invoke();

                isLocalDrag = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred: " + ex.Message);
            }
        }

        protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
        {
            if (e.Action == DragAction.Cancel)
            {
                isLocalDrag = false;
            }
            else if (e.Action == DragAction.Continue)
            {
                isLocalDrag = true;
            }
            else if (e.Action == DragAction.Drop)
            {
                isLocalDrag = false;
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            base.OnDragOver(e);
        }

        public void OnDrop(object sender, System.Windows.DragEventArgs e)
        {
            isDragging = false;

            if (MenuManager.Instance.ActiveMenu is ConfigureShortcutMenu)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    ConfigureShortcutMenu.DropData(e);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropFileMenu.Drop(e);
                MenuManager.Instance.QueueOpenMenu(Res.HomeMenu);
                Res.HomeMenu.isWidgetMode = false;
            }
        }

        internal void DisposeTrayIcon()
        {
            _trayIcon.Dispose();
        }
    }
}