using DynamicWin.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.UI.UIElements.Custom
{
    internal class DropFileElement : UIObject
    {
        public DropFileElement(UIObject? parent, Vec2 position, Vec2 size, string displayText = "Drop Files to Tray", int tSize = 24, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            roundRadius = 50;

            AddLocalObject(new DWText(null, displayText, Vec2.zero, UIAlignment.Center) { Font = Resources.Res.SFProBold, TextSize = tSize });
        }

        Col currentCol = Theme.Secondary;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            currentCol = Col.Lerp(currentCol, IsHovering ? Theme.Secondary * 2f : Theme.Secondary, 5f * deltaTime);
        }

        public override void Draw(SKCanvas canvas)
        {
            var paint = GetPaint();

            // Stroke path (outer border)
            var outerPath = BuildSuperellipsePath(
                GetRawRect(),
                radius: roundRadius,
                t: 1.0f
            );

            float[] intervals = { 10, 10 };
            paint.PathEffect = SKPathEffect.CreateDash(intervals, 0f);

            paint.IsStroke = true;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeWidth = 2f;

            paint.Color = GetColor(Theme.Primary).Value();
            canvas.DrawPath(outerPath, paint);

            // Fill path (inner content)
            paint.IsStroke = false;
            paint.PathEffect = null;
            paint.Color = GetColor(currentCol).Value();

            // Shrink for inner path
            var innerRect = GetRawRect();
            innerRect.Inflate(-10, -10); // Deflate for padding
            var innerPath = BuildSuperellipsePath(innerRect, radius: roundRadius - 10, t: 1.0f);
            canvas.DrawPath(innerPath, paint);
        }
    }
}
