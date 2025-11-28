using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using Newtonsoft.Json;

/*
 *
 *  Overview:
 *      - Handles audio visual displaying for SmallVisualiserWidget
 *      - Allows user to modify some visual settings.
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    18 May 2025
 *  Last Modified:          27 November 2025
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
                    enableColourTransition = false
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

            displayDotWhenIdle.clickCallback += () =>
            {
                saveData.displayDotWhenIdle = displayDotWhenIdle.IsChecked;
            };

            enableColourTransition.clickCallback += () =>
            {
                saveData.enableColourTransition = enableColourTransition.IsChecked;
            };

            displayDotWhenIdle.IsChecked = saveData.displayDotWhenIdle;
            enableColourTransition.IsChecked = saveData.enableColourTransition;

            displayDotWhenIdle.Anchor.X = 0;
            enableColourTransition.Anchor.X = 0;

            objects.Add(displayDotWhenIdle);
            objects.Add(enableColourTransition);

            return objects;
        }
    }

    public class SmallVisualiserWidget : SmallWidgetBase
    {
        AudioVisualiser audioVisualiser;

        public SmallVisualiserWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            audioVisualiser = new AudioVisualiser(this, new Vec2(0, 0), new Vec2(GetWidgetSize().X, GetWidgetSize().Y - 2), UIAlignment.Center);
            audioVisualiser.EnableColourTransition = RegisterSmallVisualiserWidgetSettings.saveData.enableColourTransition;
            audioVisualiser.EnableDotWhenLow = RegisterSmallVisualiserWidgetSettings.saveData.displayDotWhenIdle;
            audioVisualiser.BlurAmount = 0.3f;
            AddLocalObject(audioVisualiser);
        }

        protected override float GetWidgetWidth()
        {
            return base.GetWidgetWidth() - 10;
        }
    }
}
