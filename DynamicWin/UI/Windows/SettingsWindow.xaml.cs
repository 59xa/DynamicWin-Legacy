using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DynamicWin.Main;
using DynamicWin.Utils;
using DynamicWin.UI.UIElements;

namespace DynamicWin.UI.Forms
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            ApplyWindowTheme();
            Nav_General(null, null);
        }

        private void ApplyWindowTheme()
        {
            // Resource base color defaults
            Color accentColor = Color.FromRgb(160, 32, 240); // Default Purple
            Color borderColor = Color.FromArgb(40, 255, 255, 255);
            Brush mainBackground = new SolidColorBrush(Color.FromRgb(13, 13, 13));
            Brush sidebarBackground = new SolidColorBrush(Color.FromRgb(19, 19, 19));

            switch (Settings.Theme)
            {
                case 0: // Standard Dark
                    accentColor = Color.FromRgb(60, 60, 60);
                    mainBackground = new SolidColorBrush(Color.FromRgb(15, 15, 15));
                    sidebarBackground = new SolidColorBrush(Color.FromRgb(20, 20, 20));
                    break;

                case 1: // Standard Light
                    accentColor = Color.FromRgb(0, 122, 255); // SF Blue
                    var lightBg = new LinearGradientBrush(Color.FromRgb(245, 245, 247), Color.FromRgb(230, 230, 235), 45);
                    mainBackground = lightBg;
                    sidebarBackground = new SolidColorBrush(Color.FromRgb(235, 235, 240));
                    borderColor = Color.FromArgb(30, 0, 0, 0);
                    break;

                case 2: // Candy Pop
                    accentColor = Color.FromRgb(255, 45, 85); // Pink
                    mainBackground = new LinearGradientBrush(Color.FromRgb(45, 10, 25), Color.FromRgb(20, 5, 15), 45);
                    sidebarBackground = new SolidColorBrush(Color.FromArgb(100, 30, 5, 15));
                    break;

                case 3: // Forest Dawn
                    accentColor = Color.FromRgb(52, 199, 89); // Green
                    mainBackground = new LinearGradientBrush(Color.FromRgb(5, 25, 20), Color.FromRgb(2, 15, 10), 45);
                    sidebarBackground = new SolidColorBrush(Color.FromArgb(100, 2, 20, 15));
                    break;

                case 4: // Sunset Glow
                    accentColor = Color.FromRgb(255, 149, 0); // Orange
                    mainBackground = new LinearGradientBrush(Color.FromRgb(40, 15, 5), Color.FromRgb(20, 5, 20), 45);
                    sidebarBackground = new SolidColorBrush(Color.FromArgb(100, 30, 10, 20));
                    break;

                case 5: // Aydo (Exclusive)
                    accentColor = Color.FromRgb(160, 32, 240);
                    var aydoBg = new LinearGradientBrush();
                    aydoBg.StartPoint = new Point(0, 0);
                    aydoBg.EndPoint = new Point(1, 1);
                    aydoBg.GradientStops.Add(new GradientStop(Color.FromRgb(26, 11, 46), 0.0));
                    aydoBg.GradientStops.Add(new GradientStop(Color.FromRgb(40, 9, 87), 0.5));
                    aydoBg.GradientStops.Add(new GradientStop(Color.FromRgb(20, 5, 40), 1.0));
                    mainBackground = aydoBg;
                    sidebarBackground = new SolidColorBrush(Color.FromArgb(80, 15, 5, 30));
                    break;
            }

            // Apply resources dynamically
            var foreground = (Settings.Theme == 1) ? new SolidColorBrush(Color.FromRgb(30, 30, 30)) : Brushes.White;
            this.Resources["AccentPurple"] = new SolidColorBrush(accentColor);
            this.Resources["BorderBrush"] = new SolidColorBrush(borderColor);
            this.Resources["TextForeground"] = foreground;
            
            MainContainer.Background = mainBackground;
            SidebarContainer.Background = sidebarBackground;
            
            // Sidebar buttons should also respect text color but we usually keep them white/gray for contrast
        }

        private void ClearContent()
        {
            ContentPanel.Children.Clear();
        }

        private void AddHeader(string text)
        {
            var header = new TextBlock
            {
                Text = text,
                FontSize = 28,
                Margin = new Thickness(0, 0, 0, 25),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)this.Resources["TextForeground"]
            };
            ContentPanel.Children.Add(header);
        }

        private void CreateSetting(string label, bool initialValue, Action<bool> onToggle, string description = "")
        {
            bool isLight = Settings.Theme == 1;
            var accentBrush = (Brush)this.Resources["AccentPurple"];
            
            var container = new Border
            {
                Background = isLight ? new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(90, 10, 10, 10)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 15, 18, 15),
                Margin = new Thickness(0, 0, 0, 14),
                BorderBrush = isLight ? new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };

            // Subtle glow effect for the border using accent color if not light theme
            if (!isLight)
            {
                var accentColor = ((SolidColorBrush)accentBrush).Color;
                container.BorderBrush = new SolidColorBrush(Color.FromArgb(80, accentColor.R, accentColor.G, accentColor.B));
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock 
            { 
                Text = label, 
                FontSize = 15.5, 
                FontWeight = FontWeights.Bold, 
                Foreground = (Brush)this.Resources["TextForeground"]
            });

            if (!string.IsNullOrEmpty(description))
            {
                textStack.Children.Add(new TextBlock 
                { 
                    Text = description, 
                    FontSize = 12.5, 
                    Foreground = isLight ? new SolidColorBrush(Color.FromRgb(110, 110, 115)) : new SolidColorBrush(Color.FromRgb(180, 180, 190)),
                    Margin = new Thickness(0, 4, 10, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                });
            }

            var cb = new CheckBox
            {
                Style = (Style)FindResource("ModernToggle"),
                IsChecked = initialValue,
                VerticalAlignment = VerticalAlignment.Center
            };
            cb.Checked += (s, e) => onToggle(true);
            cb.Unchecked += (s, e) => onToggle(false);

            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(cb, 1);
            grid.Children.Add(textStack);
            grid.Children.Add(cb);

            container.Child = grid;
            ContentPanel.Children.Add(container);
        }

        public void Nav_General(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("General");

            CreateSetting("Launch at Startup", Settings.RunOnStartup, (v) => Settings.RunOnStartup = v, "Automatically start DynamicWin when you log into Windows.");
            CreateSetting("Stay on Top", Settings.AlwaysTopmost, (v) => Settings.AlwaysTopmost = v, "Keep the island visible above all other windows.");
            CreateSetting("Automatic Updates", Settings.AllowAutomaticUpdates, (v) => Settings.AllowAutomaticUpdates = v, "Download and install updates silently.");
            CreateSetting("High Performance Mode", Settings.ToggleHighRefreshRate, (v) => Settings.ToggleHighRefreshRate = v, "Increases rendering refresh rate for maximum smoothness.");
        }

        public void Nav_Appearance(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Appearance");

            var themeSection = new StackPanel { Margin = new Thickness(0,0,0,20) };
            themeSection.Children.Add(new TextBlock { Text = "Interface Theme", Margin = new Thickness(5, 0, 0, 8), Foreground = Brushes.Gray, FontSize = 12 });
            
            var themeCombo = new ComboBox { Height = 40 };
            themeCombo.Items.Add("Standard Dark");
            themeCombo.Items.Add("Standard Light");
            themeCombo.Items.Add("Candy Pop");
            themeCombo.Items.Add("Forest Dawn");
            themeCombo.Items.Add("Sunset Glow");
            themeCombo.Items.Add("Aydo (Exclusive)");

            themeCombo.SelectedIndex = Math.Clamp(Settings.Theme, 0, 5);
            themeCombo.SelectionChanged += (s, ev) => {
                Settings.Theme = themeCombo.SelectedIndex;
                DynamicWin.Utils.Theme.Instance.UpdateTheme(true);
                ApplyWindowTheme();
                Nav_Appearance(null, null); // Refresh this page to update visuals
            };
            themeSection.Children.Add(themeCombo);
            ContentPanel.Children.Add(themeSection);

            var styleSection = new StackPanel { Margin = new Thickness(0,0,0,20) };
            styleSection.Children.Add(new TextBlock { Text = "Island Style", Margin = new Thickness(5, 0, 0, 8), Foreground = Brushes.Gray, FontSize = 12 });
            
            var styleCombo = new ComboBox { Height = 40 };
            styleCombo.Items.Add("Standalone Island");
            styleCombo.Items.Add("Top Notch Style");
            styleCombo.SelectedIndex = (Settings.IslandMode == IslandObject.IslandMode.Island) ? 0 : 1;
            styleCombo.SelectionChanged += (s, ev) => {
                Settings.IslandMode = styleCombo.SelectedIndex == 0 ? IslandObject.IslandMode.Island : IslandObject.IslandMode.Notch;
                if (MainForm.Instance != null) MainForm.Instance.UpdateWindowPosition();
            };
            styleSection.Children.Add(styleCombo);
            ContentPanel.Children.Add(styleSection);

            // Aero Glass Blur is temporarily disabled per user request
            // CreateSetting("Aero Glass Blur", Settings.AllowBlur, (v) => {
            //     Settings.AllowBlur = v;
            //     if (MainForm.Instance != null) MainForm.Instance.ApplyBlur(); // Update island blur
            // }, "Enable modern acrylic background blur effect.");
            CreateSetting("Smooth Animations", Settings.AllowAnimation, (v) => Settings.AllowAnimation = v, "Enable fluid transitions and island movements.");
            CreateSetting("Edge Anti-Aliasing", Settings.AntiAliasing, (v) => Settings.AntiAliasing = v, "Smoothens the edges of the island and icons.");
            CreateSetting("Island Shadow", Settings.ToggleIslandShadow, (v) => Settings.ToggleIslandShadow = v, "Adds a subtle depth shadow behind the island.");
        }

        public void Nav_Popups(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("System Events");

            CreateSetting("Volume Overlay", PopupOptions.saveData.volumePopup, (v) => PopupOptions.saveData.volumePopup = v, "Show animated island indicator when system volume changes.");
            CreateSetting("Brightness Overlay", PopupOptions.saveData.brightnessPopup, (v) => PopupOptions.saveData.brightnessPopup = v, "Show animated island indicator when display brightness changes.");
        }

        public void Nav_Pomodoro(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Pomodoro Timer");
            ContentPanel.Children.Add(new TextBlock { Text = "Focus session settings and break intervals will be available here.", Foreground = Brushes.Gray, FontSize = 14, Margin = new Thickness(5, 10, 0, 0) });
        }

        public void Nav_Weather(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Weather Service");
            ContentPanel.Children.Add(new TextBlock { Text = "Configure your location and weather provider settings.", Foreground = Brushes.Gray, FontSize = 14, Margin = new Thickness(5, 10, 0, 0) });
        }

        public void Nav_Apps(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Apps Launcher");
            ContentPanel.Children.Add(new TextBlock { Text = "Customize your favorite apps for quick access from the island.", Foreground = Brushes.Gray, FontSize = 14, Margin = new Thickness(5, 10, 0, 0) });
        }

        public void Nav_Widgets(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Widgets");
            ContentPanel.Children.Add(new TextBlock { Text = "Widget configuration is currently integrated with the main island menu for real-time previewing.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        }

        public void Nav_Monitors(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("Monitors");

            CreateSetting("Shorten Display Area", Settings.ShortenWindowsWorkingArea, (v) => Settings.ShortenWindowsWorkingArea = v, "Reserves screen space at the top to prevent windows from covering the island.");
            
            var monitorStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            monitorStack.Children.Add(new TextBlock { Text = "Active Monitor", Margin = new Thickness(5, 0, 0, 8), Foreground = Brushes.Gray, FontSize = 12 });
            
            var monitorCombo = new ComboBox { Height = 40 };
            var screens = System.Windows.Forms.Screen.AllScreens;
            for(int i = 0; i < screens.Length; i++)
                monitorCombo.Items.Add($"Display {i+1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})");
            
            monitorCombo.SelectedIndex = Math.Clamp(Settings.ScreenIndex, 0, screens.Length - 1);
            monitorCombo.SelectionChanged += (s, ev) => {
                Settings.ScreenIndex = monitorCombo.SelectedIndex;
                if (MainForm.Instance != null) MainForm.Instance.SetMonitor(Settings.ScreenIndex);
            };
            
            monitorStack.Children.Add(monitorCombo);
            ContentPanel.Children.Add(monitorStack);
        }

        public void Nav_About(object sender, RoutedEventArgs e)
        {
            ClearContent();
            AddHeader("About DynamicWin");

            bool isLight = Settings.Theme == 1;
            var aboutBox = new Border { Background = isLight ? new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(60, 20, 20, 20)), CornerRadius = new CornerRadius(15), Padding = new Thickness(25), Margin = new Thickness(0,10,0,0) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Lead Designer: Florian Butz", Foreground = (Brush)this.Resources["TextForeground"], FontSize = 16, Margin = new Thickness(0,0,0,10) });
            stack.Children.Add(new TextBlock { Text = "Lead Developer: 59xa", Foreground = (Brush)this.Resources["TextForeground"], FontSize = 16, Margin = new Thickness(0,0,0,10) });
            stack.Children.Add(new TextBlock { Text = "Side Developer: aydocs", Foreground = Brushes.MediumPurple, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,20) });
            
            stack.Children.Add(new Separator { Background = isLight ? new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)) : new SolidColorBrush(Color.FromRgb(40,40,40)), Margin = new Thickness(0,0,0,20) });
            stack.Children.Add(new TextBlock { Text = "DynamicWin Legacy brings the best of macOS and iOS aesthetics to Windows with a refined, themed interface.", Foreground = isLight ? new SolidColorBrush(Color.FromRgb(100, 100, 100)) : Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            
            aboutBox.Child = stack;
            ContentPanel.Children.Add(aboutBox);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Settings.Save();
            var popups = new PopupOptions();
            popups.SaveSettings();

            DynamicWinMain.UpdateStartup();
            if (MainForm.Instance != null) 
            {
                MainForm.Instance.UpdateWindowPosition();
                MainForm.Instance.ApplyBlur(); // Update island blur immediately
            }
            Close();
        }

        #region Blur API for Settings Window
        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData { public WindowCompositionAttribute Attribute; public IntPtr Data; public int SizeOfData; }
        internal enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }
        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy { public AccentState AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }
        internal enum AccentState { ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4 }

        public void ApplyBlur()
        {
            try {
                var handle = new WindowInteropHelper(this).Handle;
                var accent = new AccentPolicy();
                var accentStructSize = Marshal.SizeOf(accent);

                if (Settings.AllowBlur) {
                    accent.AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    // Lighter tint for better glass clarity (Aero style)
                    accent.GradientColor = 0x30FFFFFF; 
                } else {
                    accent.AccentState = AccentState.ACCENT_DISABLED;
                }

                var accentPtr = Marshal.AllocHGlobal(accentStructSize);
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData { Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY, SizeOfData = accentStructSize, Data = accentPtr };
                SetWindowCompositionAttribute(handle, ref data);
                Marshal.FreeHGlobal(accentPtr);

                // Update transparency on window components
                ApplyWindowTheme(); // Reset to theme background always
            } catch { }
        }
        #endregion

        private void FixArea_Click(object sender, RoutedEventArgs e)
        {
            AppBarHelper.UnregisterAppBar(MainForm.Handle);
            MessageBox.Show("Display area registration cleared. Desktop will now refresh.", "System Fix", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
    
    // P/Invoke structs for Settings (duplicated for isolation)
    internal enum AccentState_Settings { ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4 }
}
