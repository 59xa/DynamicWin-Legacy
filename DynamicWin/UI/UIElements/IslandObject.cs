using DynamicWin.Main;
using DynamicWin.UI.Menu;
using DynamicWin.Utils;
using SkiaSharp;
using System;

namespace DynamicWin.UI.UIElements
{
    public class IslandObject : UIObject
    {
        float cornerSquircleT = 0f; // 0 = round, 1 = squircle
        const float BaseCornerRadius = 80f;

        // Target size for the notch curve. 
        // 22f is standard, but will be clamped dynamically if the island is smaller
        const float NotchFlareSize = 22f;

        float sizeVelocity = 0f;

        public float topOffset = 15f;

        public SecondOrder scaleSecondOrder;

        public float[] secondOrderValuesExpand = [2.3f, 0.6f, 0.15f];
        public float[] secondOrderValuesContract = [2.8f, 0.8f, 0.1f];

        public bool hidden = false;

        public Vec2 currSize;

        public enum IslandMode { Island, Notch };
        public IslandMode mode = Settings.IslandMode;

        float dropShadowStrength = 0f;
        float dropShadowSize = 0f;

        Col borderCol = Col.Transparent;

        public IslandObject() : base(null, Vec2.zero, new Vec2(250, 50), UIAlignment.TopCenter)
        {
            currSize = Size;
            Anchor = new Vec2(0.5f, 0f);
            roundRadius = 35f;
            LocalPosition = new Vec2(0, topOffset);

            scaleSecondOrder = new SecondOrder(Size, secondOrderValuesExpand[0], secondOrderValuesExpand[1], secondOrderValuesExpand[2]);
            expandInteractionRect = 20;

            maskInToIsland = false;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            float targetSquircle =
                (IsHovering || hidden == false && Size.X > 300f)
                    ? 1f
                    : 0f;

            cornerSquircleT = Mathf.Lerp(cornerSquircleT, targetSquircle, 12f * deltaTime);

            if (!hidden)
            {
                if (IsHovering)
                {
                    scaleSecondOrder.SetValues(secondOrderValuesExpand[0], secondOrderValuesExpand[1], secondOrderValuesExpand[2]);
                    currSize = MenuManager.Instance.ActiveMenu.IslandSizeBig();
                }
                else
                {
                    scaleSecondOrder.SetValues(secondOrderValuesContract[0], secondOrderValuesContract[1], secondOrderValuesContract[2]);
                    currSize = MenuManager.Instance.ActiveMenu.IslandSize();
                }

                Size = scaleSecondOrder.Update(deltaTime, currSize);

                Vec2 delta = Size - currSize;
                float frameVelocity =
                    (float)Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) /
                    Math.Max(deltaTime, 0.0001f);

                sizeVelocity = Mathf.Lerp(
                    sizeVelocity,
                    frameVelocity,
                    10f * deltaTime
                );

                LocalPosition.Y = Mathf.Lerp(LocalPosition.Y, topOffset, 15f * deltaTime);
            }
            else
            {
                scaleSecondOrder.SetValues(secondOrderValuesContract[0], secondOrderValuesContract[1], secondOrderValuesContract[2]);
                Size = scaleSecondOrder.Update(deltaTime, new Vec2(500, 15));
                LocalPosition.Y = Mathf.Lerp(LocalPosition.Y, -Size.Y / 1.5f, 25f * deltaTime);
            }

            MainForm.Instance.Opacity = hidden ? 0.85f : 1f;
            mode = Settings.IslandMode;
            topOffset = Mathf.Lerp(topOffset, (mode == IslandMode.Island) ? 7.5f : -2.5f, 15f * deltaTime);
            dropShadowStrength = Mathf.Lerp(dropShadowStrength, IsHovering ? 0.75f : 0.25f, 10f * deltaTime);
            dropShadowSize = Mathf.Lerp(dropShadowSize, IsHovering ? 35f : 7.5f, 10f * deltaTime);
            borderCol = Col.Lerp(borderCol, MenuManager.Instance.ActiveMenu.IslandBorderColor(), 10f * deltaTime);
        }

        public override void Draw(SKCanvas canvas)
        {
            var paint = GetPaint();
            paint.IsAntialias = Settings.AntiAliasing;
            paint.Color = Theme.IslandBackground.Value();

            var borderRect = GetRect();
            borderRect.Inflate(1.25f, 1.25f);

            var borderPaint = GetPaint();
            borderPaint.IsAntialias = Settings.AntiAliasing;
            borderPaint.Color = borderCol.Override(a: borderCol.a * 0.35f).Value();
            borderPaint.IsStroke = true;
            borderPaint.StrokeWidth = 1f;

            var borderPath = CreateDynamicIslandPath(
                borderRect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                mode == IslandMode.Notch
            );

            canvas.DrawPath(borderPath, borderPaint);
            paint.ImageFilter = null;

            var rect = GetRect();
            var islandPath = CreateDynamicIslandPath(
                rect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                mode == IslandMode.Notch
            );

            canvas.DrawPath(islandPath, paint);
        }

