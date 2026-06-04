using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus.SettingsMenuObjects;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.UI.Widgets;
using DynamicWin.Utils;
using SkiaSharp;
using System.IO;
using System.Reflection;
using System.Windows.Input;

namespace DynamicWin.UI.Menu.Menus
{
    public class SettingsMenu : BaseMenu
    {
        private const float ContentLeft = 25f;
        private const float ContentTop = 35f;
        private const float ContentSpacing = 10f;
        private const float TitleBottomTuck = -12f;
        private const float SectionTitleBottomTuck = -6f;
        private const float BottomReserve = 85f;
        private const float ScrollSpeed = 0.50f;
        private const float ScrollSmoothSpeed = 10f;
        private const float ScrollClampSpeed = 15f;
        private const float ScrollEpsilon = 0.05f;

        private static readonly Vec2 CheckboxSize = new Vec2(25, 32);
        private static List<IRegisterableSetting>? cachedCustomOptions;

        private readonly Action<MouseWheelEventArgs> scrollHandler;

        private List<UIObject> contentObjects = new List<UIObject>();
        private readonly Dictionary<UIObject, float> layoutBottomAdjustments = new Dictionary<UIObject, float>();
        private SmallWidgetAdder? smallWidgetAdder;
        private BigWidgetAdder? bigWidgetAdder;
        private UIObject? bottomMask;

        private SettingsCheckbox allowBlur = null!;
        private SettingsCheckbox allowAnimation = null!;
        private SettingsCheckbox antiAliasing = null!;
        private SettingsCheckbox runOnStartup = null!;
        private SettingsCheckbox allowAutomaticUpdates = null!;
        private SettingsCheckbox alwaysTopmost = null!;
        private SettingsCheckbox reduceWorkingArea = null!;
        private SettingsCheckbox toggleIslandShadow = null!;
        private SettingsCheckbox toggleHomeMenuShadow = null!;
        private SettingsCheckbox toggleHighRefreshRate = null!;
        private SettingsCheckbox limitRefreshRateWhenIdle = null!;

        private DWText limitRefreshRateDisclaimer1 = null!;
        private DWText limitRefreshRateDisclaimer2 = null!;
        private DWMultiSelectionButton? bigMenuModeSelector;

        private bool changedTheme;
        private bool layoutDirty = true;
        private float yScrollOffset;
        private float ySmoothScroll;
        private float cachedScrollLimit;
        private float lastContentSignature = float.NaN;
        private float lastIslandWidth = float.NaN;
        private float lastIslandHeight = float.NaN;

        public SettingsMenu()
        {
            scrollHandler = OnScroll;
            MainForm.onScrollEvent += scrollHandler;
        }

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            contentObjects = new List<UIObject>();
            layoutBottomAdjustments.Clear();
            layoutDirty = true;
            changedTheme = false;
            yScrollOffset = 0f;
            ySmoothScroll = 0f;
            cachedScrollLimit = 0f;
            lastContentSignature = float.NaN;

            var objects = base.InitializeMenu(island);
            var customOptions = LoadCustomOptions();

            foreach (var option in customOptions)
                option.LoadSettings();

            AddGap(objects, island, 10f);
            AddTitle(objects, island, "General");
            AddSectionTitle(objects, island, "Island Mode");
            var islandMode = AddSelector(objects, island, new[] { "Island", "Notch" });
            islandMode.SelectedIndex = Settings.IslandMode == IslandObject.IslandMode.Island ? 0 : 1;
            islandMode.onClick += index =>
            {
                Settings.IslandMode = index == 0
                    ? IslandObject.IslandMode.Island
                    : IslandObject.IslandMode.Notch;
            };

            AddBodyText(objects, island, "Renders the interface to its minimum height and width possible, also improves performance.");
            AddBodyText(objects, island, "Disabling this setting may prevent the interface from being placed correctly at the top.");

            alwaysTopmost = AddCheckbox(objects, island, "Keep interface always topmost", Settings.AlwaysTopmost);
            AddBodyText(objects, island, "Prevents the interface from overlapping on top of other windows.");

            reduceWorkingArea = AddCheckbox(objects, island, "Reduce working area", Settings.ReduceWorkingArea);
            allowBlur = AddCheckbox(objects, island, "Toggle blur", Settings.AllowBlur);
            allowAnimation = AddCheckbox(objects, island, "Toggle animations", Settings.AllowAnimation);
            antiAliasing = AddCheckbox(objects, island, "Toggle anti-aliasing", Settings.AntiAliasing);

