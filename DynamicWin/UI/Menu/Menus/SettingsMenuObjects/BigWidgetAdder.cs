using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Widgets;
using DynamicWin.Utils;
using System.Windows.Controls;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class BigWidgetAdder : UIObject
    {
        AddNew addNew;

        public BigWidgetAdder(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            Color = Theme.WidgetBackground.Override(a: 0.1f);
            roundRadius = 20;

            Anchor.Y = 0;

            addNew = new AddNew(this, Vec2.zero, new Vec2(size.X, 45), UIAlignment.BottomLeft);
            addNew.Anchor.Y = 0;
            AddLocalObject(addNew);

            UpdateWidgetDisplay();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            int line = (int)(Math.Floor(displays.Count / maxE));

            addNew.LocalPosition.Y = Mathf.Lerp(addNew.LocalPosition.Y, -line * 45 - 45, 15f * deltaTime);
            addNew.LocalPosition.X = Mathf.Lerp(addNew.LocalPosition.X, isDisplayEven() ? Size.X / 2f : Size.X / 1.3333333f, 15f * deltaTime);
            addNew.Size.X = Mathf.Lerp(addNew.Size.X, isDisplayEven() ? Size.X : Size.X / 2, 15f * deltaTime);

            var lines2 = (int)Math.Max(1, (displays.Count / maxE + 1));
            Size.Y = Mathf.Lerp(Size.Y, lines2 * 45, 15f * RendererMain.Instance.DeltaTime);
        }

        bool isDisplayEven()
        {
            return displays.Count % 2 == 0;
        }

        List<BigWidgetAdderDisplay> displays = new List<BigWidgetAdderDisplay>();
        float maxE = 2;

        void UpdateWidgetDisplay()
        {
            displays.ForEach((x) => DestroyLocalObject(x));
            displays.Clear();

            Dictionary<string, IRegisterableWidget> bigWidgets = new Dictionary<string, IRegisterableWidget>();


            foreach (var widget in Res.availableBigWidgets)
            {
                if (bigWidgets.ContainsKey(widget.GetType().FullName)) continue;
                bigWidgets.Add(widget.GetType().FullName, widget);
                System.Diagnostics.Debug.WriteLine(widget.GetType().FullName);
            }

            int c = 0;
            foreach (var bigWidget in Settings.bigWidgets)
            {
                if (!bigWidgets.ContainsKey(bigWidget)) continue;

                var widget = bigWidgets[bigWidget.ToString()];

                var display = new BigWidgetAdderDisplay(this, widget.WidgetName, UIAlignment.BottomLeft);

                display.onEditRemoveWidget += () => {
                    Settings.bigWidgets.Remove(bigWidget);
                    UpdateWidgetDisplay();
                };

                display.onEditMoveWidgetRight += () => {
                    int index = Math.Clamp(Settings.bigWidgets.IndexOf(bigWidget) + 1, 0, Settings.bigWidgets.Count - 1);
                    Settings.bigWidgets.Remove(bigWidget);

                    Settings.bigWidgets.Insert(index, bigWidget);
                    UpdateWidgetDisplay();
                };

                display.onEditMoveWidgetLeft += () => {
                    int index = Math.Clamp(Settings.bigWidgets.IndexOf(bigWidget) - 1, 0, Settings.bigWidgets.Count - 1);
                    Settings.bigWidgets.Remove(bigWidget);

                    Settings.bigWidgets.Insert(index, bigWidget);
                    UpdateWidgetDisplay();
                };

                int line = (int)(c / maxE);

                display.LocalPosition.X = (c % 2) * Size.X / 2;
                display.LocalPosition.Y -= 45 + line * 45;

                displays.Add(display);
                AddLocalObject(display);

                c++;
            }
        }

        public override ContextMenu? GetContextMenu()
        {
            var ctx = new System.Windows.Controls.ContextMenu();
            bool anyWidgetsLeft = false;

            foreach (var availableWidget in Res.availableBigWidgets)
            {
                if (Settings.bigWidgets.Contains(availableWidget.GetType().FullName)) continue;

                anyWidgetsLeft = true;

                var item = new MenuItem() { Header = availableWidget.GetType().Namespace.Split('.')[0] + ": " + availableWidget.WidgetName };
                item.Click += (x, y) =>
                {
                    Settings.bigWidgets.Add(availableWidget.GetType().FullName);
                    UpdateWidgetDisplay();
                };

                ctx.Items.Add(item);
            }

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
