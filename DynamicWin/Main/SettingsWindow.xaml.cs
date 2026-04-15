using DynamicWin.Main;
using DynamicWin.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace DynamicWin.Main
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            chkAlwaysTopmost.IsChecked = Settings.AlwaysTopmost;
            chkAllowBlur.IsChecked = Settings.AllowBlur;
            chkAllowAnimation.IsChecked = Settings.AllowAnimation;
            chkAntiAliasing.IsChecked = Settings.AntiAliasing;
            chkToggleHighRefreshRate.IsChecked = Settings.ToggleHighRefreshRate;
            chkLimitRefreshRateWhenIdle.IsChecked = Settings.LimitRefreshRateWhenIdle;
            chkToggleIslandShadow.IsChecked = Settings.ToggleIslandShadow;
            chkToggleHomeMenuShadow.IsChecked = Settings.ToggleHomeMenuShadow;
            chkShortenWindowsWorkingArea.IsChecked = Settings.ShortenWindowsWorkingArea;
            chkRunOnStartup.IsChecked = Settings.RunOnStartup;
            chkAllowAutomaticUpdates.IsChecked = Settings.AllowAutomaticUpdates;

            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string name = i == 0 ? "Primary" : $"Monitor {i + 1}";
                cmbMonitor.Items.Add($"{name} ({screen.Bounds.Width}x{screen.Bounds.Height})");
            }
            cmbMonitor.SelectedIndex = Settings.ScreenIndex;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Settings.AlwaysTopmost = chkAlwaysTopmost.IsChecked == true;
            Settings.AllowBlur = chkAllowBlur.IsChecked == true;
            Settings.AllowAnimation = chkAllowAnimation.IsChecked == true;
            Settings.AntiAliasing = chkAntiAliasing.IsChecked == true;
            Settings.ToggleHighRefreshRate = chkToggleHighRefreshRate.IsChecked == true;
            Settings.LimitRefreshRateWhenIdle = chkLimitRefreshRateWhenIdle.IsChecked == true;
            Settings.ToggleIslandShadow = chkToggleIslandShadow.IsChecked == true;
            Settings.ToggleHomeMenuShadow = chkToggleHomeMenuShadow.IsChecked == true;
            Settings.ShortenWindowsWorkingArea = chkShortenWindowsWorkingArea.IsChecked == true;
            Settings.RunOnStartup = chkRunOnStartup.IsChecked == true;
            Settings.AllowAutomaticUpdates = chkAllowAutomaticUpdates.IsChecked == true;

            if (chkShortenWindowsWorkingArea.IsChecked == true)
            {
                AppBarManager.Apply(MainForm.Instance, Settings.ScreenIndex, true);
            }
            else
            {
                AppBarManager.Apply(MainForm.Instance, Settings.ScreenIndex, false);
            }

            SaveManager.SaveAll();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}