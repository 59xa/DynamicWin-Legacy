using DynamicWin.Utils;
using SkiaSharp;
using System;

namespace DynamicWin.UI.UIElements.Custom
{
    /// <summary>
    /// Successor to DWProgressBar: exposes clamped Value (0..1), smoothing, colors and immediate-set API.
    /// Designed to be reusable across widgets.
    /// </summary>
    public class DWProgressBarEx : UIObject
    {
        // Public target value (0..1)
        private float _value = 1f;
        public float Value
        {
            get => _value;
            set
            {
                if (IsLocked) return; // Ignore external sets when locked
                _value = Math.Clamp(value, 0f, 1f);
            }
        }

        // Smoothing factor used when animating displayed value towards Value
        public float Smoothing { get; set; } = 15f;

        // Display colors
        public Col BackgroundColor { get; set; }
        public Col ForegroundColor { get; set; }

        // Corner radius
        public float CornerRadius { get; set; } = 15f;

        // Internal displayed value (smoothed)
        private float displayedValue = 1f;

        /// <summary>
        /// When true the progress control will ignore external attempts to change Value/SetValueImmediate.
        /// Use ForceSetValue when you need to override the lock.
        /// </summary>
        public bool IsLocked { get; set; } = false;

        public DWProgressBarEx(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter,
            Col? background = null, Col? foreground = null) : base(parent, position, size, alignment)
        {
            BackgroundColor = background ?? Theme.IconColor.Override(a: 0.08f);
            ForegroundColor = foreground ?? Theme.IconColor.Override(a: 1f);

            // Initialise displayed to current value
            displayedValue = Value;
        }

        /// <summary>
        /// Immediately set displayed and target value without smoothing.
        /// This respects the IsLocked flag.
        /// </summary>
        public void SetValueImmediate(float v)
        {
            if (IsLocked) return;
            Value = v;
            displayedValue = Value;
        }

        /// <summary>
        /// Forcefully set the value even when locked.
        /// </summary>
        public void ForceSetValue(float v)
        {
            // Update target value but do not jump displayedValue so smoothing still applies.
            _value = Math.Clamp(v, 0f, 1f);
        }

        /// <summary>
        /// Forcefully set both target and displayed value immediately, ignoring the lock.
        /// Use sparingly as this will snap the visual state.
        /// </summary>
        public void ForceSetImmediate(float v)
        {
            _value = Math.Clamp(v, 0f, 1f);
            displayedValue = _value;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Smoothly interpolate displayed value towards target Value
            if (Smoothing <= 0f)
            {
                displayedValue = Value;
            }
            else
            {
                displayedValue = Mathf.Lerp(displayedValue, Value, Math.Min(1f, Smoothing * deltaTime));
            }
        }

        public override bool WantsRealtimeUpdate => Math.Abs(displayedValue - Value) > 0.001f;

        public override void Draw(SKCanvas canvas)
        {
            // Compute screen rect for the control
            var size = Size;
            var pos = RawPosition + LocalPosition;
            var screenPos = GetScreenPosFromRawPosition(pos, size);

            var totalRect = SKRect.Create(screenPos.X, screenPos.Y, size.X, size.Y);
            var fillRect = SKRect.Create(screenPos.X, screenPos.Y, size.X * displayedValue, size.Y);

            using (var p = GetPaint())
            {
                p.IsStroke = false;
                p.IsAntialias = Main.Settings.AntiAliasing;
                p.Color = GetColor(BackgroundColor).Value();
                canvas.DrawRoundRect(new SKRoundRect(totalRect, CornerRadius), p);
            }

            using (var p = GetPaint())
            {
                p.IsStroke = false;
                p.IsAntialias = Main.Settings.AntiAliasing;
                p.Color = GetColor(ForegroundColor).Value();
                canvas.DrawRoundRect(new SKRoundRect(fillRect, CornerRadius), p);
            }
        }
    }
}
