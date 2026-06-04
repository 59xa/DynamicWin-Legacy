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
        private const float EdgePadding = 15f;
        private const float WidgetSpacing = 30f;
        private const float MiddleSpacing = 35f;

        private readonly UIObject container;
        private readonly float minimumWidth;
        private float lastLayoutSignature = float.NaN;
        private bool layoutDirty = true;

        public readonly List<SmallWidgetBase> smallLeftWidgets = new List<SmallWidgetBase>();
        public readonly List<SmallWidgetBase> smallRightWidgets = new List<SmallWidgetBase>();
        public readonly List<SmallWidgetBase> smallCenterWidgets = new List<SmallWidgetBase>();

        public SmallWidgetAdder(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, position, size, alignment)
        {
            UseGpuCaching = false;
            minimumWidth = size.X;
            Color = Theme.WidgetBackground.Override(a: 0.1f);
            roundRadius = 25;

            container = new UIObject(this, Vec2.zero, new Vec2(size.X - 100, size.Y), UIAlignment.Center)
            {
                Color = Col.Transparent,
                UseGpuCaching = false
            };
            AddLocalObject(container);

            UpdateWidgetDisplay();
            LayoutWidgets();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            float signature = BuildLayoutSignature();
            if (float.IsNaN(lastLayoutSignature) || Math.Abs(signature - lastLayoutSignature) > 0.01f)
            {
                lastLayoutSignature = signature;
                layoutDirty = true;
            }

            if (layoutDirty)
                LayoutWidgets();
        }

        public override ContextMenu? GetContextMenu()
        {
            var ctx = new ContextMenu();
            bool anyWidgetsLeft = false;

            var left = new MenuItem() { Header = "Left", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/align-left.png") };
            var middle = new MenuItem() { Header = "Middle", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/align-centre.png") };
            var right = new MenuItem() { Header = "Right", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/align-right.png") };

            foreach (var availableWidget in Res.availableSmallWidgets)
            {
                string? fullName = availableWidget.GetType().FullName;
                if (string.IsNullOrEmpty(fullName) || IsWidgetSelected(fullName))
                    continue;

                anyWidgetsLeft = true;

                left.Items.Add(CreateAddItem(availableWidget, () =>
                {
                    Settings.smallWidgetsLeft.Add(fullName);
                    UpdateWidgetDisplay();
                }));

                middle.Items.Add(CreateAddItem(availableWidget, () =>
                {
                    Settings.smallWidgetsMiddle.Add(fullName);
                    UpdateWidgetDisplay();
                }));

                right.Items.Add(CreateAddItem(availableWidget, () =>
                {
                    Settings.smallWidgetsRight.Add(fullName);
                    UpdateWidgetDisplay();
                }));
            }

            ctx.Items.Add(left);
            ctx.Items.Add(middle);
            ctx.Items.Add(right);

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
            ClearWidgets(smallRightWidgets);
            ClearWidgets(smallLeftWidgets);
            ClearWidgets(smallCenterWidgets);

            var smallWidgets = Res.availableSmallWidgets
                .Where(widget => !string.IsNullOrEmpty(widget.GetType().FullName))
                .GroupBy(widget => widget.GetType().FullName!)
                .ToDictionary(group => group.Key, group => group.First());

            AddConfiguredWidgets(Settings.smallWidgetsMiddle, smallCenterWidgets, smallWidgets, UIAlignment.Center);
            AddConfiguredWidgets(Settings.smallWidgetsLeft, smallLeftWidgets, smallWidgets, UIAlignment.MiddleLeft);
            AddConfiguredWidgets(Settings.smallWidgetsRight, smallRightWidgets, smallWidgets, UIAlignment.MiddleRight);

            layoutDirty = true;
        }

        private void AddConfiguredWidgets(
            List<string> configuredWidgets,
            List<SmallWidgetBase> destination,
            Dictionary<string, IRegisterableWidget> availableWidgets,
            UIAlignment alignment)
        {
            foreach (string configuredWidget in configuredWidgets.ToArray())
            {
                if (!availableWidgets.TryGetValue(configuredWidget, out var widget))
                    continue;

                string capturedWidget = configuredWidget;
                var instance = (SmallWidgetBase)widget.CreateWidgetInstance(container, Vec2.zero, alignment);
                instance.isEditMode = true;

                instance.onEditRemoveWidget += () =>
                {
                    configuredWidgets.Remove(capturedWidget);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetLeft += () =>
                {
                    MoveWidget(configuredWidgets, capturedWidget, 1);
                    UpdateWidgetDisplay();
                };

                instance.onEditMoveWidgetRight += () =>
                {
                    MoveWidget(configuredWidgets, capturedWidget, -1);
                    UpdateWidgetDisplay();
                };

                destination.Add(instance);
                AddLocalObject(instance);
            }
        }

        private void LayoutWidgets()
        {
            float leftStackedPos = EdgePadding;
            foreach (var smallLeft in smallLeftWidgets)
            {
                smallLeft.Anchor.X = 0;
                smallLeft.LocalPosition.X = leftStackedPos;
                leftStackedPos += WidgetSpacing + smallLeft.GetWidgetSize().X;
            }

            float rightStackedPos = -EdgePadding;
            foreach (var smallRight in smallRightWidgets)
            {
                smallRight.Anchor.X = 1;
                smallRight.LocalPosition.X = rightStackedPos;
                rightStackedPos -= WidgetSpacing + smallRight.GetWidgetSize().X;
            }

            float centerStackPos = 0f;
            foreach (var smallCenter in smallCenterWidgets)
            {
                smallCenter.Anchor.X = 1;
                smallCenter.LocalPosition.X = centerStackPos;
                centerStackPos -= WidgetSpacing + smallCenter.GetWidgetSize().X;
            }

            foreach (var smallCenter in smallCenterWidgets)
                smallCenter.LocalPosition.X -= centerStackPos / 2f + WidgetSpacing;

            float requiredWidth = GetWidgetsWidth(smallLeftWidgets)
                + GetWidgetsWidth(smallRightWidgets)
                + GetWidgetsWidth(smallCenterWidgets)
                + WidgetSpacing * (smallLeftWidgets.Count + smallRightWidgets.Count + smallCenterWidgets.Count + 0.25f)
                + MiddleSpacing;

            Size = new Vec2(Math.Max(minimumWidth, requiredWidth), Size.Y);
            container.Size = new Vec2(Math.Max(1f, Size.X - 100f), Size.Y);
            layoutDirty = false;
        }

        private float BuildLayoutSignature()
        {
            float signature = Size.X * 0.03f + Size.Y;

            AddWidgetSignature(smallLeftWidgets, ref signature);
            AddWidgetSignature(smallCenterWidgets, ref signature);
            AddWidgetSignature(smallRightWidgets, ref signature);

            return signature;
        }

        private static void AddWidgetSignature(List<SmallWidgetBase> widgets, ref float signature)
        {
            signature += widgets.Count * 17f;

            foreach (var widget in widgets)
            {
                Vec2 size = widget.GetWidgetSize();
                signature += size.X * 0.37f + size.Y * 0.11f;
            }
        }

        private static float GetWidgetsWidth(List<SmallWidgetBase> widgets)
        {
            float width = 0f;
            foreach (var widget in widgets)
                width += widget.GetWidgetSize().X;

            return width;
        }

        private void ClearWidgets(List<SmallWidgetBase> widgets)
        {
            for (int i = widgets.Count - 1; i >= 0; i--)
                DestroyLocalObject(widgets[i]);

            widgets.Clear();
        }

        private static bool IsWidgetSelected(string fullName)
        {
            return Settings.smallWidgetsRight.Contains(fullName)
                || Settings.smallWidgetsLeft.Contains(fullName)
                || Settings.smallWidgetsMiddle.Contains(fullName);
        }

        private static MenuItem CreateAddItem(IRegisterableWidget widget, Action click)
        {
            var item = new MenuItem() { Header = $"{GetWidgetSourceName(widget)}: {widget.WidgetName}" };
            item.Click += (x, y) => click();
            return item;
        }

        private static void MoveWidget(List<string> widgets, string widget, int direction)
        {
            int currentIndex = widgets.IndexOf(widget);
            if (currentIndex < 0)
                return;

            int nextIndex = Math.Clamp(currentIndex + direction, 0, widgets.Count - 1);
            if (nextIndex == currentIndex)
                return;

            widgets.RemoveAt(currentIndex);
            widgets.Insert(nextIndex, widget);
        }

        private static string GetWidgetSourceName(IRegisterableWidget widget)
        {
            string? widgetNamespace = widget.GetType().Namespace;
            if (string.IsNullOrEmpty(widgetNamespace))
                return "DynamicWin";

            return widgetNamespace.Split('.')[0];
        }
    }
}
