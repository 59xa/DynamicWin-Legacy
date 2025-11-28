using DynamicWin.Main;
using DynamicWin.Utils;
using SkiaSharp;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DynamicWin.UI.Widgets
{
    public class WidgetBase : UIObject
    {
        public bool isEditMode = false;

        protected bool isSmallWidget = false;
        public bool IsSmallWidget { get { return isSmallWidget; } }

        //DWText widgetName;

        public WidgetBase(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, Vec2.zero, alignment)
        {
            Size = GetWidgetSize();

            var objs = InitializeWidget();
            objs.ForEach(obj => AddLocalObject(obj));

            roundRadius = 15f;
        }

        public Vec2 GetWidgetSize() { return new Vec2(GetWidgetWidth(), GetWidgetHeight()); }

        protected virtual float GetWidgetHeight() { return 100; }
        protected virtual float GetWidgetWidth() { return 200; }

        public List<UIObject> InitializeWidget()
        {
            return new List<UIObject>();
        }

        public Action onEditRemoveWidget;
        public Action onEditMoveWidgetLeft;
        public Action onEditMoveWidgetRight;

        public override ContextMenu? GetContextMenu()
        {
            if (!isEditMode) return null;

            var ctx = new System.Windows.Controls.ContextMenu();

            MenuItem remove = new MenuItem() { Header = "Remove" };
            remove.Click += (x, y) => onEditRemoveWidget?.Invoke();

            MenuItem pL = new MenuItem() { Header = "<- Push Left" };
            pL.Click += (x, y) => onEditMoveWidgetLeft?.Invoke();

            MenuItem pR = new MenuItem() { Header = "Push Right ->" };
            pR.Click += (x, y) => onEditMoveWidgetRight?.Invoke();
            
            ctx.Items.Add(remove);
            ctx.Items.Add(pL);
            ctx.Items.Add(pR);

            return ctx;
        }

        float hoverProgress = 0f;

        public override void Draw(SKCanvas canvas)
        {
            Size = GetWidgetSize();

            hoverProgress = Mathf.Lerp(hoverProgress, IsHovering ? 1f : 0f, 10f * RendererMain.Instance.DeltaTime);

            if(hoverProgress > 0.025f)
            {
                var paint = GetPaint();
                paint.ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 0, hoverProgress * 10, hoverProgress * 10, Theme.WidgetBackground.Override(a: hoverProgress / 10).Value());

                // Save the canvas state for the hover transform and clip
                int saveCount = canvas.Save();

                var p = Position + Size / 2;
                canvas.Scale(1 + hoverProgress / 60, 1 + hoverProgress / 60, p.X, p.Y);

                // Use a clip to exclude the widget rect and draw shadow into difference region
                int clipSave = canvas.Save();
                canvas.ClipRoundRect(GetRect(), SKClipOperation.Difference, antialias: true);
                canvas.DrawRoundRect(GetRect(), paint);
                canvas.RestoreToCount(clipSave);

                // Restore the hover transform state
                canvas.RestoreToCount(saveCount);
            }

            // Draw the widget visuals and children
            DrawWidget(canvas);

            if (isEditMode)
            {
                var paint = GetPaint();

                paint.IsStroke = true;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.StrokeWidth = 2f;

                float expand = 10;
                var brect = SKRect.Create(Position.X - expand / 2, Position.Y - expand / 2, Size.X + expand, Size.Y + expand);
                var broundRect = new SKRoundRect(brect, roundRadius);

                int noClip = canvas.Save();

                paint.Color = SKColors.DimGray;
                canvas.DrawRoundRect(broundRect, paint);

                canvas.RestoreToCount(noClip);
            }
        }

        public virtual void DrawWidget(SKCanvas canvas) { }

        // Background task support when widget changes active state
        // Widgets can override RunBackgroundAsync to perform off-UI-thread work while inactive
        CancellationTokenSource? _backgroundCts;
        Task? _backgroundTask;

        protected override void OnActiveChanged(bool isEnabled)
        {
            base.OnActiveChanged(isEnabled);

            if (!isEnabled)
            {
                // Start background work when requested to become inactive
                if (_backgroundCts != null) return;
                _backgroundCts = new CancellationTokenSource();
                var token = _backgroundCts.Token;
                _backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunBackgroundAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* expected on cancellation - swallow */ }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[WidgetBase] Background exception: " + ex);
                    }
                    finally
                    {
                        _backgroundCts = null;
                        _backgroundTask = null;
                    }
                }, token);
            }
            else
            {
                // Stop background work when becoming active (do not block UI)
                if (_backgroundCts != null)
                {
                    try
                    {
                        _backgroundCts.Cancel();
                    }
                    catch { }

                    // Observe faults without blocking UI thread
                    _backgroundTask?.ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            System.Diagnostics.Debug.WriteLine("[WidgetBase] Background fault: " + t.Exception);
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }

        /// <summary>
        /// Override to run background work when widget becomes inactive
        /// IMPORTANT: Do not touch UI objects directly from this method. Marshal to UI thread via BeginInvokeUI(...) OR update thread-safe fields and read them on UI thread
        /// Default implementation is a simple no-op loop
        /// </summary>
        protected virtual async Task RunBackgroundAsync(CancellationToken token)
        {
            // Default: nothing heavy, but keep an awaitable loop so derived classes can override without re-implementing the loop
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* swallow */ }
        }

        /// <summary>
        /// Helper for background threads to marshal non-blocking UI updates
        /// Prefer this over Dispatcher.Invoke to avoid deadlocks/hitches
        /// </summary>
        protected void BeginInvokeUI(Action action)
        {
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null && !disp.CheckAccess())
                    disp.BeginInvoke(action, DispatcherPriority.Normal);
                else
                    action();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WidgetBase] BeginInvokeUI failed: " + ex);
            }
        }
    }
}