            AddBodyText(objects, island, "Enables application to run at the highest refresh rate supported by your monitor.");
            AddBodyText(objects, island, "This setting will cause performance degradation on some devices, proceed with caution.");
            toggleHighRefreshRate = AddCheckbox(objects, island, "Toggle high-refresh-rate mode", Settings.ToggleHighRefreshRate, () =>
            {
                bool enabled = toggleHighRefreshRate.IsChecked;
                SetRefreshRateSubSettingsEnabled(enabled, immediate: false);

                if (!enabled)
                    Settings.LimitRefreshRateWhenIdle = false;
            });

            limitRefreshRateDisclaimer1 = AddBodyText(objects, island, "Renders the application at 60 hertz when not hovered.", left: 65f);
            limitRefreshRateDisclaimer2 = AddBodyText(objects, island, "Toggle this setting to improve some of the performance usage.", left: 65f);
            limitRefreshRateWhenIdle = AddCheckbox(objects, island, "Limit refresh rate when idle", Settings.LimitRefreshRateWhenIdle, left: 65f);
            SetRefreshRateSubSettingsEnabled(toggleHighRefreshRate.IsChecked, immediate: true);

            toggleIslandShadow = AddCheckbox(objects, island, "Toggle island shadow", Settings.ToggleIslandShadow, () =>
            {
                bool enabled = toggleIslandShadow.IsChecked;
                SetHomeMenuShadowEnabled(enabled, immediate: false);

                if (!enabled)
                    Settings.ToggleHomeMenuShadow = false;
            });

            toggleHomeMenuShadow = AddCheckbox(objects, island, "Toggle home menu shadow when idle", Settings.ToggleHomeMenuShadow, left: 65f);
            SetHomeMenuShadowEnabled(toggleIslandShadow.IsChecked, immediate: true);

            runOnStartup = AddCheckbox(objects, island, "Start application on login", Settings.RunOnStartup);
            allowAutomaticUpdates = AddCheckbox(objects, island, "Allow automatic updates", Settings.AllowAutomaticUpdates);

            AddMonitorSelector(objects, island);
            AddDefaultBigMenuSelector(objects, island);
            AddThemeSelector(objects, island);

            AddGap(objects, island, 18f);
            AddTitle(objects, island, "Widgets");

            AddSectionTitle(objects, island, "Small widgets (right click to add/edit)");
            smallWidgetAdder = new SmallWidgetAdder(island, Vec2.zero, new Vec2(IslandSize().X - 50, 35), UIAlignment.TopCenter);
            AddContent(objects, smallWidgetAdder);

            AddSectionTitle(objects, island, "Big widgets (right click to add/edit)", topPadding: 15f);
            bigWidgetAdder = new BigWidgetAdder(island, Vec2.zero, new Vec2(IslandSize().X - 50, 35), UIAlignment.TopCenter);
            AddContent(objects, bigWidgetAdder);

            AddGap(objects, island, 18f);
            AddTitle(objects, island, "Widget Settings");
            AddCustomOptions(objects, island, customOptions);

            AddReleaseStream(objects, island);
            AddVersionInfo(objects, island);

            objects.Add(new SettingsRenderDemand(island, NeedsRealtimeLayout));
            AddSaveButton(objects, island);

            return objects;
        }

        public override void Update()
        {
            base.Update();

            if (bottomMask != null)
                bottomMask.blurAmount = 15;

            float deltaTime = RendererMain.Instance?.DeltaTime ?? 1f / 60f;

            float signature = BuildContentSignature();
            if (float.IsNaN(lastContentSignature) || Math.Abs(signature - lastContentSignature) > 0.01f)
            {
                lastContentSignature = signature;
                layoutDirty = true;
            }

            var islandSize = IslandSize();
            if (float.IsNaN(lastIslandWidth)
                || Math.Abs(lastIslandWidth - islandSize.X) > 0.05f
                || Math.Abs(lastIslandHeight - islandSize.Y) > 0.05f)
            {
                lastIslandWidth = islandSize.X;
                lastIslandHeight = islandSize.Y;
                layoutDirty = true;
            }

            float clampedTarget = Mathf.Clamp(yScrollOffset, -cachedScrollLimit, 0f);
            yScrollOffset = Smooth(yScrollOffset, clampedTarget, ScrollClampSpeed, deltaTime, ScrollEpsilon);

            float previousSmooth = ySmoothScroll;
            ySmoothScroll = Smooth(ySmoothScroll, yScrollOffset, ScrollSmoothSpeed, deltaTime, ScrollEpsilon);
            if (Math.Abs(previousSmooth - ySmoothScroll) > 0.001f)
                layoutDirty = true;

            if (layoutDirty)
                LayoutContent();
        }