        SKPath CreateDynamicIslandPath(SKRect r, float radius, float t, bool hasNotch)
        {
            var path = new SKPath();
            path.FillType = SKPathFillType.Winding;

            float x0 = r.Left;
            float x1 = r.Right;
            float y0 = r.Top;
            float y1 = r.Bottom;

            // Clamp radius so it doesn't invert small shapes
            float maxRadius = Math.Min(r.Width, r.Height) * 0.5f;
            radius = Math.Min(radius, maxRadius - 0.01f);

            // Squircle math
            const float kappa = 0.55228475f;
            float easedT = t * t * (3f - 2f * t);
            float squash = Mathf.Lerp(1f, 1.16f, easedT);
            float ctrl = radius * kappa * squash;

            if (hasNotch)
            {
                // How much vertical space we actually have for the notch
                float availableFlareHeight = r.Height * 0.5f;

                // Normalise 0..1 against the intended notch size
                float flareT = Math.Clamp(availableFlareHeight / NotchFlareSize, 0f, 1f);

                // Ease so small sizes shrink faster
                flareT = flareT * flareT;

                // Final flare size
                float flareSize = NotchFlareSize * flareT;

                // If the island is very short, the bottom radius must also shrink 
                // so it doesn't fight the flare
                float availableHeight = r.Height - flareSize;
                float effectiveBottomRadius = Math.Min(radius, availableHeight);
                if (effectiveBottomRadius < 0) effectiveBottomRadius = 0;

                float bottomCtrl = effectiveBottomRadius * kappa * squash;

                // Bottom-left corner
                path.MoveTo(x0, y1 - effectiveBottomRadius);
                path.CubicTo(
                    x0, y1 - effectiveBottomRadius + bottomCtrl,
                    x0 + effectiveBottomRadius - bottomCtrl, y1,
                    x0 + effectiveBottomRadius, y1
                );

                // Bottom edge
                path.LineTo(x1 - effectiveBottomRadius, y1);

                // Bottom-right corner
                path.CubicTo(
                    x1 - effectiveBottomRadius + bottomCtrl, y1,
                    x1, y1 - effectiveBottomRadius + bottomCtrl,
                    x1, y1 - effectiveBottomRadius
                );

                // Right edge
                // Connect bottom corner to the start of the top flare
                path.LineTo(x1, y0 + flareSize);

                // Top-right outward flare
                // Using square rect (flareSize * 2) to ensure 1:1 circular curvature
                path.ArcTo(
                    SKRect.Create(x1, y0, flareSize * 2, flareSize * 2),
                    180, 90, false
                );

                // Top-left outward flare
                path.ArcTo(
                    SKRect.Create(x0 - flareSize * 2, y0, flareSize * 2, flareSize * 2),
                    270, 90, false
                );

                // Left edge
                path.LineTo(x0, y1 - effectiveBottomRadius);
            }
            else
            {
                // Island mode (IslandMode.Island)
                path.MoveTo(x0, y0 + radius);
                path.CubicTo(x0, y0 + radius - ctrl, x0 + radius - ctrl, y0, x0 + radius, y0);

                path.LineTo(x1 - radius, y0);
                path.CubicTo(x1 - radius + ctrl, y0, x1, y0 + radius - ctrl, x1, y0 + radius);

                path.LineTo(x1, y1 - radius);
                path.CubicTo(x1, y1 - radius + ctrl, x1 - radius + ctrl, y1, x1 - radius, y1);

                path.LineTo(x0 + radius, y1);
                path.CubicTo(x0 + radius - ctrl, y1, x0, y1 - radius + ctrl, x0, y1 - radius);
            }

            path.Close();
            return path;
        }

        public override SKRoundRect GetInteractionRect()
        {
            var rect = SKRect.Create(Position.X, Position.Y, Size.X, Size.Y);
            if (IsHovering) rect.Inflate(expandInteractionRect + 5, expandInteractionRect + 5);
            rect.Inflate(expandInteractionRect, expandInteractionRect);
            return new SKRoundRect(rect, roundRadius);
        }

        /// <summary>
        /// Exposes the path of the dynamic island regardless of its state (island or notch)
        /// </summary>
        /// <returns>The path of the IslandObject through <see cref="CreateDynamicIslandPath(SKRect, float, float, bool)"/></returns>
        public SKPath GetIslandPath()
        {
            var rect = GetRect();
            return CreateDynamicIslandPath(
                rect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                mode == IslandMode.Notch
            );
        }
    }
}