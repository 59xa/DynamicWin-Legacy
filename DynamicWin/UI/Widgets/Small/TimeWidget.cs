using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static DynamicWin.UI.Widgets.Small.RegisterUsedDevicesOptions;

namespace DynamicWin.UI.Widgets.Small
{
    class RegisterTimeWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => true;

        public string WidgetName => "Time Display";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new TimeWidget(parent, position, alignment);
        }
    }

    class RegisterTimeWidgetSettings : IRegisterableSetting
    {
        public string SettingID => "timewidget";

        public string SettingTitle => "Clock";


        public static TimeWidgetSave saveData;

        public struct TimeWidgetSave
        {
            public bool militaryTime;
        }

        public void LoadSettings()
        {
            if (SaveManager.Contains(SettingID))
            {
                saveData = JsonConvert.DeserializeObject<TimeWidgetSave>((string)SaveManager.Get(SettingID));
            }
            else
            {
                saveData = new TimeWidgetSave() { militaryTime = false };
            }
        }

        public void SaveSettings()
        {
            SaveManager.Add(SettingID, JsonConvert.SerializeObject(saveData));
        }

        public List<UIObject> SettingsObjects()
        {
            var objects = new List<UIObject>();

            var militaryTime = new DWCheckbox(null, "Use 24-hour format", new Vec2(25, 0), new Vec2(25, 25), null, UIAlignment.TopLeft);

            militaryTime.clickCallback += () =>
            {
                saveData.militaryTime = militaryTime.IsChecked;
            };

            militaryTime.IsChecked = saveData.militaryTime;
            militaryTime.Anchor.X = 0;
            objects.Add(militaryTime);

            return objects;
        }
    }

    public class TimeWidget : SmallWidgetBase
    {
        DWText timeText;
        private string lastRenderedTime = string.Empty;
        private DateTime nextTimeRefresh = DateTime.MinValue;

        public TimeWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            lastRenderedTime = GetTime();
            timeText = new DWText(this, lastRenderedTime, Vec2.zero, UIAlignment.Center);
            timeText.TextSize = 14;
            timeText.Font = Res.SFProRegular;
            AddLocalObject(timeText);
            ScheduleNextRefresh();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (DateTime.Now < nextTimeRefresh) return;

            string current = GetTime();
            if (current != lastRenderedTime)
            {
                lastRenderedTime = current;
                timeText.SilentSetText(current);
            }

            ScheduleNextRefresh();
        }

        protected override float GetWidgetWidth() { return RegisterTimeWidgetSettings.saveData.militaryTime ? 35 : 55; }

        string GetTime()
        {
            return RegisterTimeWidgetSettings.saveData.militaryTime ? DateTime.Now.ToString("HH:mm") : DateTime.Now.ToString("hh:mm tt", new System.Globalization.CultureInfo("en-US"));
        }

        private void ScheduleNextRefresh()
        {
            var now = DateTime.Now;
            nextTimeRefresh = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0)
                .AddMinutes(1)
                .AddMilliseconds(50);
        }
    }
}