        public override void OnDispose()
        {
            MainForm.onScrollEvent -= scrollHandler;
            base.OnDispose();
        }

        public override Vec2 IslandSize()
        {
            var vec = new Vec2(525, 425);

            if (smallWidgetAdder != null)
                vec.X = Math.Max(vec.X, smallWidgetAdder.Size.X + 50);

            return vec;
        }

        public override Vec2 IslandSizeBig()
        {
            return IslandSize() + 5;
        }

        public override Col IslandBorderColor()
        {
            return Settings.IslandMode == IslandObject.IslandMode.Island
                ? new Col(0.5f, 0.5f, 0.5f)
                : Col.Transparent;
        }

        public static List<IRegisterableSetting> LoadCustomOptions()
        {
            if (cachedCustomOptions != null)
                return cachedCustomOptions;

            cachedCustomOptions = new List<IRegisterableSetting>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                AddRegisterableSettingsFromAssembly(assembly, cachedCustomOptions);

            string dirPath = Path.Combine(SaveManager.SavePath, "Extensions");
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
                return cachedCustomOptions;
            }

            foreach (string file in Directory.GetFiles(dirPath, "*.dll"))
            {
                try
                {
                    AddRegisterableSettingsFromAssembly(Assembly.LoadFile(file), cachedCustomOptions);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SettingsMenu: failed to load extension settings from {file}: {ex.Message}");
                }
            }

