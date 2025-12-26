using DynamicWin.Main;
using DynamicWin.UI.UIElements;
using System.Collections.Generic;
using SkiaSharp;
using DynamicWin.Utils;

namespace DynamicWin.UI.UIElements.Custom
{
    public class Media : UIObject
    {
        public Media(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            
        }

        public override void Draw(SKCanvas canvas)
        {
            var rect = GetRect();
            rect.Inflate(25, 0);

            var paint = GetPaint();

            var placeRect = new SKRoundRect(SKRect.Create(Position.X, Position.Y, Size.X, Size.Y), 25);
            placeRect.Deflate(5, 5);

            float[] intervals = { 10, 10 };
            paint.PathEffect = SKPathEffect.CreateDash(intervals, 0f);

            paint.IsStroke = true;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeWidth = 2f;

            paint.Color = GetColor(Theme.IslandBackground.Inverted().Override(a: 0.1f)).Value();

            canvas.DrawRoundRect(placeRect, paint);

            canvas.ClipRoundRect(rect, antialias: true);
        }
    }
}
