using DynamicWin.UI.Menu.Menus;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.Utils;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Newtonsoft.Json;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.UI.Widgets.Small
{
    class RegisterUsedDevicesWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => true;
        public string WidgetName => "Used Devices";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new UsedDevicesWidget(parent, position, alignment);
        }
    }

    class RegisterUsedDevicesOptions : IRegisterableSetting
    {
        public string SettingTitle => "Used Devices";

        public string SettingID => "useddevicewidget";

        public static UsedDevicesOptionsSave saveData;

        public struct UsedDevicesOptionsSave
        {
            public bool enableIndicator;
            public float indicatorThreshold;
        }

        public void LoadSettings()
        {
            if (SaveManager.Contains(SettingID))
            {
                saveData = JsonConvert.DeserializeObject<UsedDevicesOptionsSave>((string)SaveManager.Get(SettingID));
            }
            else
            {
                saveData = new UsedDevicesOptionsSave() { indicatorThreshold = 0.5f };
            }
        }

        public void SaveSettings()
        {
            SaveManager.Add(SettingID, JsonConvert.SerializeObject(saveData));
        }

        public List<UIObject> SettingsObjects()
        {
            var objects = new List<UIObject>();

            var loudnessMeter = new LoudnessMeter(null, new Vec2(0, 0), new Vec2(400, 7.5f));

            var thresholdSlider = new DWSlider(null, new Vec2(0, 0), new Vec2(400, 25));
            thresholdSlider.Value = Mathf.Clamp(saveData.indicatorThreshold, 0.05f, 1f);
            thresholdSlider.clickCallback = (x) =>
            {
                saveData.indicatorThreshold = x;
            };

            var enableIndicatorCheckbox = new DWCheckbox(null, "Display microphone indicator", new Vec2(25, 0), new Vec2(25, 25), null, alignment: UIAlignment.TopLeft);
            enableIndicatorCheckbox.IsChecked = saveData.enableIndicator;

            var thresholdText = new DWText(null, "Threshold", new Vec2(25, 0), UIAlignment.TopLeft);

            enableIndicatorCheckbox.clickCallback = () =>
            {
                saveData.enableIndicator = enableIndicatorCheckbox.IsChecked;
                thresholdSlider.SetActive(saveData.enableIndicator);
                loudnessMeter.SetActive(saveData.enableIndicator);
                thresholdText.SetActive(saveData.enableIndicator);
            };

            thresholdSlider.SilentSetActive(saveData.enableIndicator);
            loudnessMeter.SilentSetActive(saveData.enableIndicator);
            thresholdText.SilentSetActive(saveData.enableIndicator);

            objects.Add(enableIndicatorCheckbox);
            objects.Add(thresholdText);
            objects.Add(loudnessMeter);
            objects.Add(thresholdSlider);
            
            return objects;
        }
    }

    internal class LoudnessMeter : DWProgressBarEx
    {
        public LoudnessMeter(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            // Make the smoothing more responsive like the legacy meter
            this.Smoothing = 25f;
            IsLocked = true;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Update target value (use ForceSetValue so IsLocked can remain true and UI cannot modify it)
            ForceSetValue(GetMicrophoneLoudness());

            // Compute colours similar to the legacy behaviour
            var colBase = (Value > 0.85f) ? new Col(1, 0, 0) : (Value > 0.65f) ? new Col(1, 1, 0) : new Col(0, 1, 0);
            var factor = RegisterUsedDevicesOptions.saveData.indicatorThreshold < Value ? 1f : 0.45f;

            var contentCol = GetColor(colBase) * factor;
            ForegroundColor = contentCol;
            BackgroundColor = contentCol * 0.25f;
        }

        public static float GetMicrophoneLoudness()
        {
            return (float)Math.Sqrt(((DynamicWinMain.defaultMicrophone == null) ? 0f : DynamicWinMain.defaultMicrophone.AudioMeterInformation.MasterPeakValue) + 0.001);
        }
    }

    public class UsedDevicesWidget : SmallWidgetBase
    {
        public UsedDevicesWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {

        }

        public override void OnDestroy()
        {
            base.OnDestroy();
        }

        float camDotSize = 2.5f;
        float camDotSizeCurrent = 0f;
        float camDotPositionX = 0f;

        float micDotSize = 2.5f;
        float micDotSizeCurrent = 0f;
        float micDotPositionX = 0f;

        float seperation = 6.5f;

        protected override float GetWidgetWidth()
        {
            return camDotSizeCurrent * 4 + micDotSizeCurrent * 4;
        }

        float sinCycleCamera = 1f;
        float sinCycleMicrophone = 0f;

        float sinSpeed = 2.75f;
        private DateTime lastDeviceUsagePoll = DateTime.MinValue;
        private readonly TimeSpan deviceUsagePollInterval = TimeSpan.FromMilliseconds(750);
        private bool cachedCamActive;
        private bool cachedMicActive;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (cachedCamActive)
                sinCycleCamera += sinSpeed * deltaTime;
            if (cachedMicActive || isMicrophoneIndicatorShowing)
                sinCycleMicrophone += sinSpeed * deltaTime;

            var now = DateTime.UtcNow;
            if ((now - lastDeviceUsagePoll) >= deviceUsagePollInterval)
            {
                lastDeviceUsagePoll = now;
                cachedCamActive = DeviceUsageChecker.IsWebcamInUse();
                cachedMicActive = DeviceUsageChecker.IsMicrophoneInUse();
            }

            camDotSizeCurrent = Mathf.Lerp(camDotSizeCurrent, cachedCamActive ? camDotSize : 0f, 5f * deltaTime);
            micDotSizeCurrent = Mathf.Lerp(micDotSizeCurrent, cachedMicActive ? micDotSize : 0f, 5f * deltaTime);

            if(cachedCamActive && cachedMicActive)
            {
                camDotPositionX = Mathf.Lerp(camDotPositionX, seperation, 5f * deltaTime);
                micDotPositionX = Mathf.Lerp(micDotPositionX, -seperation, 5f * deltaTime);
            }
            else
            {
                camDotPositionX = Mathf.Lerp(camDotPositionX, 0, 5f * deltaTime);
                micDotPositionX = Mathf.Lerp(micDotPositionX, 0, 5f * deltaTime);
            }

            isMicrophoneIndicatorShowing =
                RegisterUsedDevicesOptions.saveData.enableIndicator &&
                LoudnessMeter.GetMicrophoneLoudness() > RegisterUsedDevicesOptions.saveData.indicatorThreshold;
        }

        bool isMicrophoneIndicatorShowing = false;

        public override bool WantsContinuousUpdate =>
            cachedCamActive ||
            cachedMicActive ||
            isMicrophoneIndicatorShowing ||
            camDotSizeCurrent > 0.05f ||
            micDotSizeCurrent > 0.05f;

        public override void DrawWidget(SKCanvas canvas)
        {
            var paint = GetPaint();

            var camPos = GetScreenPosFromRawPosition(new Vec2(camDotPositionX, 0), new Vec2(0, camDotSizeCurrent / 2), UIAlignment.Center, this);

            paint.Color = GetColor(Col.Lerp(Theme.Error, Theme.Error * 0.6f, Mathf.Remap((float)Math.Sin(sinCycleCamera), -1, 1, 0, 1))).Value();
            canvas.DrawCircle(camPos.X, camPos.Y, camDotSizeCurrent, paint);

            var micPos = GetScreenPosFromRawPosition(new Vec2(micDotPositionX, 0), new Vec2(0, micDotSizeCurrent / 2), UIAlignment.Center, this);

            paint.Color = GetColor(Col.Lerp(Theme.Success, Theme.Success * 0.6f, Mathf.Remap((float)Math.Sin(sinCycleMicrophone), -1, 1, 0, 1))).Value();
            canvas.DrawCircle(micPos.X, micPos.Y, micDotSizeCurrent, paint);

            if (isMicrophoneIndicatorShowing && RegisterUsedDevicesOptions.saveData.enableIndicator)
            {
                paint.IsStroke = true;
                paint.StrokeWidth = 1.25f;

                var micIndicatorPos = GetScreenPosFromRawPosition(new Vec2(micDotPositionX, 0), new Vec2(0, micDotSizeCurrent / 2), UIAlignment.Center, this);
                canvas.DrawCircle(micIndicatorPos.X, micIndicatorPos.Y, micDotSizeCurrent + 3f, paint);
            }
        }
    }
}
