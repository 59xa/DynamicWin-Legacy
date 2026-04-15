using DynamicWin.Main;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.Utils;
using SkiaSharp;
using System;

namespace DynamicWin.UI.Widgets.Big
{
    class RegisterPomodoroWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => false;
        public string WidgetName => "Pomodoro";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new PomodoroWidget(parent, position, alignment);
        }
    }

    public class PomodoroWidget : WidgetBase
    {
        private DWText timeText;
        private DWText stateText;
        private DWImageButton actionButton;
        private float progress = 0f;

        public PomodoroWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            stateText = new DWText(this, "Focus Mode", new Vec2(25, 25), UIAlignment.TopLeft)
            {
                TextSize = 14,
                Font = Resources.Res.SFProBold,
                Color = Theme.TextSecond
            };
            AddLocalObject(stateText);

            timeText = new DWText(this, "25:00", new Vec2(25, 45), UIAlignment.TopLeft)
            {
                TextSize = 38,
                Font = Resources.Res.SFProBold,
                Color = Theme.TextMain
            };
            AddLocalObject(timeText);

            actionButton = new DWImageButton(this, Resources.Res.Play, new Vec2(-25, 0), new Vec2(35, 35), () =>
            {
                if (PomodoroService.Instance.CurrentState == PomodoroService.PomodoroState.Idle)
                    PomodoroService.Instance.StartWork();
                else
                    PomodoroService.Instance.Toggle();
            }, alignment: UIAlignment.MiddleRight);
            AddLocalObject(actionButton);

            PomodoroService.Instance.OnTick += (rem, tot) => {
                progress = 1f - ((float)rem / tot);
                TimeSpan t = TimeSpan.FromSeconds(rem);
                timeText.SilentSetText(string.Format("{0:D2}:{1:D2}", t.Minutes, t.Seconds));
            };

            PomodoroService.Instance.OnStateChanged += (state) => {
                stateText.SetText(state switch {
                    PomodoroService.PomodoroState.Working => "Concentrating",
                    PomodoroService.PomodoroState.ShortBreak => "Quick Break",
                    PomodoroService.PomodoroState.LongBreak => "Resting",
                    _ => "Focus Mode"
                });
            };
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            actionButton.Image.Image = PomodoroService.Instance.CurrentState != PomodoroService.PomodoroState.Idle ? Resources.Res.Pause : Resources.Res.Play;
        }

        public override void DrawWidget(SKCanvas canvas)
        {
            var rect = GetRect();
            var paint = GetPaint();
            
            // Background
            paint.Color = GetColor(Theme.WidgetBackground).Value();
            canvas.DrawRoundRect(rect, paint);

            // Circular Progress (Subtle Track)
            float size = 80f;
            var circleRect = new SKRect(rect.Rect.Right - size - 70, rect.Rect.MidY - size/2, rect.Rect.Right - 70, rect.Rect.MidY + size/2);
            
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 6f;
            paint.Color = SKColors.White.WithAlpha(20);
            canvas.DrawCircle(circleRect.MidX, circleRect.MidY, size/2, paint);

            // Progress Arc (Dynamic Color based on state)
            paint.Color = PomodoroService.Instance.CurrentState == PomodoroService.PomodoroState.Working 
                ? SKColors.Tomato 
                : SKColors.DeepSkyBlue;
            
            paint.StrokeCap = SKStrokeCap.Round;
            canvas.DrawArc(circleRect, -90, progress * 360f, false, paint);
        }
    }
}
