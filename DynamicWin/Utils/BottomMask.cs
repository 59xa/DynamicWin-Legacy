using DynamicWin.UI;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.Utils
{
    internal class BottomMask : UIObject
    {
        private UIObject target;
        public float alpha = 0.7f;               // opacity of mask
        public float shadowStrength = 10f;       // 0 = no shadow
        public float padding = 10f;              // extra space around target
        public Col shadowColor = new Col(0, 0, 0); // default black shadow

        public BottomMask(UIObject? parent, UIObject targetElement, Col? shadowCol = null)
            : base(parent, Vec2.zero, Vec2.zero) // ignore alignment
        {
            target = targetElement;
            roundRadius = 50;
            Color = Theme.IslandBackground;
            blurAmount = 15;

            if (shadowCol != null)
                shadowColor = shadowCol;
        }

        public override void Update(float deltaTime)
        {
            // No need to update LocalPosition or Size; we draw based on target's screen position
        }

        public override void Draw(SKCanvas canvas)
        {
            if (target == null) return;

            // Use target.Position so drawing uses the same transformed coordinate space
            Vec2 screen = target.Position - new Vec2(padding, padding);
            Vec2 size = target.Size + new Vec2(padding * 2, padding * 2);

            // Draw shadow
            if (shadowStrength > 0)
            {
                using var shadowPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = shadowColor.Override(a: alpha).Value(), // use custom shadow color
                    ImageFilter = SKImageFilter.CreateBlur(shadowStrength, shadowStrength),
                    Style = SKPaintStyle.Fill
                };

                var shadowRect = new SKRoundRect(SKRect.Create(screen.X, screen.Y, size.X, size.Y), roundRadius);
                canvas.DrawRoundRect(shadowRect, shadowPaint);
            }

            // Draw main mask
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = Color.Override(a: alpha).Value(),
                Style = SKPaintStyle.Fill
            };

            if (blurAmount > 0)
                paint.ImageFilter = SKImageFilter.CreateBlur(blurAmount, blurAmount);

            var rect = new SKRoundRect(SKRect.Create(screen.X, screen.Y, size.X, size.Y), roundRadius);
            canvas.DrawRoundRect(rect, paint);
        }
    }
}