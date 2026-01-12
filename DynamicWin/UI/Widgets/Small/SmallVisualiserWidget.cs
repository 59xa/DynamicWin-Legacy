using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using Newtonsoft.Json;
using Windows.Media.Control;

/*
 *
 *  Overview:
 *      - Handles audio visual displaying for SmallVisualiserWidget
 *      - Allows user to modify some visual settings.
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    18 May 2025
 *  Last Modified:          12 January 2026
 *
 */

namespace DynamicWin.UI.Widgets.Small
{
    class RegisterSmallVisualiserWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => true;
        public string WidgetName => "Audio Visualiser";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new SmallVisualiserWidget(parent, position, alignment);
        }
    }

    class RegisterSmallVisualiserWidgetSettings : IRegisterableSetting
    {
        public string SettingID => "small-visualiser-widget";

        public string SettingTitle => "Audio Visualiser";

        public static SmallVisualiserSave saveData;

        public struct SmallVisualiserSave
        {
            public bool displayDotWhenIdle;
            public bool enableColourTransition;
            public bool useThumbnailBackground;
        }

        /// <summary>
        /// Loads the visualizer settings from persistent storage or initializes them with default values if no saved
        /// settings are found.
        /// </summary>
        /// <remarks>This method attempts to retrieve previously saved settings using the current setting
        /// identifier. If no settings are found, it initializes the settings with default values. Call this method
        /// before accessing settings to ensure they are loaded or initialized appropriately.</remarks>
        public void LoadSettings()
        {
            if (SaveManager.Contains(SettingID))
            {
                saveData = JsonConvert.DeserializeObject<SmallVisualiserSave>((string)SaveManager.Get(SettingID));
            }
            else
            {
                saveData = new SmallVisualiserSave()
                {
                    displayDotWhenIdle = true,
                    enableColourTransition = false,
                    useThumbnailBackground = true
                };
            }
        }

        /// <summary>
        /// Saves the current settings to persistent storage.
        /// </summary>
        /// <remarks>This method serializes the current settings data and stores it using the associated
        /// setting identifier. Call this method to persist any changes made to the settings so they can be restored in
        /// future sessions.</remarks>
        public void SaveSettings()
        {
            SaveManager.Add(SettingID, JsonConvert.SerializeObject(saveData));
        }

        /// <summary>
        /// Returns a list of UI objects representing the available settings controls for the visualiser.
        /// </summary>
        /// <remarks>The returned UI objects reflect the current state of the underlying settings and
        /// update the settings when interacted with. Callers can use this list to display or manage the settings UI for
        /// the visualiser.</remarks>
        /// <returns>A list of <see cref="UIObject"/> instances corresponding to the settings controls. The list contains one
        /// object for each configurable setting.</returns>
        public List<UIObject> SettingsObjects()
        {
            var objects = new List<UIObject>();

            var displayDotWhenIdle = new DWCheckbox(null, "Display visualiser dots when idle", new Vec2(25, 0), new Vec2(25, 25), null, UIAlignment.TopLeft);
            var enableColourTransition = new DWCheckbox(null, "Enable visualiser colour transitioning", new Vec2(25, 0), new Vec2(25, 25), null, UIAlignment.TopLeft);
            var useThumbnailBackground = new DWCheckbox(null, "Use media thumbnail as background", new Vec2(25, 0), new Vec2(25, 25), null, UIAlignment.TopLeft);
            var thumbnailDisclaimer = new DWText(null, "By enabling this option, colour transitioning will be bypassed.", new Vec2(25, 0), UIAlignment.TopLeft);

            displayDotWhenIdle.clickCallback += () =>
            {
                saveData.displayDotWhenIdle = displayDotWhenIdle.IsChecked;
            };

            enableColourTransition.clickCallback += () =>
            {
                saveData.enableColourTransition = enableColourTransition.IsChecked;
            };

            useThumbnailBackground.clickCallback += () =>
            {
                saveData.useThumbnailBackground = useThumbnailBackground.IsChecked;
            };

            displayDotWhenIdle.IsChecked = saveData.displayDotWhenIdle;
            enableColourTransition.IsChecked = saveData.enableColourTransition;
            useThumbnailBackground.IsChecked = saveData.useThumbnailBackground;

            displayDotWhenIdle.Anchor.X = 0;
            enableColourTransition.Anchor.X = 0;
            thumbnailDisclaimer.Anchor.X = 0;
            useThumbnailBackground.Anchor.X = 0;

            objects.Add(displayDotWhenIdle);
            objects.Add(enableColourTransition);
            objects.Add(thumbnailDisclaimer);
            objects.Add(useThumbnailBackground);

            return objects;
        }
    }

    public class SmallVisualiserWidget : SmallWidgetBase
    {
        private AudioVisualiser audioVisualiser;
        private float collapseProgress = 1f; // Start fully expanded
        private Animator? collapseAnim = null;

        private volatile bool targetExpanded = true; // Current target state
        private bool isRunning = true; // Background loop control

        public SmallVisualiserWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, position, alignment)
        {
            audioVisualiser = new AudioVisualiser(
                this,
                new Vec2(0, 0),
                new Vec2(GetWidgetSize().X, GetWidgetSize().Y - 2),
                UIAlignment.Center
            );

            audioVisualiser.EnableColourTransition = RegisterSmallVisualiserWidgetSettings.saveData.enableColourTransition;
            audioVisualiser.UseThumbnailBackground = RegisterSmallVisualiserWidgetSettings.saveData.useThumbnailBackground;
            audioVisualiser.EnableDotWhenLow = RegisterSmallVisualiserWidgetSettings.saveData.displayDotWhenIdle;
            audioVisualiser.BlurAmount = 0.3f;

            AddLocalObject(audioVisualiser);

            Task.Run(SessionMonitorLoop);
        }

        private async Task SessionMonitorLoop()
        {
            while (isRunning)
            {
                try
                {
                    var timeline = await MediaInfo.FetchCurrentTimelineAsync();

                    // Keep visualiser expanded for any session (Playing or Paused), collapse only if no session exists
                    bool newExpanded = timeline != null &&
                                       timeline.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                    if (newExpanded != targetExpanded)
                    {
                        targetExpanded = newExpanded;
                        BeginInvokeUI(() => StartCollapseOrExpand(targetExpanded));
                    }
                }
                catch
                {
                    // Swallow transient errors
                }

                await Task.Delay(500);
            }
        }

        private void StartCollapseOrExpand(bool expand)
        {
            // Avoid redundant animations
            float target = expand ? 1f : 0f;
            if (Math.Abs(collapseProgress - target) < 0.001f) return;

            // Stop any running animation
            if (collapseAnim != null)
            {
                try { collapseAnim.Stop(false); } catch { }
                try { DestroyLocalObject(collapseAnim); } catch { }
                collapseAnim = null;
            }

            collapseAnim = new Animator(300, 1);
            bool expanding = expand;

            collapseAnim.onAnimationUpdate += (t) =>
            {
                float e = Easings.EaseOutCubic(t);
                collapseProgress = expanding ? e : 1f - e;

                // Smoothly resize visualiser
                audioVisualiser.Size = new Vec2(GetWidgetSize().X * collapseProgress, GetWidgetSize().Y - 2);
                audioVisualiser.SilentSetActive(collapseProgress > 0f);
            };

            collapseAnim.onAnimationEnd += () =>
            {
                collapseProgress = expanding ? 1f : 0f;
                audioVisualiser.SilentSetActive(expanding);

                try { DestroyLocalObject(collapseAnim); } catch { }
                collapseAnim = null;
            };

            AddLocalObject(collapseAnim);
            collapseAnim.Start();
        }

        protected override float GetWidgetWidth()
        {
            float full = base.GetWidgetWidth() - 10;
            return full * collapseProgress;
        }

        public override void OnDestroy()
        {
            isRunning = false; // Stop the background loop
            base.OnDestroy();
        }
    }
}
