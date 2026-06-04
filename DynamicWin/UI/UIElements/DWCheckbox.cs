using DynamicWin.Resources;
using DynamicWin.Utils;
using SkiaSharp;

namespace DynamicWin.UI.UIElements
{
    internal class DWCheckbox : UIObject
    {
        private const float PreferredBoxSize = 25f;
        private const float PreferredRowHeight = 32f;
        private const float LabelGap = 15f;

        private readonly DWText label;

        private bool isChecked;
        private float hoverAlpha;
        private float checkAlpha;
        private float pressScale = 1f;
        private float boxSize = PreferredBoxSize;
        private float lastLayoutWidth = float.NaN;
        private float lastLayoutHeight = float.NaN;

        public Action? clickCallback;

        public bool IsChecked
        {
            get => isChecked;
            set => SetChecked(value, animate: false);
        }

        public DWCheckbox(
            UIObject? parent,
            string buttonText,
            Vec2 position,
            Vec2 size,
            Action? clickCallback,
            UIAlignment alignment = UIAlignment.TopCenter)
            : base(parent, position, new Vec2(size.X, Math.Max(size.Y, PreferredRowHeight)), alignment)
        {
            this.clickCallback = clickCallback;

            UseGpuCaching = false;
            Color = Theme.IconColor.Override(a: 0.16f);
            roundRadius = PreferredBoxSize / 2f;

            label = new DWText(this, buttonText, new Vec2(PreferredBoxSize + LabelGap, 0), UIAlignment.MiddleLeft)
            {
                Anchor = new Vec2(0, 0.5f),
                Color = Theme.TextSecond,
                Font = Res.SFProRegular,
                TextSize = PreferredBoxSize / 1.5f
            };
            AddLocalObject(label);

            UpdateLayoutMetrics(force: true);
            SetChecked(false, animate: false);
        }

        public override bool WantsRealtimeUpdate
        {
            get
            {
                float targetHover = IsHovering ? 1f : 0f;
                float targetCheck = isChecked ? 1f : 0f;
                float targetScale = IsMouseDown ? 0.94f : 1f;

                return Math.Abs(hoverAlpha - targetHover) > 0.01f
                    || Math.Abs(checkAlpha - targetCheck) > 0.01f
                    || Math.Abs(pressScale - targetScale) > 0.002f
                    || IsMouseDown;
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            UpdateLayoutMetrics(force: false);

            hoverAlpha = Smooth(hoverAlpha, IsHovering ? 1f : 0f, 12f, deltaTime, 0.01f);
            checkAlpha = Smooth(checkAlpha, isChecked ? 1f : 0f, 18f, deltaTime, 0.01f);
            pressScale = Smooth(pressScale, IsMouseDown ? 0.94f : 1f, 18f, deltaTime, 0.002f);
        }

        public override void Draw(SKCanvas canvas)
        {
            UpdateLayoutMetrics(force: false);

            var boxRect = GetBoxRect();
            int save = canvas.Save();
            canvas.Scale(pressScale, pressScale, boxRect.MidX, boxRect.MidY);

            using (var paint = GetPaint())
            {
                paint.Color = Theme.IconColor.Override(a: 0.16f + hoverAlpha * 0.10f).Value();
                canvas.DrawRoundRect(new SKRoundRect(boxRect, boxSize / 2f), paint);
            }

            if (checkAlpha > 0.01f && Res.Check != null)
            {
                using var iconPaint = GetPaint();
                using var iconFilter = SKColorFilter.CreateBlendMode(
                    Theme.IconColor.Override(a: checkAlpha).Value(),
                    SKBlendMode.SrcIn);

                iconPaint.Color = Theme.IconColor.Override(a: checkAlpha).Value();
                iconPaint.ColorFilter = iconFilter;

                float inset = boxSize * 0.27f;
                var iconRect = SKRect.Create(
                    boxRect.Left + inset,
                    boxRect.Top + inset,
                    boxSize - inset * 2f,
                    boxSize - inset * 2f);

                canvas.DrawBitmap(Res.Check, iconRect, iconPaint);
            }

            canvas.RestoreToCount(save);
        }

        public override void OnMouseUp()
        {
            SetChecked(!isChecked, animate: true);
            clickCallback?.Invoke();
        }

        private void SetChecked(bool value, bool animate)
        {
            if (isChecked == value)
            {
                if (!animate)
                    checkAlpha = value ? 1f : 0f;

                return;
            }

            isChecked = value;

            if (!animate)
                checkAlpha = value ? 1f : 0f;
        }

        private SKRect GetBoxRect()
        {
            return SKRect.Create(
                Position.X,
                Position.Y + (Size.Y - boxSize) / 2f,
                boxSize,
                boxSize);
        }

        private void UpdateLayoutMetrics(bool force)
        {
            if (!force
                && Math.Abs(lastLayoutWidth - Size.X) <= 0.001f
                && Math.Abs(lastLayoutHeight - Size.Y) <= 0.001f)
            {
                return;
            }

            lastLayoutWidth = Size.X;
            lastLayoutHeight = Size.Y;
            boxSize = Math.Min(PreferredBoxSize, Math.Max(1f, Math.Min(Size.X, Size.Y)));
            roundRadius = boxSize / 2f;

            label.Position = new Vec2(boxSize + LabelGap, 0);
            label.LocalPosition = Vec2.zero;
            label.TextSize = boxSize / 1.5f;
            label.Size = label.GetBoundsForString(label.Text);
        }

        private static float Smooth(float current, float target, float speed, float deltaTime, float epsilon)
        {
            if (Math.Abs(current - target) <= epsilon)
                return target;

            return Mathf.Lerp(current, target, speed * deltaTime);
        }
    }
}
