using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Widgets;
using DynamicWin.Utils;
using System.Windows.Controls;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class BigWidgetAdder : UIObject
    {
        private const float RowHeight = 45f;
        private const int Columns = 2;
        private const float AnimationSpeed = 15f;
        private const float Epsilon = 0.05f;

        private readonly AddNew addNew;
        private readonly List<BigWidgetAdderDisplay> displays = new List<BigWidgetAdderDisplay>();

        private float targetHeight;
        private float targetAddX;
        private float targetAddY;
        private float targetAddWidth;
        private bool layoutDirty = true;

        public BigWidgetAdder(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, position, size, alignment)
        {
            UseGpuCaching = false;
            Color = Theme.WidgetBackground.Override(a: 0.1f);
            roundRadius = 20;
            Anchor.Y = 0;

            addNew = new AddNew(this, Vec2.zero, new Vec2(size.X, RowHeight), UIAlignment.BottomLeft);
            addNew.Anchor.Y = 0;
            AddLocalObject(addNew);

            UpdateWidgetDisplay();
            RecalculateLayout();
            ApplyLayoutTargets(immediate: true);
        }

        public override bool WantsRealtimeUpdate
        {
            get
            {
                return Math.Abs(addNew.LocalPosition.X - targetAddX) > Epsilon
                    || Math.Abs(addNew.LocalPosition.Y - targetAddY) > Epsilon
                    || Math.Abs(addNew.Size.X - targetAddWidth) > Epsilon
                    || Math.Abs(Size.Y - targetHeight) > Epsilon;
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (layoutDirty)
                RecalculateLayout();

            ApplyLayoutTargets(immediate: false, deltaTime);
        }

        public override ContextMenu? GetContextMenu()
        {
            var ctx = new ContextMenu();
            bool anyWidgetsLeft = false;

            foreach (var availableWidget in Res.availableBigWidgets)
            {
                string? fullName = availableWidget.GetType().FullName;
                if (string.IsNullOrEmpty(fullName) || Settings.bigWidgets.Contains(fullName))
                    continue;

                anyWidgetsLeft = true;

                var item = new MenuItem() { Header = $"{GetWidgetSourceName(availableWidget)}: {availableWidget.WidgetName}" };
                item.Click += (x, y) =>
                {
                    Settings.bigWidgets.Add(fullName);
                    UpdateWidgetDisplay();
                };

                ctx.Items.Add(item);
            }

            if (anyWidgetsLeft)
                return ctx;

            var empty = new ContextMenu();
            empty.Items.Add(new MenuItem()
            {
                Header = "No widgets available.",
                IsEnabled = false
            });
            return empty;
        }

        private void UpdateWidgetDisplay()
        {
            for (int i = displays.Count - 1; i >= 0; i--)
                DestroyLocalObject(displays[i]);

            displays.Clear();

            var bigWidgets = Res.availableBigWidgets
                .Where(widget => !string.IsNullOrEmpty(widget.GetType().FullName))
                .GroupBy(widget => widget.GetType().FullName!)
                .ToDictionary(group => group.Key, group => group.First());

            for (int i = 0; i < Settings.bigWidgets.Count; i++)
            {
                string bigWidget = Settings.bigWidgets[i];
                if (!bigWidgets.TryGetValue(bigWidget, out var widget))
                    continue;

                string capturedWidget = bigWidget;
                var display = new BigWidgetAdderDisplay(this, widget.WidgetName, UIAlignment.BottomLeft);

                display.onEditRemoveWidget += () =>
                {
                    Settings.bigWidgets.Remove(capturedWidget);
                    UpdateWidgetDisplay();
                };

                display.onEditMoveWidgetRight += () =>
                {
                    MoveWidget(capturedWidget, 1);
                    UpdateWidgetDisplay();
                };

                display.onEditMoveWidgetLeft += () =>
                {
                    MoveWidget(capturedWidget, -1);
                    UpdateWidgetDisplay();
                };

                displays.Add(display);
                AddLocalObject(display);
            }

            layoutDirty = true;
        }

        private void RecalculateLayout()
        {
            for (int i = 0; i < displays.Count; i++)
            {
                int row = i / Columns;
                int column = i % Columns;

                var display = displays[i];
                display.Size = new Vec2(Size.X / Columns, RowHeight);
                display.LocalPosition.X = column * Size.X / Columns;
                display.LocalPosition.Y = -RowHeight - row * RowHeight;
            }

            int addRow = displays.Count / Columns;
            bool evenDisplayCount = displays.Count % Columns == 0;

            targetAddY = -RowHeight - addRow * RowHeight;
            targetAddX = evenDisplayCount ? Size.X / 2f : Size.X * 0.75f;
            targetAddWidth = evenDisplayCount ? Size.X : Size.X / Columns;
            targetHeight = Math.Max(RowHeight, (addRow + 1) * RowHeight);

            layoutDirty = false;
        }

        private void ApplyLayoutTargets(bool immediate, float deltaTime = 0f)
        {
            if (immediate)
            {
                addNew.LocalPosition.X = targetAddX;
                addNew.LocalPosition.Y = targetAddY;
                addNew.Size = new Vec2(targetAddWidth, RowHeight);
                Size = new Vec2(Size.X, targetHeight);
                return;
            }

            addNew.LocalPosition.X = Smooth(addNew.LocalPosition.X, targetAddX, AnimationSpeed, deltaTime, Epsilon);
            addNew.LocalPosition.Y = Smooth(addNew.LocalPosition.Y, targetAddY, AnimationSpeed, deltaTime, Epsilon);
            addNew.Size = new Vec2(Smooth(addNew.Size.X, targetAddWidth, AnimationSpeed, deltaTime, Epsilon), RowHeight);
            Size = new Vec2(Size.X, Smooth(Size.Y, targetHeight, AnimationSpeed, deltaTime, Epsilon));
        }

        private static void MoveWidget(string widget, int direction)
        {
            int currentIndex = Settings.bigWidgets.IndexOf(widget);
            if (currentIndex < 0)
                return;

            int nextIndex = Math.Clamp(currentIndex + direction, 0, Settings.bigWidgets.Count - 1);
            if (nextIndex == currentIndex)
                return;

            Settings.bigWidgets.RemoveAt(currentIndex);
            Settings.bigWidgets.Insert(nextIndex, widget);
        }

        private static string GetWidgetSourceName(IRegisterableWidget widget)
        {
            string? widgetNamespace = widget.GetType().Namespace;
            if (string.IsNullOrEmpty(widgetNamespace))
                return "DynamicWin";

            return widgetNamespace.Split('.')[0];
        }

        private static float Smooth(float current, float target, float speed, float deltaTime, float epsilon)
        {
            if (Math.Abs(current - target) <= epsilon)
                return target;

            return Mathf.Lerp(current, target, speed * deltaTime);
        }
    }
}