            return cachedCustomOptions;
        }

        private void OnScroll(MouseWheelEventArgs e)
        {
            if (!ReferenceEquals(MenuManager.Instance?.ActiveMenu, this))
                return;

            yScrollOffset += e.Delta * ScrollSpeed;
            yScrollOffset = Mathf.Clamp(yScrollOffset, -cachedScrollLimit, 0f);
            layoutDirty = true;
        }

        private void SaveAndBack()
        {
            Settings.AllowBlur = allowBlur.IsChecked;
            Settings.AllowAnimation = allowAnimation.IsChecked;
            Settings.AntiAliasing = antiAliasing.IsChecked;
            Settings.ToggleHighRefreshRate = toggleHighRefreshRate.IsChecked;
            Settings.LimitRefreshRateWhenIdle = toggleHighRefreshRate.IsChecked && limitRefreshRateWhenIdle.IsChecked;
            Settings.ToggleIslandShadow = toggleIslandShadow.IsChecked;
            Settings.ToggleHomeMenuShadow = toggleIslandShadow.IsChecked && toggleHomeMenuShadow.IsChecked;
            Settings.RunOnStartup = runOnStartup.IsChecked;
            Settings.AllowAutomaticUpdates = allowAutomaticUpdates.IsChecked;
            Settings.AlwaysTopmost = alwaysTopmost.IsChecked;
            Settings.ReduceWorkingArea = reduceWorkingArea.IsChecked;

            if (bigMenuModeSelector != null)
            {
                Settings.DefaultBigMenuMode = bigMenuModeSelector.SelectedIndex switch
                {
                    1 => HomeMenu.BigMenuMode.Tray,
                    2 => HomeMenu.BigMenuMode.Media,
                    _ => HomeMenu.BigMenuMode.Widgets
                };
            }

            foreach (var item in LoadCustomOptions())
                item.SaveSettings();

            DynamicWinMain.UpdateStartup();

            if (changedTheme)
            {
                Theme.Instance.UpdateTheme(true);
            }
            else
            {
                Res.HomeMenu = new HomeMenu();
                MenuManager.OpenMenu(Res.HomeMenu);
            }

            Settings.Save();
        }

        private void AddMonitorSelector(List<UIObject> objects, IslandObject island)
        {
            AddSectionTitle(objects, island, "Selected Monitor");

            int monitorCount = Math.Max(1, MainForm.GetMonitorCount());
            var selectedMonitors = new string[monitorCount];

            for (int i = 0; i < monitorCount; i++)
            {
                var screen = System.Windows.Forms.Screen.AllScreens[Math.Min(i, System.Windows.Forms.Screen.AllScreens.Length - 1)];
                string monitorLabel = i == 0 ? "Primary" : $"Monitor {i + 1}";
                selectedMonitors[i] = $"{monitorLabel} ({screen.Bounds.Width}x{screen.Bounds.Height})";
            }

            var selectedMonitor = AddSelector(objects, island, selectedMonitors);
            selectedMonitor.SelectedIndex = Math.Clamp(Settings.ScreenIndex, 0, monitorCount - 1);
            selectedMonitor.onClick += index =>
            {
                Settings.ScreenIndex = index;
                MainForm.Instance?.SetMonitor(index);
            };
        }

        private void AddDefaultBigMenuSelector(List<UIObject> objects, IslandObject island)
        {
            AddSectionTitle(objects, island, "Default Big Menu Mode");

            bigMenuModeSelector = AddSelector(objects, island, new[] { "Widgets", "Tray", "Media" });
            bigMenuModeSelector.SelectedIndex = Settings.DefaultBigMenuMode switch
            {
                HomeMenu.BigMenuMode.Tray => 1,
                HomeMenu.BigMenuMode.Media => 2,
                _ => 0
            };
            bigMenuModeSelector.onClick += index =>
            {
                Settings.DefaultBigMenuMode = index switch
                {
                    1 => HomeMenu.BigMenuMode.Tray,
                    2 => HomeMenu.BigMenuMode.Media,
                    _ => HomeMenu.BigMenuMode.Widgets
                };
            };
        }

        private void AddThemeSelector(List<UIObject> objects, IslandObject island)
        {
            AddSectionTitle(objects, island, "Themes");

            var theme = AddSelector(objects, island, new[] { "Custom", "Dark", "Light", "Candy", "Forest Dawn", "Sunset Glow" });
            theme.SelectedIndex = Settings.Theme + 1;
            theme.onClick += index =>
            {
                Settings.Theme = index - 1;
                changedTheme = true;
            };
        }

        private void AddCustomOptions(List<UIObject> objects, IslandObject island, List<IRegisterableSetting> customOptions)
        {
            foreach (var option in customOptions)
            {
                AddSectionTitle(objects, island, option.SettingTitle);

                foreach (var optionItem in option.SettingsObjects())
                {
                    optionItem.Parent = island;

                    if (optionItem.alignment == UIAlignment.TopLeft)
                    {
                        optionItem.Position = new Vec2(ContentLeft, 0);
                        optionItem.Anchor.X = 0;
                    }

                    if (optionItem is DWText text)
                    {
                        text.Color = Theme.TextSecond;
                        text.Font = Res.SFProRegular;
                        text.TextSize = 12;
                        text.Size = text.GetBoundsForString(text.Text);
                    }
                    else if (optionItem is DWCheckbox)
                    {
                        optionItem.Size = CheckboxSize;
                    }

                    AddContent(objects, optionItem);
                }
            }
        }

        private void AddReleaseStream(List<UIObject> objects, IslandObject island)
        {
            AddSectionTitle(objects, island, "Release Stream");
            AddBodyText(objects, island, "Updates will be checked after you restart the application", topPadding: -15f);
            AddBodyText(objects, island, "or by pressing the 'Check for updates now' button.", topPadding: -15f);

            var releaseStream = AddSelector(objects, island, new[] { "Release", "Canary" });
            releaseStream.SelectedIndex = Settings.ReleaseStream;
            releaseStream.onClick += index =>
            {
                Settings.ReleaseStream = index;
                Settings.Save();
            };

            var checkForUpdateBtn = new DWTextButton(
                island,
                "Check for updates now",
                new Vec2(ContentLeft, 0),
                new Vec2(IslandSize().X - 360, 32),
                CheckForUpdate,
                UIAlignment.TopLeft);
            checkForUpdateBtn.Anchor.X = 0;
            AddContent(objects, checkForUpdateBtn);
        }

        private void AddVersionInfo(List<UIObject> objects, IslandObject island)
        {
            AddText(objects, island, $"Application version: {DynamicWinMain.Version} ({DynamicWinMain.ReleaseStream.ToFriendlyString()})", 15, Res.SFProBold, Theme.TextMain);
            AddText(objects, island, $"Software architecture: {DynamicWinMain.ProcessArchitecture.ToString().ToLower()}", 13, Res.SFProBold, Theme.TextMain, topPadding: -10f);
            AddText(objects, island, "Maintained and developed by 59xa", 13, Res.SFProRegular, Theme.TextThird, topPadding: -10f);
            AddText(objects, island, "Created by Florian Butz", 13, Res.SFProRegular, Theme.TextThird, topPadding: -10f);
            AddText(objects, island, "Licenced under CC BY-SA 4.0", 13, Res.SFProRegular, Theme.TextThird, topPadding: -10f);
        }

        private void AddSaveButton(List<UIObject> objects, IslandObject island)
        {
            var backBtn = new DWTextButton(island, "Save changes", new Vec2(0, -45), new Vec2(250, 40), SaveAndBack, UIAlignment.BottomCenter)
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
        }

        private void CheckForUpdate()
        {
            SaveManager.Add("settings.ReleaseStream", Settings.ReleaseStream);
            MenuManager.OpenOverlayMenu(new UpdaterOverlay(), 0f);

            _ = Task.Run(async () =>
            {
                var updater = new Updater();
                AppVersion? update = null;

                try
                {
                    update = await updater.CheckForUpdate();
                }
                catch
                {
                    update = null;
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    MenuManager.CloseOverlay();
                    MenuManager.Instance?.UnlockMenu();

                    if (update == null)
                        MenuManager.OpenMenu(Res.HomeMenu);
                    else
                        MenuManager.OpenMenu(new UpdaterMenu(update));
                });
            });
        }

        private SettingsCheckbox AddCheckbox(
            List<UIObject> objects,
            IslandObject island,
            string text,
            bool isChecked,
            Action? onChanged = null,
            float left = ContentLeft)
        {
            var checkbox = new SettingsCheckbox(island, text, new Vec2(left, 0), CheckboxSize, onChanged, UIAlignment.TopLeft)
            {
                Anchor = new Vec2(0, 0.5f)
            };
            checkbox.IsChecked = isChecked;
            AddContent(objects, checkbox);
            return checkbox;
        }

        private DWMultiSelectionButton AddSelector(
            List<UIObject> objects,
            IslandObject island,
            string[] options,
            float left = ContentLeft,
            float height = 32f)
        {
            var selector = new DWMultiSelectionButton(
                island,
                options,
                new Vec2(left, 0),
                new Vec2(IslandSize().X - left * 2f, height),
                UIAlignment.TopLeft);

            selector.Anchor.X = 0;
            AddContent(objects, selector);
            return selector;
        }

        private DWText AddTitle(List<UIObject> objects, IslandObject island, string text)
        {
            return AddText(objects, island, text, 24, Res.SFProBold, Theme.TextMain, bottomAdjustment: TitleBottomTuck);
        }

        private DWText AddSectionTitle(List<UIObject> objects, IslandObject island, string text, float left = ContentLeft, float topPadding = 0f)
        {
            return AddText(objects, island, text, 15, Res.SFProBold, Theme.TextMain, left, topPadding, SectionTitleBottomTuck);
        }

        private DWText AddBodyText(List<UIObject> objects, IslandObject island, string text, float left = ContentLeft, float topPadding = 0f)
        {
            return AddText(objects, island, text, 12, Res.SFProRegular, Theme.TextSecond, left, topPadding);
        }

        private DWText AddText(
            List<UIObject> objects,
            IslandObject island,
            string text,
            float textSize,
            SKTypeface font,
            Col color,
            float left = ContentLeft,
            float topPadding = 0f,
            float bottomAdjustment = 0f)
        {
            if (topPadding > 0.001f)
                AddGap(objects, island, topPadding);

            var label = new DWText(island, text, new Vec2(left, 0), UIAlignment.TopLeft)
            {
                Anchor = new Vec2(0, 0),
                Color = color,
                Font = font,
                TextSize = textSize
            };
            label.Size = label.GetBoundsForString(text);

            AddContent(objects, label);
            if (Math.Abs(bottomAdjustment) > 0.001f)
                layoutBottomAdjustments[label] = bottomAdjustment;

            return label;
        }

        private void AddGap(List<UIObject> objects, IslandObject island, float height)
        {
            AddContent(objects, new SettingsSpacer(island, height));
        }

        private void AddContent(List<UIObject> objects, UIObject obj)
        {
            contentObjects.Add(obj);
            objects.Add(obj);
        }

        private void SetRefreshRateSubSettingsEnabled(bool enabled, bool immediate)
        {
            SetObjectEnabled(limitRefreshRateWhenIdle, enabled, immediate);
            SetObjectEnabled(limitRefreshRateDisclaimer1, enabled, immediate);
            SetObjectEnabled(limitRefreshRateDisclaimer2, enabled, immediate);

            if (!enabled && limitRefreshRateWhenIdle != null)
                limitRefreshRateWhenIdle.IsChecked = false;

            layoutDirty = true;
        }

        private void SetHomeMenuShadowEnabled(bool enabled, bool immediate)
        {
            SetObjectEnabled(toggleHomeMenuShadow, enabled, immediate);

            if (!enabled && toggleHomeMenuShadow != null)
                toggleHomeMenuShadow.IsChecked = false;

            layoutDirty = true;
        }

        private static void SetObjectEnabled(UIObject? obj, bool enabled, bool immediate)
        {
            if (obj == null)
                return;

            if (immediate)
                obj.SilentSetActive(enabled);
            else
                obj.IsEnabled = enabled;
        }

        private bool NeedsRealtimeLayout()
        {
            return layoutDirty
                || Math.Abs(yScrollOffset - Mathf.Clamp(yScrollOffset, -cachedScrollLimit, 0f)) > ScrollEpsilon
                || Math.Abs(ySmoothScroll - yScrollOffset) > ScrollEpsilon;
        }

        private void LayoutContent()
        {
            float y = ContentTop;

            foreach (var uiObject in contentObjects)
            {
                if (uiObject == null || !uiObject.IsEnabled)
                    continue;

                uiObject.LocalPosition.Y = y + ySmoothScroll;
                y += Math.Max(1f, uiObject.Size.Y) + ContentSpacing + GetLayoutBottomAdjustment(uiObject);
            }

            float contentHeight = Math.Max(0f, y - ContentTop - ContentSpacing);
            float visibleHeight = Math.Max(1f, IslandSize().Y - ContentTop - BottomReserve);
            cachedScrollLimit = Math.Max(0f, contentHeight - visibleHeight);

            if (cachedScrollLimit <= 0f)
            {
                yScrollOffset = 0f;
                ySmoothScroll = 0f;
            }

            layoutDirty = false;
        }

        private float BuildContentSignature()
        {
            float signature = contentObjects.Count * 19f;

            foreach (var uiObject in contentObjects)
            {
                if (uiObject == null || !uiObject.IsEnabled)
                    continue;

                signature += uiObject.Size.X * 0.013f + uiObject.Size.Y * 0.37f;
                signature += GetLayoutBottomAdjustment(uiObject) * 0.11f;
            }

            return signature;
        }

        private float GetLayoutBottomAdjustment(UIObject uiObject)
        {
            return layoutBottomAdjustments.TryGetValue(uiObject, out float adjustment) ? adjustment : 0f;
        }

        private static float Smooth(float current, float target, float speed, float deltaTime, float epsilon)
        {
            if (Math.Abs(current - target) <= epsilon)
                return target;

            return Mathf.Lerp(current, target, speed * deltaTime);
        }

        private static void AddRegisterableSettingsFromAssembly(Assembly assembly, List<IRegisterableSetting> target)
        {
            foreach (var option in GetLoadableTypes(assembly))
            {
                if (!typeof(IRegisterableSetting).IsAssignableFrom(option) || !option.IsClass || option.IsAbstract)
                    continue;

                try
                {
                    if (Activator.CreateInstance(option) is IRegisterableSetting optionInstance)
                        target.Add(optionInstance);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SettingsMenu: failed to create setting {option.FullName}: {ex.Message}");
                }
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        }

        private sealed class SettingsSpacer : UIObject
        {
            public SettingsSpacer(UIObject? parent, float height)
                : base(parent, Vec2.zero, new Vec2(1, Math.Max(1f, height)), UIAlignment.TopLeft)
            {
                UseGpuCaching = false;
                Color = Col.Transparent;
            }

            public override void Draw(SKCanvas canvas)
            {
            }
        }

        private sealed class SettingsRenderDemand : UIObject
        {
            private readonly Func<bool> shouldRender;

            public SettingsRenderDemand(UIObject? parent, Func<bool> shouldRender)
                : base(parent, Vec2.zero, Vec2.one, UIAlignment.TopLeft)
            {
                this.shouldRender = shouldRender;
                UseGpuCaching = false;
                Color = Col.Transparent;
                maskInToIsland = false;
                expandInteractionRect = 0;
            }

            public override bool WantsRealtimeUpdate => shouldRender();

            public override void Draw(SKCanvas canvas)
            {
            }
        }
    }
}
