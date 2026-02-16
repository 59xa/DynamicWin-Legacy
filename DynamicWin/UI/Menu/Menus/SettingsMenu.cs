using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus.SettingsMenuObjects;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.UI.Widgets;
using DynamicWin.UI.Widgets.Small;
using DynamicWin.Utils;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Xml.Linq;
using static DynamicWin.UI.UIElements.IslandObject;
using static System.Net.Mime.MediaTypeNames;

namespace DynamicWin.UI.Menu.Menus
{
    public class SettingsMenu : BaseMenu
    {
        private static List<IRegisterableSetting> _cachedCustomOptions;

        public SettingsMenu()
        {
            MainForm.onScrollEvent += (MouseWheelEventArgs x) =>
            {
                yScrollOffset += x.Delta * 0.50f;
            };
        }

        bool changedTheme = false;

        DWMultiSelectionButton bigMenuModeSelector;

        void SaveAndBack()
        {
            Settings.AllowBlur = allowBlur.IsChecked;
            Settings.AllowAnimation = allowAnimation.IsChecked;
            Settings.AntiAliasing = antiAliasing.IsChecked;
            Settings.ToggleHighRefreshRate = toggleHighRefreshRate.IsChecked;
            Settings.LimitRefreshRateWhenIdle = limitRefreshRateWhenIdle != null && limitRefreshRateWhenIdle.IsChecked;
            Settings.ToggleIslandShadow = toggleIslandShadow.IsChecked;
            Settings.ToggleHomeMenuShadow = toggleHomeMenuShadow.IsChecked;
            Settings.RunOnStartup = runOnStartup.IsChecked;
            Settings.AllowAutomaticUpdates = allowAutomaticUpdates.IsChecked;

            // Save the selected default big menu mode
            if (bigMenuModeSelector != null)
            {
                Settings.DefaultBigMenuMode = bigMenuModeSelector.SelectedIndex switch
                {
                    0 => HomeMenu.BigMenuMode.Widgets,
                    1 => HomeMenu.BigMenuMode.Tray,
                    2 => HomeMenu.BigMenuMode.Media,
                    _ => HomeMenu.BigMenuMode.Widgets
                };
            }

            DynamicWinMain.UpdateStartup();

            if (changedTheme)
                Theme.Instance.UpdateTheme(true);
            else
            {
                Res.HomeMenu = new HomeMenu();
                MenuManager.OpenMenu(Res.HomeMenu);
            }

            foreach (var item in _cachedCustomOptions)
            {
                item.SaveSettings();
            }

            Settings.Save();
        }

        DWCheckbox allowBlur;
        DWCheckbox allowAnimation;
        DWCheckbox antiAliasing;
        DWCheckbox runOnStartup;
        DWCheckbox allowAutomaticUpdates;
        DWCheckbox toggleIslandShadow;
        DWCheckbox toggleHomeMenuShadow;
        DWCheckbox toggleHighRefreshRate;
        DWCheckbox limitRefreshRateWhenIdle;

        DWText refreshRateDisclaimer1, refreshRateDisclaimer2, limitRefreshRateDisclaimer1, limitRefreshRateDisclaimer2;

        UIObject bottomMask;

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            var objects = base.InitializeMenu(island);

            LoadCustomOptions();

            foreach (var item in _cachedCustomOptions)
            {
                item.LoadSettings();
            }

            var generalTitle = new DWText(island, "General", new Vec2(25, 0), UIAlignment.TopLeft);
            generalTitle.Font = Res.SFProBold;
            generalTitle.Anchor.X = 0;
            objects.Add(generalTitle);

            {
                var islandModesTitle = new DWText(island, "Island Mode", new Vec2(25, 0), UIAlignment.TopLeft);
                islandModesTitle.Font = Res.SFProBold;
                islandModesTitle.Color = Theme.TextMain;
                islandModesTitle.TextSize = 15;
                islandModesTitle.Anchor.X = 0;
                objects.Add(islandModesTitle);

                var islandModes = new string[] { "Island", "Notch" };
                var islandMode = new DWMultiSelectionButton(island, islandModes, new Vec2(25, 0), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);
                islandMode.SelectedIndex = (Settings.IslandMode == IslandObject.IslandMode.Island) ? 0 : 1;
                islandMode.Anchor.X = 0;
                islandMode.onClick += (index) =>
                {
                    Settings.IslandMode = (index == 0) ? IslandObject.IslandMode.Island : IslandObject.IslandMode.Notch;
                };
                objects.Add(islandMode);
            }

