using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Widgets;
using DynamicWin.UI.Widgets.Small;
using DynamicWin.Utils;
using System.Windows.Controls;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class SmallWidgetAdder : UIObject
    {
        public SmallWidgetAdder(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            Color = Theme.WidgetBackground.Override(a: 0.1f);
            roundRadius = 25;

            container = new UIObject(this, Vec2.zero, new Vec2(size.X - 100, size.Y), UIAlignment.Center);
            container.Color = Col.Transparent;
            AddLocalObject(container);

            UpdateWidgetDisplay();
        }

        UIObject container;

        public List<SmallWidgetBase> smallLeftWidgets = new List<SmallWidgetBase>();
        public List<SmallWidgetBase> smallRightWidgets = new List<SmallWidgetBase>();
        public List<SmallWidgetBase> smallCenterWidgets = new List<SmallWidgetBase>();

        void UpdateWidgetDisplay()
        {
            smallRightWidgets.ForEach((x) => DestroyLocalObject(x));
            smallLeftWidgets.ForEach((x) => DestroyLocalObject(x));
            smallCenterWidgets.ForEach((x) => DestroyLocalObject(x));

            smallRightWidgets.Clear();
            smallLeftWidgets.Clear();
            smallCenterWidgets.Clear();

            Dictionary<string, IRegisterableWidget> smallWidgets = new Dictionary<string, IRegisterableWidget>();


            foreach (var widget in Res.availableSmallWidgets)
            {
                smallWidgets.Add(widget.GetType().FullName, widget);
                System.Diagnostics.Debug.WriteLine(widget.GetType().FullName);
            }

            foreach (var smallWidget in Settings.smallWidgetsMiddle)
            {
                if (!smallWidgets.ContainsKey(smallWidget)) continue;

                var widget = smallWidgets[smallWidget.ToString()];

                var instance = (SmallWidgetBase)widget.CreateWidgetInstance(container, Vec2.zero, UIAlignment.Center);
                instance.isEditMode = true;

                instance.onEditRemoveWidget += () => {
                    Settings.smallWidgetsMiddle.Remove(smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetLeft += () => {
                    int index = Math.Clamp(Settings.smallWidgetsMiddle.IndexOf(smallWidget) + 1, 0, Settings.smallWidgetsMiddle.Count - 1);
                    Settings.smallWidgetsMiddle.Remove(smallWidget);

                    Settings.smallWidgetsMiddle.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetRight += () => {
                    int index = Math.Clamp(Settings.smallWidgetsMiddle.IndexOf(smallWidget) - 1, 0, Settings.smallWidgetsMiddle.Count - 1);
                    Settings.smallWidgetsMiddle.Remove(smallWidget);

                    Settings.smallWidgetsMiddle.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                smallCenterWidgets.Add(instance);
            }

            foreach (var smallWidget in Settings.smallWidgetsLeft)
            {
                if (!smallWidgets.ContainsKey(smallWidget)) continue;

                var widget = smallWidgets[smallWidget.ToString()];

                var instance = (SmallWidgetBase)widget.CreateWidgetInstance(container, Vec2.zero, UIAlignment.MiddleLeft);
                instance.isEditMode = true;

                instance.onEditRemoveWidget += () => {
                    Settings.smallWidgetsLeft.Remove(smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetLeft += () => {
                    int index = Math.Clamp(Settings.smallWidgetsLeft.IndexOf(smallWidget) + 1, 0, Settings.smallWidgetsLeft.Count - 1);
                    Settings.smallWidgetsLeft.Remove(smallWidget);

                    Settings.smallWidgetsLeft.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetRight += () => {
                    int index = Math.Clamp(Settings.smallWidgetsLeft.IndexOf(smallWidget) - 1, 0, Settings.smallWidgetsLeft.Count - 1);
                    Settings.smallWidgetsLeft.Remove(smallWidget);

                    Settings.smallWidgetsLeft.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                smallLeftWidgets.Add(instance);
            }

            foreach (var smallWidget in Settings.smallWidgetsRight)
            {
                if (!smallWidgets.ContainsKey(smallWidget)) continue;

                var widget = smallWidgets[smallWidget.ToString()];

                var instance = (SmallWidgetBase)widget.CreateWidgetInstance(container, Vec2.zero, UIAlignment.MiddleRight);
                instance.isEditMode = true;

                instance.onEditRemoveWidget += () => {
                    Settings.smallWidgetsRight.Remove(smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetLeft += () => {
                    int index = Math.Clamp(Settings.smallWidgetsRight.IndexOf(smallWidget) + 1, 0, Settings.smallWidgetsRight.Count - 1);
                    Settings.smallWidgetsRight.Remove(smallWidget);

                    Settings.smallWidgetsRight.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetRight += () => {
                    int index = Math.Clamp(Settings.smallWidgetsRight.IndexOf(smallWidget) - 1, 0, Settings.smallWidgetsRight.Count - 1);
                    Settings.smallWidgetsRight.Remove(smallWidget);

                    Settings.smallWidgetsRight.Insert(index, smallWidget);
                    UpdateWidgetDisplay();
                };

                smallRightWidgets.Add(instance);
            }

            smallCenterWidgets.ForEach((x) => AddLocalObject(x));
            smallLeftWidgets.ForEach((x) => AddLocalObject(x));
            smallRightWidgets.ForEach((x) => AddLocalObject(x));
        }

        public float smallWidgetsSpacing = 30;
        public float middleWidgetsSpacing = 35;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            { // Left Small Widgets
                float leftStackedPos = 15f;
                foreach (var smallLeft in smallLeftWidgets)
                {
                    smallLeft.Anchor.X = 0;
                    smallLeft.LocalPosition.X = leftStackedPos;

                    leftStackedPos += smallWidgetsSpacing + smallLeft.GetWidgetSize().X;
                }
            }

            { // Right Small Widgets
                float rightStackedPos = -15f;
                foreach (var smallRight in smallRightWidgets)
                {
                    smallRight.Anchor.X = 1;
                    smallRight.LocalPosition.X = rightStackedPos;

                    rightStackedPos -= smallWidgetsSpacing + smallRight.GetWidgetSize().X;
                }
            }

            { // Center Small Widgets
                float centerStackPos = 0f;
                foreach (var smallCenter in smallCenterWidgets)
                {
                    smallCenter.Anchor.X = 1;
                    smallCenter.LocalPosition.X = centerStackPos;

                    centerStackPos -= smallWidgetsSpacing + smallCenter.GetWidgetSize().X;
                }

                foreach (var smallCenter in smallCenterWidgets)
                {
                    smallCenter.LocalPosition.X -= centerStackPos / 2 + smallWidgetsSpacing;
                }
            }

            Vec2 size = Size;

            float sizeTogether = 0f;
            smallLeftWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);
            smallRightWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);
            smallCenterWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);

            sizeTogether += smallWidgetsSpacing * (smallCenterWidgets.Count + smallLeftWidgets.Count + smallRightWidgets.Count + 0.25f) + middleWidgetsSpacing;

            size.X = (float)Math.Max(size.X, sizeTogether);
        }

        public override ContextMenu? GetContextMenu()
        {
            var ctx = new System.Windows.Controls.ContextMenu();
            bool anyWidgetsLeft = false;

            MenuItem left = new MenuItem() { Header = "Left" };
            MenuItem middle = new MenuItem() { Header = "Middle" };
            MenuItem right = new MenuItem() { Header = "Right" };

            foreach (var availableWidget in Res.availableSmallWidgets)
            {
                if (Settings.smallWidgetsRight.Contains(availableWidget.GetType().FullName) ||
                    Settings.smallWidgetsLeft.Contains(availableWidget.GetType().FullName) ||
                    Settings.smallWidgetsMiddle.Contains(availableWidget.GetType().FullName)) continue;

                anyWidgetsLeft = true;

                var itemR = new MenuItem() { Header = availableWidget.GetType().Namespace.Split('.')[0] + ": " + availableWidget.WidgetName };
                itemR.Click += (x, y) =>
                {
                    Settings.smallWidgetsRight.Add(availableWidget.GetType().FullName);
                    UpdateWidgetDisplay();
                };

                var itemM = new MenuItem() { Header = availableWidget.GetType().Namespace.Split('.')[0] + ": " + availableWidget.WidgetName };
                itemM.Click += (x, y) =>
                {
                    Settings.smallWidgetsMiddle.Add(availableWidget.GetType().FullName);
                    UpdateWidgetDisplay();
                };

                var itemL = new MenuItem() { Header = availableWidget.GetType().Namespace.Split('.')[0] + ": " + availableWidget.WidgetName };
                itemL.Click += (x, y) =>
                {
                    Settings.smallWidgetsLeft.Add(availableWidget.GetType().FullName);
                    UpdateWidgetDisplay();
                };

                left.Items.Add(itemL);
                middle.Items.Add(itemM);
                right.Items.Add(itemR);
            }

            ctx.Items.Add(left);
            ctx.Items.Add(middle);
            ctx.Items.Add(right);

            if (!anyWidgetsLeft)
            {
                var ctx2 = new ContextMenu();
                ctx2.Items.Add(new MenuItem()
                {
                    Header = "No widgets available.",
                    IsEnabled = false
                });
                return ctx2;
            }

            return ctx;
        }
    }
}
