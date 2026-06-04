using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using SkiaSharp;
using System.Windows.Controls;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class BigWidgetAdderDisplay : UIObject
    {
        private readonly Col displayColor;
        private float hoverAlpha;
        private float scale = 1f;

        public Action? onEditRemoveWidget;
        public Action? onEditMoveWidgetLeft;
        public Action? onEditMoveWidgetRight;

        public BigWidgetAdderDisplay(UIObject? parent, string widgetName, UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, Vec2.zero, Vec2.zero, alignment)
        {
            UseGpuCaching = false;

            Size = new Vec2((parent?.Size.X ?? 400f) / 2f, 45f);
            Anchor = Vec2.zero;
            roundRadius = 45f;
            displayColor = Theme.Primary.Override(a: 0.15f);

            AddLocalObject(new DWText(this, DWText.Truncate(widgetName, 25), Vec2.zero, UIAlignment.Center)
            {
                TextSize = 14,
                Color = Theme.TextSecond
            });
        }

        public override bool WantsRealtimeUpdate
        {
            get
            {
                float targetHover = IsHovering ? 1f : 0f;
                float targetScale = IsHovering ? 1.025f : 1f;

                return Math.Abs(hoverAlpha - targetHover) > 0.01f
                    || Math.Abs(scale - targetScale) > 0.002f;
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            hoverAlpha = Smooth(hoverAlpha, IsHovering ? 1f : 0f, 9f, deltaTime, 0.01f);
            scale = Smooth(scale, IsHovering ? 1.025f : 1f, 15f, deltaTime, 0.002f);
        }

        public override void Draw(SKCanvas canvas)
        {
            int canvasRestore = canvas.Save();

            var pivot = Position + Size / 2f;
            canvas.Scale(scale, scale, pivot.X, pivot.Y);

            using var paint = GetPaint();
            paint.Color = displayColor.Override(a: 0.15f + hoverAlpha * 0.05f).Value();

            var rect = GetRect();
            rect.Deflate(5, 5);
            canvas.DrawRoundRect(rect, paint);

            canvas.RestoreToCount(canvasRestore);
        }

        public override ContextMenu? GetContextMenu()
        {
            var ctx = new ContextMenu();

            var remove = new MenuItem() { Header = "Remove", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/trash.png") };
            remove.Click += (x, y) => onEditRemoveWidget?.Invoke();

            var pushLeft = new MenuItem() { Header = "Push Left", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/left.png") };
            pushLeft.Click += (x, y) => onEditMoveWidgetLeft?.Invoke();

            var pushRight = new MenuItem() { Header = "Push Right", Icon = ContextMenuUtils.LoadMenuIcon("Resources/icons/context/right.png") };
            pushRight.Click += (x, y) => onEditMoveWidgetRight?.Invoke();

            ctx.Items.Add(remove);
            ctx.Items.Add(pushLeft);
            ctx.Items.Add(pushRight);

            return ctx;
        }

        private static float Smooth(float current, float target, float speed, float deltaTime, float epsilon)
        {
            if (Math.Abs(current - target) <= epsilon)
                return target;

            return Mathf.Lerp(current, target, speed * deltaTime);
        }
    }
}