            allowBlur = new DWCheckbox(island, "Toggle blur", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            allowBlur.IsChecked = Settings.AllowBlur;
            allowBlur.Anchor.X = 0;
            objects.Add(allowBlur);

            allowAnimation = new DWCheckbox(island, "Toggle animations", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            allowAnimation.IsChecked = Settings.AllowAnimation;
            allowAnimation.Anchor.X = 0;
            objects.Add(allowAnimation);

            antiAliasing = new DWCheckbox(island, "Toggle anti-aliasing", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            antiAliasing.IsChecked = Settings.AntiAliasing;
            antiAliasing.Anchor.X = 0;
            objects.Add(antiAliasing);

            refreshRateDisclaimer1 = new DWText(island, "Enables application to run at the highest refresh rate supported by your monitor.", new Vec2(25, 0), UIAlignment.TopLeft);
            refreshRateDisclaimer1.Font = Res.SFProRegular;
            refreshRateDisclaimer1.TextSize = 12;
            refreshRateDisclaimer1.Anchor.X = 0;

            refreshRateDisclaimer2 = new DWText(island, "This setting will cause performance degradation on some devices, proceed with caution.", new Vec2(25, 0), UIAlignment.TopLeft);
            refreshRateDisclaimer2.Font = Res.SFProRegular;
            refreshRateDisclaimer2.TextSize = 12;
            refreshRateDisclaimer2.Anchor.X = 0;

            toggleHighRefreshRate = new DWCheckbox(
                island,
                "Toggle high-refresh-rate mode",
                new Vec2(25, 0),
                new Vec2(25, 25),
                () =>
                {
                    bool enabled = toggleHighRefreshRate.IsChecked;

                    limitRefreshRateWhenIdle.IsEnabled = enabled;
                    limitRefreshRateDisclaimer1.IsEnabled = enabled;
                    limitRefreshRateDisclaimer2.IsEnabled = enabled;

                    if (!enabled)
                    {
                        limitRefreshRateWhenIdle.IsChecked = false;
                        Settings.LimitRefreshRateWhenIdle = false;
                    }
                },
                UIAlignment.TopLeft
            );
            toggleHighRefreshRate.IsChecked = Settings.ToggleHighRefreshRate;
            toggleHighRefreshRate.Anchor.X = 0;

            objects.Add(refreshRateDisclaimer1);
            objects.Add(refreshRateDisclaimer2);
            objects.Add(toggleHighRefreshRate);

            limitRefreshRateWhenIdle = new DWCheckbox(
                island,
                "Limit refresh rate when idle",
                new Vec2(65, 0),
                new Vec2(25, 25),
                () => { },
                UIAlignment.TopLeft
            );
            limitRefreshRateWhenIdle.IsChecked = Settings.LimitRefreshRateWhenIdle;
            limitRefreshRateWhenIdle.Anchor.X = 0;

            limitRefreshRateDisclaimer1 = new DWText(
                island,
                "Renders the application at 60 hertz when not hovered.",
                new Vec2(65, 0),
                UIAlignment.TopLeft
            )
            {
                Font = Res.SFProRegular,
                TextSize = 12,
                Anchor = new Vec2(0, 0)
            };

            limitRefreshRateDisclaimer2 = new DWText(
                island,
                "Toggle this setting to improve some of the performance usage.",
                new Vec2(65, 0),
                UIAlignment.TopLeft
            )
            {
                Font = Res.SFProRegular,
                TextSize = 12,
                Anchor = new Vec2(0, 0)
            };

            objects.Add(limitRefreshRateDisclaimer1);
            objects.Add(limitRefreshRateDisclaimer2);
            objects.Add(limitRefreshRateWhenIdle);

            bool enableRefreshRateSubSettings = toggleHighRefreshRate.IsChecked;

            limitRefreshRateWhenIdle.IsEnabled = enableRefreshRateSubSettings;
            limitRefreshRateDisclaimer1.IsEnabled = enableRefreshRateSubSettings;
            limitRefreshRateDisclaimer2.IsEnabled = enableRefreshRateSubSettings;

            toggleIslandShadow = new DWCheckbox(
                island,
                "Toggle island shadow",
                new Vec2(25, 0),
                new Vec2(25, 25),
                () =>
                {
                    bool enabled = toggleIslandShadow.IsChecked;

                    toggleHomeMenuShadow.IsEnabled = enabled;
                    if (!enabled)
                    {
                        toggleHomeMenuShadow.IsChecked = false;
                        Settings.ToggleHomeMenuShadow = false;
                    }
                },
                UIAlignment.TopLeft);
            toggleIslandShadow.IsChecked = Settings.ToggleIslandShadow;
            toggleIslandShadow.Anchor.X = 0;
            objects.Add(toggleIslandShadow);

            toggleHomeMenuShadow = new DWCheckbox(
                island,
                "Toggle home menu shadow when idle",
                new Vec2(65, 0),
                new Vec2(25, 25),
                () => { },
                UIAlignment.TopLeft);
            toggleHomeMenuShadow.IsChecked = Settings.ToggleHomeMenuShadow;
            toggleHomeMenuShadow.Anchor.X = 0;
            objects.Add(toggleHomeMenuShadow);

            bool enableIslandShadowSubSettings = toggleIslandShadow.IsChecked;

            toggleHomeMenuShadow.IsEnabled = enableIslandShadowSubSettings;

            runOnStartup = new DWCheckbox(island, "Start application on login", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            runOnStartup.IsChecked = Settings.RunOnStartup;
            runOnStartup.Anchor.X = 0;
            objects.Add(runOnStartup);

            allowAutomaticUpdates = new DWCheckbox(island, "Allow automatic updates", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            allowAutomaticUpdates.IsChecked = Settings.AllowAutomaticUpdates;
            allowAutomaticUpdates.Anchor.X = 0;
            objects.Add(allowAutomaticUpdates);

            {
                var selectedMonitorTitle = new DWText(island, "Selected Monitor", new Vec2(25, 0), UIAlignment.TopLeft);
                selectedMonitorTitle.Font = Res.SFProBold;
                selectedMonitorTitle.TextSize = 15;
                selectedMonitorTitle.Anchor.X = 0;
                objects.Add(selectedMonitorTitle);

                // Get current monitor count and names
                int monitorCount = MainForm.GetMonitorCount();
                var selectedMonitors = new string[monitorCount];

                for (int i = 0; i < monitorCount; i++)
                {
                    // Get the specific monitor to show resolution/friendly name if possible
                    var screen = System.Windows.Forms.Screen.AllScreens[i];
                    string monitorLabel = (i == 0) ? "Primary" : $"Monitor {i + 1}";
                    selectedMonitors[i] = $"{monitorLabel} ({screen.Bounds.Width}x{screen.Bounds.Height})";
                }

                var selectedMonitor = new DWMultiSelectionButton(island, selectedMonitors, new Vec2(25, 0), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);

                // Clamp the index to ensure it doesn't crash if a monitor was unplugged
                selectedMonitor.SelectedIndex = Math.Clamp(Settings.ScreenIndex, 0, monitorCount - 1);
                selectedMonitor.Anchor.X = 0;

                selectedMonitor.onClick += (index) =>
                {
                    Settings.ScreenIndex = index;
                    // Immediate preview: move the island to the selected monitor
                    if (MainForm.Instance != null)
                    {
                        MainForm.Instance.SetMonitor(index);
                    }
                };
                objects.Add(selectedMonitor);
            }

            {
                var bigMenuModeTitle = new DWText(island, "Default Big Menu Mode", new Vec2(25, 0), UIAlignment.TopLeft);
                bigMenuModeTitle.Font = Res.SFProBold;
                bigMenuModeTitle.TextSize = 15;
                bigMenuModeTitle.Anchor.X = 0;
                objects.Add(bigMenuModeTitle);
                var bigMenuModes = new string[] { "Widgets", "Tray", "Media" };
                bigMenuModeSelector = new DWMultiSelectionButton(island, bigMenuModes, new Vec2(25, 0), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);
                bigMenuModeSelector.SelectedIndex = Settings.DefaultBigMenuMode switch
                {
                    HomeMenu.BigMenuMode.Widgets => 0,
                    HomeMenu.BigMenuMode.Tray => 1,
                    HomeMenu.BigMenuMode.Media => 2,
                    _ => 0
                };
                bigMenuModeSelector.Anchor.X = 0;
                bigMenuModeSelector.onClick += (index) =>
                {
                    Settings.DefaultBigMenuMode = index switch
                    {
                        0 => HomeMenu.BigMenuMode.Widgets,
                        1 => HomeMenu.BigMenuMode.Tray,
                        2 => HomeMenu.BigMenuMode.Media,
                        _ => HomeMenu.BigMenuMode.Widgets
                    };
                };
                objects.Add(bigMenuModeSelector);
            }

            {
                var themeTitle = new DWText(island, "Themes", new Vec2(25, 0), UIAlignment.TopLeft);
                themeTitle.Font = Res.SFProBold;
                themeTitle.TextSize = 15;
                themeTitle.Anchor.X = 0;
                objects.Add(themeTitle);

                var themeOptions = new string[] { "Custom", "Dark", "Light", "Candy", "Forest Dawn", "Sunset Glow" };
                var theme = new DWMultiSelectionButton(island, themeOptions, new Vec2(25, 0), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);
                theme.SelectedIndex = Settings.Theme + 1;
                theme.Anchor.X = 0;
                theme.onClick += (index) =>
                {
                    Settings.Theme = index - 1;
                    changedTheme = true;
                };
                objects.Add(theme);
            }

            objects.Add(new DWText(island, " ", new Vec2(0, 0))
            {
                TextSize = 2
            });

            var widgetsTitle = new DWText(island, "Widgets", new Vec2(25, 0), UIAlignment.TopLeft);
            widgetsTitle.Font = Res.SFProBold;
            widgetsTitle.Color = Theme.TextMain;
            widgetsTitle.Anchor.X = 0;
            objects.Add(widgetsTitle);

            {
                var wTitle = new DWText(island, "Small widgets (right click to add/edit)", new Vec2(25, 0), UIAlignment.TopLeft);
                wTitle.Font = Res.SFProBold;
                wTitle.Color = Theme.TextMain;
                wTitle.TextSize = 15;
                wTitle.Anchor.X = 0;
                objects.Add(wTitle);

                smallWidgetAdder = new SmallWidgetAdder(island, Vec2.zero, new Vec2(IslandSize().X - 50, 35), UIAlignment.TopCenter);
                objects.Add(smallWidgetAdder);
            }

            {
                var wTitle = new DWText(island, "Big widgets (right click to add/edit)", new Vec2(25, 15), UIAlignment.TopLeft);
                wTitle.Font = Res.SFProBold;
                wTitle.Color = Theme.TextMain;
                wTitle.TextSize = 15;
                wTitle.Anchor.X = 0;
                objects.Add(wTitle);

                bigWidgetAdder = new BigWidgetAdder(island, Vec2.zero, new Vec2(IslandSize().X - 50, 35), UIAlignment.TopCenter);
                objects.Add(bigWidgetAdder);
            }

            objects.Add(new DWText(island, " ", new Vec2(25, 0), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 20
            });

            var widgetOptionsTitle = new DWText(island, "Widget Settings", new Vec2(25, 0), UIAlignment.TopLeft);
            widgetOptionsTitle.Font = Res.SFProBold;
            widgetOptionsTitle.Color = Theme.TextMain;
            widgetOptionsTitle.Anchor.X = 0;
            objects.Add(widgetOptionsTitle);

            {
                foreach (var option in _cachedCustomOptions)
                {
                    var wTitle = new DWText(island, option.SettingTitle, new Vec2(25, 0), UIAlignment.TopLeft);
                    wTitle.Font = Res.SFProBold;
                    wTitle.TextSize = 15;
                    wTitle.Anchor.X = 0;
                    objects.Add(wTitle);

                    foreach (var optionItem in option.SettingsObjects())
                    {
                        optionItem.Parent = island;

                        if (optionItem.alignment == UIAlignment.TopLeft)
                        {
                            optionItem.Position = new Vec2(25, 0);
                            optionItem.Anchor.X = 0;
                        }

                        if (optionItem is DWText)
                        {
                            ((DWText)optionItem).Color = Theme.TextMain;
                            ((DWText)optionItem).Font = Res.SFProRegular;
                            ((DWText)optionItem).TextSize = 13;
                        }
                        else if (optionItem is DWCheckbox)
                        {
                            optionItem.Size = new Vec2(25, 25);
                        }

                        objects.Add(optionItem);
                    }
                }
            }

            var releaseStreamTitle = new DWText(island, "Release Stream", new Vec2(25, 0), UIAlignment.TopLeft);
            releaseStreamTitle.Font = Res.SFProBold;
            releaseStreamTitle.Color = Theme.TextMain;
            releaseStreamTitle.Anchor.X = 0;
            objects.Add(releaseStreamTitle);

            var releaseStreamDisclaimerPt1 = new DWText(island, "Updates will be checked after you restart the application", new Vec2(25, -15), UIAlignment.TopLeft);
            releaseStreamDisclaimerPt1.Font = Res.SFProRegular;
            releaseStreamDisclaimerPt1.TextSize = 12;
            releaseStreamDisclaimerPt1.Color = Theme.TextSecond;
            releaseStreamDisclaimerPt1.Anchor.X = 0;
            objects.Add(releaseStreamDisclaimerPt1);

            var releaseStreamDisclaimerPt2 = new DWText(island, "or by pressing the 'Check for updates now' button.", new Vec2(25, -30), UIAlignment.TopLeft);
            releaseStreamDisclaimerPt2.Font = Res.SFProRegular;
            releaseStreamDisclaimerPt2.TextSize = 12;
            releaseStreamDisclaimerPt2.Color = Theme.TextSecond;
            releaseStreamDisclaimerPt2.Anchor.X = 0;
            objects.Add(releaseStreamDisclaimerPt2);
            {
                var releaseStreams = new string[] { "Release", "Canary" };
                var releaseStream = new DWMultiSelectionButton(island, releaseStreams, new Vec2(25, -25), new Vec2(IslandSize().X - 50, 30), UIAlignment.TopLeft);
                releaseStream.SelectedIndex = Settings.ReleaseStream;
                releaseStream.Anchor.X = 0;
                releaseStream.onClick += (index) =>
                {
                    Settings.ReleaseStream = index;
                    Settings.Save();
                };
                objects.Add(releaseStream);
            }

            // Trigger update logic immediately upon pressing the update button
            var checkForUpdateBtn = new DWTextButton(island, "Check for updates now", new Vec2(25, -25), new Vec2(IslandSize().X - 360, 30), () =>
            {
                SaveManager.Add("settings.ReleaseStream", Settings.ReleaseStream);

                // Show overlay immediately on UI thread
                MenuManager.OpenOverlayMenu(new UpdaterOverlay(), 0f);

                // Perform check in background
                _ = Task.Run(async () =>
                {
                    var updater = new Updater();
                    AppVersion? update = null;
                    try { update = await updater.CheckForUpdate(); } catch { update = null; }

                    // Back to UI thread to update menus
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        MenuManager.CloseOverlay();
                        MenuManager.Instance?.UnlockMenu();

                        if (update == null)
                        {
                            MenuManager.OpenMenu(Res.HomeMenu);
                        }
                        else
                        {
                            MenuManager.OpenMenu(new UpdaterMenu(update));
                        }
                    });
                });
            }, UIAlignment.TopLeft);
            checkForUpdateBtn.Anchor.X = 0;
            objects.Add(checkForUpdateBtn);

            objects.Add(new DWText(island, $"Application version: {DynamicWinMain.Version} ({DynamicWinMain.ReleaseStream.ToFriendlyString()})", new Vec2(25, -15), UIAlignment.TopLeft)
            {
                Color = Theme.TextMain,
                Anchor = new Vec2(0, 0),
                TextSize = 15,
                Font = Res.SFProBold
            });

            objects.Add(new DWText(island, $"Software architecture: {DynamicWinMain.SoftwareArchitecture.ToString().ToLower()}", new Vec2(25, -25), UIAlignment.TopLeft)
            {
                Color = Theme.TextMain,
                Anchor = new Vec2(0, 0),
                TextSize = 13,
                Font = Res.SFProBold
            });

            objects.Add(new DWText(island, "Maintained and developed by 59xa", new Vec2(25, -25), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13,
            });

            objects.Add(new DWText(island, "Created by Florian Butz", new Vec2(25, -25), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13
            });

            objects.Add(new DWText(island, "Licenced under CC BY-SA 4.0", new Vec2(25, -25), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13
            });

            var backBtn = new DWTextButton(island, "Save changes", new Vec2(0, -45), new Vec2(250, 40), () => { SaveAndBack(); }, UIAlignment.BottomCenter)
            {
                roundRadius = 25
            };
            backBtn.Text.Font = Res.SFProBold;

            bottomMask = new BottomMask(island, backBtn)
            {
                padding = 20,
                alpha = 0.7f,
                roundRadius = 50,
                shadowStrength = 10f,
                shadowColor = Theme.IslandBackground,
                Color = Theme.IslandBackground
            };

            objects.Add(bottomMask);
            objects.Add(backBtn);

            return objects;
        }

        SmallWidgetAdder smallWidgetAdder;
        BigWidgetAdder bigWidgetAdder;

        float yScrollOffset = 0f;
        float ySmoothScroll = 0f;

        public override void Update()
        {
            base.Update();

            ySmoothScroll = Mathf.Lerp(ySmoothScroll,
                yScrollOffset, 10f * RendererMain.Instance.DeltaTime);

            bottomMask.blurAmount = 15;

            var yScrollLim = 0f;
            var yPos = 35f;
            var spacing = 15f;

            for (int i = 0; i < UiObjects.Count - 2; i++)
            {
                var uiObject = UiObjects[i];
                if (!uiObject.IsEnabled) continue;

                uiObject.LocalPosition.Y = yPos + ySmoothScroll;
                yPos += uiObject.Size.Y + spacing;

                if (yPos > IslandSize().Y - 50) yScrollLim += uiObject.Size.Y + spacing;
            }

            yScrollOffset = Mathf.Lerp(yScrollOffset,
                Mathf.Clamp(yScrollOffset, -yScrollLim, 0f), 15f * RendererMain.Instance.DeltaTime);
        }

        public override Vec2 IslandSize()
        {
            var vec = new Vec2(525, 425);

            if (smallWidgetAdder != null)
            {
                vec.X = Math.Max(vec.X, smallWidgetAdder.Size.X + 50);
            }

            return vec;
        }

        public override Vec2 IslandSizeBig()
        {
            return IslandSize() + 5;
        }

        public static List<IRegisterableSetting> LoadCustomOptions()
        {
            if (_cachedCustomOptions != null) return _cachedCustomOptions;

            _cachedCustomOptions = new List<IRegisterableSetting>();

            var registerableSettings = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(s => s.GetTypes())
            .Where(p => typeof(IRegisterableSetting).IsAssignableFrom(p) && p.IsClass);

            foreach (var option in registerableSettings)
            {
                var optionInstance = (IRegisterableSetting)Activator.CreateInstance(option);
                _cachedCustomOptions.Add(optionInstance);
            }

            // Loading in custom DLLs

            var dirPath = Path.Combine(SaveManager.SavePath, "Extensions");

            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
            else
            {
                foreach (var file in Directory.GetFiles(dirPath))
                {
                    if (Path.GetExtension(file).ToLower().Equals(".dll"))
                    {
                        System.Diagnostics.Debug.WriteLine(file);
                        var DLL = Assembly.LoadFile(Path.Combine(dirPath, file));

                        var dllRegisterableSettings = DLL.GetTypes()
                            .Where(p => typeof(IRegisterableSetting).IsAssignableFrom(p) && p.IsClass);

                        foreach (var option in dllRegisterableSettings)
                        {
                            var optionInstance = (IRegisterableSetting)Activator.CreateInstance(option);
                            _cachedCustomOptions.Add(optionInstance);
                        }
                    }
                }
            }

            return _cachedCustomOptions;
        }

        // Border should only be rendered if on island mode instead of notch
        public override Col IslandBorderColor()
        {
            IslandMode mode = Settings.IslandMode; // Reads either Island or Notch as value
            if (mode == IslandMode.Island) return new Col(0.5f, 0.5f, 0.5f);
            else return new Col(0, 0, 0, 0); // Render transparent if island mode is Notch
        }
    }
}