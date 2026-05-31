using DynamicWin.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.UI.UIElements
{
    public class DWText : UIObject
    {
        private string text = "";
        public string Text { get { return text; } set { SetText(value); } }

        private float textSize = 24;
        public float TextSize
        {
            get => textSize;
            set
            {
                if (NearlyEqual(textSize, value)) return;

                textSize = value;
                InvalidateTextCache();
            }
        }

        private Vec2 textBounds = Vec2.zero;

        private SKTypeface font;
        public SKTypeface Font
        {
            get => font;
            set
            {
                if (ReferenceEquals(font, value)) return;

                font = value;
                InvalidateTextCache();
            }
        }

        private SKTextBlob? cachedBlob;
        private SKFont? cachedFont;
        private Vec2 drawAlignmentSize = Vec2.one;
        private bool textCacheDirty = true;

        public Vec2 TextBounds
        {
            get
            {
                if (textBounds == null) return Vec2.zero;
                return textBounds;
            }
        }

        public DWText(UIObject? parent, string text, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, Vec2.zero, alignment)
        {
            this.text = text;
            Color = Theme.TextMain;
            font = Resources.Res.SFProRegular;
            UseGpuCaching = true;
        }

        public override void Draw(SKCanvas canvas)
        {
            EnsureTextCache();

            using var paint = GetPaint();
            paint.Color = Color.Value();
            paint.TextSize = textSize;
            paint.Typeface = font;

            if (cachedBlob != null)
            {
                var drawPosition = GetScreenPosFromRawPosition(RawPosition, drawAlignmentSize) + LocalPosition;
                canvas.DrawText(cachedBlob, drawPosition.X, drawPosition.Y, paint);
            }

            //canvas.DrawRoundRect(GetRect(), paint);
        }

        public Vec2 GetBoundsForString(string text)
        {
            using var font = new SKFont(Font, textSize);
            using var blob = SKTextBlob.Create(text, font);

            return blob == null ? Vec2.zero : new Vec2(blob.Bounds.Width, blob.Bounds.Height);
        }

        private void EnsureTextCache()
        {
            if (!textCacheDirty) return;

            cachedBlob?.Dispose();
            cachedFont?.Dispose();

            cachedFont = new SKFont(font, textSize);
            cachedBlob = SKTextBlob.Create(text ?? string.Empty, cachedFont);

            using var measurePaint = GetPaint();
            measurePaint.TextSize = textSize;
            measurePaint.Typeface = font;

            var fontMetrics = measurePaint.FontMetrics;
            drawAlignmentSize = new Vec2(
                measurePaint.MeasureText(text ?? string.Empty),
                fontMetrics.Descent + fontMetrics.Ascent
            );

            if (cachedBlob != null)
            {
                textBounds = new Vec2(cachedBlob.Bounds.Width, cachedBlob.Bounds.Height);
                Size = textBounds;
            }
            else
            {
                textBounds = Vec2.zero;
                Size = Vec2.one;
            }

            textCacheDirty = false;
        }

        private void InvalidateTextCache()
        {
            textCacheDirty = true;
            MarkGpuDirty();
        }

        Animator changeTextAnim;

        public void SilentSetText(string text)
        {
            if (this.text == text) return;

            this.text = text;
            InvalidateTextCache();
        }

        public void SetText(string text)
        {
            if (this.text == text) return;
            if (changeTextAnim != null && changeTextAnim.IsRunning) return;

            float ogTextSize = textSize;

            changeTextAnim = new Animator(350, 1);

            changeTextAnim.onAnimationUpdate += (x) =>
            {
                if(x <= 0.5f)
                {
                    float t = Easings.EaseInQuint(x * 2);

                    TextSize = Mathf.Lerp(ogTextSize, ogTextSize / 1.5f, t);
                    localBlurAmount = Mathf.Lerp(0, 10, t);
                    Alpha = Mathf.Lerp(1, 0, x);
                }
                else
                {
                    SilentSetText(text);

                    float t = Easings.EaseOutQuint((x - 0.5f) * 2);

                    TextSize = Mathf.Lerp(ogTextSize / 2.5f, ogTextSize, t);
                    localBlurAmount = Mathf.Lerp(10, 0, t);
                    Alpha = Mathf.Lerp(0, 1, x);
                }
            };

            AddLocalObject(changeTextAnim);
            changeTextAnim.Start();
            changeTextAnim.onAnimationEnd += () =>
            {
                SilentSetText(text);
                TextSize = ogTextSize;
                DestroyLocalObject(changeTextAnim);
            };
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            cachedBlob?.Dispose();
            cachedBlob = null;
            cachedFont?.Dispose();
            cachedFont = null;
        }

        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";
        }
    }
}
