using DynamicWin.Resources;
using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using SkiaSharp;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class AddNew : UIObject
    {
        private static readonly float[] DashIntervals = { 10f, 10f };

        public AddNew(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, position, size, alignment)
        {
            UseGpuCaching = false;
            Color = Theme.IconColor.Override(a: 0.4f);

            AddLocalObject(new DWImage(this, Res.Add, Vec2.zero, new Vec2(15, 15), UIAlignment.Center)
            {
                Color = Theme.IconColor
            });
        }

        public override void Draw(SKCanvas canvas)
        {
            using var paint = GetPaint();
            using var dash = SKPathEffect.CreateDash(DashIntervals, 0f);

            var placeRect = new SKRoundRect(SKRect.Create(Position.X, Position.Y, Size.X, Size.Y), 25);
            placeRect.Deflate(5, 5);

            paint.PathEffect = dash;
            paint.IsStroke = true;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeWidth = 2f;

            canvas.DrawRoundRect(placeRect, paint);

            placeRect.Deflate(5f, 5f);
            paint.PathEffect = null;
            paint.Color = Color.Override(a: 0.05f).Value();
            paint.IsStroke = false;

            canvas.DrawRoundRect(placeRect, paint);
        }
    }
}
