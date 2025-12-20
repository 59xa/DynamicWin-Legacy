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

        public SettingsMenu()
        {
            MainForm.onScrollEvent += (MouseWheelEventArgs x) => 
            {
                yScrollOffset += x.Delta * 0.50f;
            };
        }

        bool changedTheme = false;

        void SaveAndBack()
        {
            Settings.AllowBlur = allowBlur.IsChecked;
            Settings.AllowAnimation = allowAnimation.IsChecked;
            Settings.AntiAliasing = antiAliasing.IsChecked;
            Settings.RunOnStartup = runOnStartup.IsChecked;

            DynamicWinMain.UpdateStartup();

            if (changedTheme)
                Theme.Instance.UpdateTheme(true);
            else
            {
                Res.HomeMenu = new HomeMenu();
                MenuManager.OpenMenu(Res.HomeMenu);
            }

            foreach (var item in customOptions)
            {
                item.SaveSettings();
            }

            Settings.Save();
        }

        DWCheckbox allowBlur;
        DWCheckbox allowAnimation;
        DWCheckbox antiAliasing;
        DWCheckbox runOnStartup;

        UIObject bottomMask;

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            var objects = base.InitializeMenu(island);

            SettingsMenu.LoadCustomOptions();

            foreach (var item in customOptions)
            {
                item.LoadSettings();
            }

            var generalTitle = new DWText(island, "General", new Vec2(25, 0), UIAlignment.TopLeft);
            generalTitle.Font = Res.SatoshiBold;
            generalTitle.Anchor.X = 0;
            objects.Add(generalTitle);

            {
                var islandModesTitle = new DWText(island, "Island Mode", new Vec2(25, 0), UIAlignment.TopLeft);
                islandModesTitle.Font = Res.SatoshiBold;
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

            runOnStartup = new DWCheckbox(island, "Start application on login", new Vec2(25, 0), new Vec2(25, 25), () => { }, UIAlignment.TopLeft);
            runOnStartup.IsChecked = Settings.RunOnStartup;
            runOnStartup.Anchor.X = 0;
            objects.Add(runOnStartup);


            {
                var selectedMonitorTitle = new DWText(island, "Selected Monitor", new Vec2(25, 0), UIAlignment.TopLeft);
                selectedMonitorTitle.Font = Res.SatoshiBold;
                selectedMonitorTitle.TextSize = 15;
                selectedMonitorTitle.Anchor.X = 0;
                objects.Add(selectedMonitorTitle);

                var selectedMonitors = new string[MainForm.GetMonitorCount()];
                for(int i = 0; i < selectedMonitors.Length; i++)
                {
                    if (i == 0) selectedMonitors[i] = "Primary";
                    else selectedMonitors[i] = "Monitor " + i;
                }

                var selectedMonitor = new DWMultiSelectionButton(island, selectedMonitors, new Vec2(25, 0), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);
                selectedMonitor.SelectedIndex = Math.Clamp(Settings.ScreenIndex, 0, MainForm.GetMonitorCount() - 1);
                selectedMonitor.Anchor.X = 0;
                selectedMonitor.onClick += (index) =>
                {
                    Settings.ScreenIndex = index;
                    MainForm.Instance.SetMonitor(Settings.ScreenIndex);
                };
                objects.Add(selectedMonitor);
            }

            {
                var themeTitle = new DWText(island, "Themes", new Vec2(25, 0), UIAlignment.TopLeft);
                themeTitle.Font = Res.SatoshiBold;
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
            widgetsTitle.Font = Res.SatoshiBold;
            widgetsTitle.Color = Theme.TextMain;
            widgetsTitle.Anchor.X = 0;
            objects.Add(widgetsTitle);

            {
                var wTitle = new DWText(island, "Small widgets (right click to add/edit)", new Vec2(25, 0), UIAlignment.TopLeft);
                wTitle.Font = Res.SatoshiBold;
                wTitle.Color = Theme.TextMain;
                wTitle.TextSize = 15;
                wTitle.Anchor.X = 0;
                objects.Add(wTitle);

                smallWidgetAdder = new SmallWidgetAdder(island, Vec2.zero, new Vec2(IslandSize().X - 50, 35), UIAlignment.TopCenter);
                objects.Add(smallWidgetAdder);
            }

            {
                var wTitle = new DWText(island, "Big widgets (right click to add/edit)", new Vec2(25, 15), UIAlignment.TopLeft);
                wTitle.Font = Res.SatoshiBold;
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
            widgetOptionsTitle.Font = Res.SatoshiBold;
            widgetOptionsTitle.Color = Theme.TextMain;
            widgetOptionsTitle.Anchor.X = 0;
            objects.Add(widgetOptionsTitle);

            {
                foreach(var option in customOptions)
                {
                    var wTitle = new DWText(island, option.SettingTitle, new Vec2(25, 0), UIAlignment.TopLeft);
                    wTitle.Font = Res.SatoshiBold;
                    wTitle.TextSize = 15;
                    wTitle.Anchor.X = 0;
                    objects.Add(wTitle);

                    foreach(var optionItem in option.SettingsObjects())
                    {
                        optionItem.Parent = island;

                        if(optionItem.alignment == UIAlignment.TopLeft)
                        {
                            optionItem.Position = new Vec2(25, 0);
                            optionItem.Anchor.X = 0;
                        }

                        if (optionItem is DWText)
                        {
                            ((DWText)optionItem).Color = Theme.TextMain;
                            ((DWText)optionItem).Font = Res.SatoshiRegular;
                            ((DWText)optionItem).TextSize = 13;
                        }else if(optionItem is DWCheckbox)
                        {
                            optionItem.Size = new Vec2(25, 25);
                        }

                        objects.Add(optionItem);
                    }
                }
            }

            var releaseStreamTitle = new DWText(island, "Release Stream", new Vec2(25, 0), UIAlignment.TopLeft);
            releaseStreamTitle.Font = Res.SatoshiBold;
            releaseStreamTitle.TextSize = 15;
            releaseStreamTitle.Color = Theme.TextMain;
            releaseStreamTitle.Anchor.X = 0;
            objects.Add(releaseStreamTitle);

            var releaseStreamDisclaimer = new DWText(island, "Updates will be checked after you restart the application.", new Vec2(25, -15), UIAlignment.TopLeft);
            releaseStreamDisclaimer.Font = Res.SatoshiRegular;
            releaseStreamDisclaimer.TextSize = 12;
            releaseStreamDisclaimer.Color = Theme.TextSecond;
            releaseStreamDisclaimer.Anchor.X = 0;
            objects.Add(releaseStreamDisclaimer);
            {
                var releaseStreams = new string[] { "Release", "Canary" };
                var releaseStream = new DWMultiSelectionButton(island, releaseStreams, new Vec2(25, -15), new Vec2(IslandSize().X - 50, 25), UIAlignment.TopLeft);
                releaseStream.SelectedIndex = Settings.ReleaseStream;
                releaseStream.Anchor.X = 0;
                releaseStream.onClick += (index) =>
                {
                    Settings.ReleaseStream = index;
                };
                objects.Add(releaseStream);
            }

            objects.Add(new DWText(island, $"Application version: {DynamicWinMain.Version} ({DynamicWinMain.ReleaseStream})", new Vec2(25, 0), UIAlignment.TopLeft)
            {
                Color = Theme.TextMain,
                Anchor = new Vec2(0, 0),
                TextSize = 15,
                Font = Res.SatoshiBold
            });

            objects.Add(new DWText(island, "Maintained and developed by 59xa", new Vec2(25, 0), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13,
            });

            objects.Add(new DWText(island, "Created by Florian Butz", new Vec2(25, 0), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13
            });

            objects.Add(new DWText(island, "Licenced under CC BY-SA 4.0", new Vec2(25, 0), UIAlignment.TopLeft)
            {
                Color = Theme.TextThird,
                Anchor = new Vec2(0, 0.5f),
                TextSize = 13
            });

            var backBtn = new DWTextButton(island, "Save changes", new Vec2(0, -45), new Vec2(250, 40), () => { SaveAndBack(); }, UIAlignment.BottomCenter)
            {
                roundRadius = 25
            };
            backBtn.Text.Font = Res.SatoshiBold;

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

            for(int i = 0; i < UiObjects.Count - 2; i++)
            {
                var uiObject = UiObjects[i];
                if (!uiObject.IsEnabled) continue;

                uiObject.LocalPosition.Y = yPos + ySmoothScroll;
                yPos += uiObject.Size.Y + spacing;

                if (yPos > IslandSize().Y - 45) yScrollLim += uiObject.Size.Y + spacing;
            }

            yScrollOffset = Mathf.Lerp(yScrollOffset,
                Mathf.Clamp(yScrollOffset, -yScrollLim, 0f), 15f * RendererMain.Instance.DeltaTime);
        }

        public override Vec2 IslandSize()
        {
            var vec = new Vec2(525, 425);

            if(smallWidgetAdder != null)
            {
                vec.X = Math.Max(vec.X, smallWidgetAdder.Size.X + 50);
            }

            return vec;
        }

        public override Vec2 IslandSizeBig()
        {
            return IslandSize() + 5;
        }

        static List<IRegisterableSetting> customOptions;

        public static List<IRegisterableSetting> LoadCustomOptions()
        {
            customOptions = new List<IRegisterableSetting>();

            var registerableSettings = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(s => s.GetTypes())
            .Where(p => typeof(IRegisterableSetting).IsAssignableFrom(p) && p.IsClass);

            foreach (var option in registerableSettings)
            {
                var optionInstance = (IRegisterableSetting)Activator.CreateInstance(option);
                customOptions.Add(optionInstance);
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
                        var DLL = new Assembly[] { Assembly.LoadFile(Path.Combine(dirPath, file)) };

                        var dllRegisterableSettings = DLL
                            .SelectMany(s => s.GetTypes())
                            .Where(p => typeof(IRegisterableSetting).IsAssignableFrom(p) && p.IsClass);

                        foreach (var option in dllRegisterableSettings)
                        {
                            var optionInstance = (IRegisterableSetting)Activator.CreateInstance(option);
                            customOptions.Add(optionInstance);
                        }
                    }
                }
            }

            return customOptions;
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
