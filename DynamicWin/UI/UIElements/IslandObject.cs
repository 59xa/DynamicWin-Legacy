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

        // Island-only tuning
        float islandCornerRadius = 65f;
        float islandSquircleStrength = 1.16f;

        // Notch-only tuning
        float notchCornerRadius = 80f;
        float notchSquircleStrength = 1.08f;

        // Target size for the notch curve
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

        // This remains the target settings, but we use _morphT for the actual visual state
        public IslandMode mode = Settings.IslandMode;

        // 0.0 = full island, 1.0 = full notch
        private float _morphT = 0f;

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

            // Initialise morph state based on startup setting
            _morphT = Settings.IslandMode == IslandMode.Notch ? 1f : 0f;
        }

        public override bool WantsRealtimeUpdate
        {
            get
            {
                float targetMorph = (Settings.IslandMode == IslandMode.Notch) ? 1f : 0f;
                float targetSquircleState = (_morphT > 0.5f)
                    ? 1f
                    : (IsHovering ? 1f : (Size.Y > 20f ? 1f : 0f));
                float targetY = Mathf.Lerp((mode == IslandMode.Island ? 7.5f : 15f), -2.5f, _morphT);
                float targetShadowStrength = IsHovering ? 0.75f : 0.25f;
                float targetShadowSize = IsHovering ? 35f : 7.5f;

                return Math.Abs(_morphT - targetMorph) > 0.001f ||
                       Math.Abs(cornerSquircleT - targetSquircleState) > 0.001f ||
                       Math.Abs(LocalPosition.Y - targetY) > 0.1f ||
                       Math.Abs(dropShadowStrength - targetShadowStrength) > 0.001f ||
                       Math.Abs(dropShadowSize - targetShadowSize) > 0.1f ||
                       Math.Abs(Size.X - currSize.X) > 0.25f ||
                       Math.Abs(Size.Y - currSize.Y) > 0.25f;
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Determine target mode and animate MorphT
            mode = Settings.IslandMode;
            float targetMorph = (mode == IslandMode.Notch) ? 1f : 0f;

            // Animate morphT (6f for snappy but visible transition)
            _morphT = Mathf.Lerp(_morphT, targetMorph, 6f * deltaTime);

            // Calculate blended parameters based on MorphT
            float currentSquircleStrength = Mathf.Lerp(islandSquircleStrength, notchSquircleStrength, _morphT);

            // Squircle animation logic
            // If it's mostly notch (_morphT > 0.5), force squircle
            // Use hover state logic otherwise
            float targetSquircleState = (_morphT > 0.5f)
                ? 1f
                : (IsHovering ? 1f : (Size.Y > 20f ? 1f : 0f));

            cornerSquircleT = Mathf.Lerp(cornerSquircleT, targetSquircleState, 12f * deltaTime);

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

                // Animate position
                // IslandMode.Island sits at 15f or 7.5f while IslandMode.Notch sits at -2.5f
                // We interpolate based on _morphT
                float targetY = Mathf.Lerp((mode == IslandMode.Island ? 7.5f : 15f), -2.5f, _morphT);
                topOffset = Mathf.Lerp(topOffset, targetY, 15f * deltaTime);
                LocalPosition.Y = topOffset;
            }
            else
            {
                scaleSecondOrder.SetValues(secondOrderValuesContract[0], secondOrderValuesContract[1], secondOrderValuesContract[2]);
                Size = scaleSecondOrder.Update(deltaTime, new Vec2(500, 15));
                LocalPosition.Y = Mathf.Lerp(LocalPosition.Y, -Size.Y / 1.5f, 25f * deltaTime);
            }

            MainForm.Instance.Opacity = hidden ? 0.85f : 1f;

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

            // Pass the interpolated _morphT instead of the boolean mode
            var borderPath = CreateDynamicIslandPath(
                borderRect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                _morphT
            );

            canvas.DrawPath(borderPath, borderPaint);
            paint.ImageFilter = null;

            var rect = GetRect();
            var islandPath = CreateDynamicIslandPath(
                rect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                _morphT
            );

            canvas.DrawPath(islandPath, paint);
        }

        /// <summary>
        /// Generates the path, smoothly morphing between Island (morphT=0) and Notch (morphT=1),
        /// while preserving the logic that shrinks the flare if the island height is small
        /// </summary>
        SKPath CreateDynamicIslandPath(SKRect r, float radius, float t, float morphT)
        {
            var path = new SKPath();
            path.FillType = SKPathFillType.Winding;

            float x0 = r.Left;
            float x1 = r.Right;
            float y0 = r.Top;
            float y1 = r.Bottom;

            // Define the Morph Thresholds
            // 0.0 -> 0.85: moving up, shrinking TOP corners to 0 radius
            // 0.85 -> 1.0: touching top, growing flares
            const float MorphThreshold = 0.85f;

            // Interpolate global radius settings
            float effectiveRadius = Mathf.Lerp(islandCornerRadius, notchCornerRadius, morphT);
            float maxRadius = Math.Min(r.Width, r.Height) * 0.5f;
            radius = Math.Min(effectiveRadius, maxRadius - 0.01f);

            // Calculate specific radii for Morphing
            float bottomRadius = radius;

            // Top radius shrinks to 0 as we approach the notch threshold
            // map 0..0.85 to 1..0
            float topRadiusMorph = Math.Clamp(1f - (morphT / MorphThreshold), 0f, 1f);
            float topRadius = radius * topRadiusMorph;

            // Squircle math
            const float kappa = 0.55228475f;
            float easedT = t * t * (3f - 2f * t);

            float targetSquash = Mathf.Lerp(islandSquircleStrength, notchSquircleStrength, morphT);
            float squash = Mathf.Lerp(1f, targetSquash, easedT);

            float bCtrl = bottomRadius * kappa * squash;
            float tCtrl = topRadius * kappa * squash;

            // Check if we are physically morphing into the flare shape
            bool drawFlares = morphT > MorphThreshold;

            if (drawFlares)
            {
                // Notch mode (IslandMode.Notch)

                // Calculate how much of the animation is complete (0.0 to 1.0)
                float animProgress = (morphT - MorphThreshold) / (1f - MorphThreshold);

                // How much vertical space we actually have for the notch
                float availableFlareHeight = r.Height * 0.5f;

                // Calculate flareT (0.0 to 1.0)
                // We combine the animation progress with the physical size constraint
                // If the box is tiny, availableFlareHeight / NotchFlareSize will be < 1.0, limiting the flare
                float sizeRatio = Math.Clamp(availableFlareHeight / NotchFlareSize, 0f, 1f);
                float flareT = sizeRatio * animProgress;

                // Ease so small sizes shrink faster
                flareT = flareT * flareT;

                // Final flare size
                float currentFlareSize = NotchFlareSize * flareT;

                // If the island is very short, the bottom radius must also shrink so it doesn't fight the flare
                float availableHeight = r.Height - currentFlareSize;
                float effectiveBottomRadius = Math.Min(radius, availableHeight);
                if (effectiveBottomRadius < 0) effectiveBottomRadius = 0;

                float bottomCtrl = effectiveBottomRadius * kappa * squash;

                // Bottom-left corner start
                path.MoveTo(x0, y1 - effectiveBottomRadius);

                // Bottom-left corner curve
                path.CubicTo(
                    x0, y1 - effectiveBottomRadius + bottomCtrl,
                    x0 + effectiveBottomRadius - bottomCtrl, y1,
                    x0 + effectiveBottomRadius, y1
                );

                // Bottom edge
                path.LineTo(x1 - effectiveBottomRadius, y1);

                // Bottom-right corner curve
                path.CubicTo(
                    x1 - effectiveBottomRadius + bottomCtrl, y1,
                    x1, y1 - effectiveBottomRadius + bottomCtrl,
                    x1, y1 - effectiveBottomRadius
                );

                // Line right side to flare start
                path.LineTo(x1, y0 + currentFlareSize);

                // Top-right outward flare
                if (currentFlareSize > 0.1f)
                {
                    path.ArcTo(
                        SKRect.Create(x1, y0, currentFlareSize * 2, currentFlareSize * 2),
                        180, 90, false
                    );
                }
                else
                {
                    path.LineTo(x1, y0);
                }

                // Top edge (invisible/offscreen)
                if (currentFlareSize > 0.1f)
                {
                    path.LineTo(x0 + currentFlareSize * 2, y0);

                    // Top-left outward flare
                    path.ArcTo(
                        SKRect.Create(x0 - currentFlareSize * 2, y0, currentFlareSize * 2, currentFlareSize * 2),
                        270, 90, false
                    );
                }
                else
                {
                    path.LineTo(x0, y0);
                }

                // Close the shape (line down to left side)
                path.LineTo(x0, y1 - effectiveBottomRadius);
            }
            else
            {
                // Island mode (IslandMode.Island)

                path.MoveTo(x0 + topRadius, y0);

                path.LineTo(x1 - topRadius, y0);
                path.CubicTo(x1 - topRadius + tCtrl, y0, x1, y0 + topRadius - tCtrl, x1, y0 + topRadius);

                path.LineTo(x1, y1 - bottomRadius);
                path.CubicTo(x1, y1 - bottomRadius + bCtrl, x1 - bottomRadius + bCtrl, y1, x1 - bottomRadius, y1);

                path.LineTo(x0 + bottomRadius, y1);
                path.CubicTo(x0 + bottomRadius - bCtrl, y1, x0, y1 - bottomRadius + bCtrl, x0, y1 - bottomRadius);

                path.LineTo(x0, y0 + topRadius);
                path.CubicTo(x0, y0 + topRadius - tCtrl, x0 + topRadius - tCtrl, y0, x0 + topRadius, y0);
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
        /// <returns>The path of the IslandObject through <see cref="CreateDynamicIslandPath(SKRect, float, float, float)"/></returns>
        public SKPath GetIslandPath()
        {
            var rect = GetRect();
            return CreateDynamicIslandPath(
                rect.Rect,
                BaseCornerRadius,
                cornerSquircleT,
                _morphT
            );
        }
    }
}
