using DynamicWin.Utils;
using DynamicWin.UI.UIElements;
using SkiaSharp;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using DynamicWin.Resources;

namespace DynamicWin.UI.Widgets.Small
{
    class RegisterMediaThumbnailWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => true;

        public string WidgetName => "Media Thumbnail Display";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new MediaThumbnailWidget(parent, position, alignment);
        }
    }

    public class MediaThumbnailWidget : SmallWidgetBase
    {
        private readonly object mediaLock = new object();
        private SKBitmap? thumbnailBitmap;
        private SKBitmap? pendingBitmap;
        private SKBitmap? previousBitmap;
        private Media? pendingMedia;
        private string? currentMediaKey;
        private string? pendingMediaKey;

        private readonly MediaAnimator animator = new MediaAnimator();

        // Track whether service says there is any media at all
        private volatile bool hasMedia = false;

        // Smooth collapse/expand animation progress (0 = collapsed width 0, 1 = full square)
        private float collapseProgress = 0f;
        private Animator? collapseAnim = null;

        private DateTime lastDecodeTime = DateTime.MinValue;
        private readonly TimeSpan minDecodeInterval = TimeSpan.FromMilliseconds(500);
        private CancellationTokenSource? decodeWorkerCts = null;
        private byte[]? latestBytes = null;
        private Media? latestMedia = null;
        private bool decodeRequested = false;

        public MediaThumbnailWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;
            StartDecodeWorker();

            // Try to initialise from service cache so first show has an image
            try
            {
                var svc = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (svc != null)
                {
                    // Clone into owned SKBitmap
                    try
                    {
                        using var tmp = SKImage.FromBitmap(svc);
                        var bmp = SKBitmap.FromImage(tmp);
                        lock (mediaLock)
                        {
                            // Queue as pending so animator will run
                            if (thumbnailBitmap == null)
                            {
                                pendingBitmap = bmp;
                                pendingMedia = null;
                                pendingMediaKey = null;
                                hasMedia = true;
                                collapseProgress = 1f;
                            }
                            else
                            {
                                try { thumbnailBitmap.Dispose(); } catch { }
                                thumbnailBitmap = bmp;
                                hasMedia = true;
                                collapseProgress = 1f;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // Default collapsed
                    collapseProgress = 0f;
                }
            }
            catch { }
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            lock (mediaLock)
            {
                latestBytes = e.ThumbnailBytes;
                latestMedia = e.Media;
                decodeRequested = true;
            }
        }

        private void StartDecodeWorker()
        {
            decodeWorkerCts = new CancellationTokenSource();
            var cts = decodeWorkerCts;
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    bool shouldDecode = false;
                    byte[]? bytes = null;
                    Media? media = null;
                    bool prevHas = hasMedia;

                    lock (mediaLock)
                    {
                        if (decodeRequested)
                        {
                            shouldDecode = true;
                            bytes = latestBytes;
                            media = latestMedia;
                            decodeRequested = false;
                        }
                    }

                    if (!shouldDecode)
                    {
                        await Task.Delay(50, cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    // Debounce: wait for minDecodeInterval, coalescing further changes
                    var debounceStart = DateTime.UtcNow;
                    while ((DateTime.UtcNow - debounceStart) < minDecodeInterval)
                    {
                        await Task.Delay(50, cts.Token).ConfigureAwait(false);
                        lock (mediaLock)
                        {
                            if (decodeRequested)
                            {
                                // New change arrived, restart debounce
                                bytes = latestBytes;
                                media = latestMedia;
                                decodeRequested = false;
                                debounceStart = DateTime.UtcNow;
                            }
                        }
                    }

                    // If cancelled, exit
                    if (cts.IsCancellationRequested) break;

                    // If no media, clear all
                    if (media == null && (bytes == null || bytes.Length == 0))
                    {
                        lock (mediaLock)
                        {
                            hasMedia = false;
                            currentMediaKey = null;
                            if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                            if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                            if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                            pendingMedia = null;
                            pendingMediaKey = null;
                        }
                        if (prevHas != hasMedia)
                        {
                            BeginInvokeUI(() => StartCollapseOrExpand(false));
                        }
                        continue;
                    }

                    // Decode thumbnail
                    SKBitmap? newBmp = null;
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            using var ms = new SKMemoryStream(bytes);
                            newBmp = SKBitmap.Decode(ms);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("MediaThumbnailWidget: decode failed: " + ex.Message);
                            newBmp = null;
                        }
                    }

                    lock (mediaLock)
                    {
                        string key = (media == null) ? string.Empty : $"{media.Title ?? ""}|{media.Artist ?? ""}|{(bytes?.Length ?? 0)}";
                        if (key == currentMediaKey || key == pendingMediaKey)
                        {
                            if (newBmp != null)
                            {
                                try { newBmp.Dispose(); } catch { }
                            }
                        }
                        else
                        {
                            if (thumbnailBitmap == null && newBmp != null)
                            {
                                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                                pendingBitmap = newBmp;
                                pendingMedia = media;
                                pendingMediaKey = key;
                                hasMedia = true;
                            }
                            else
                            {
                                if (newBmp != null)
                                {
                                    if (pendingBitmap != null)
                                    {
                                        try { pendingBitmap.Dispose(); } catch { }
                                        pendingBitmap = null;
                                        pendingMediaKey = null;
                                        pendingMedia = null;
                                    }
                                    pendingBitmap = newBmp;
                                    pendingMedia = media;
                                    pendingMediaKey = key;
                                    hasMedia = true;
                                }
                                if (newBmp == null && key != currentMediaKey && pendingMediaKey == null)
                                {
                                    currentMediaKey = key;
                                    if (media == null)
                                    {
                                        hasMedia = false;
                                        if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                                        if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                        if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                                    }
                                }
                            }
                        }
                    }
                    lastDecodeTime = DateTime.UtcNow;
                    if (prevHas != hasMedia)
                    {
                        BeginInvokeUI(() => StartCollapseOrExpand(hasMedia));
                    }
                }
            }, decodeWorkerCts.Token);
        }

        private void StartCollapseOrExpand(bool expand)
        {
            try
            {
                // Stop existing animator if present
                if (collapseAnim != null)
                {
                    try { collapseAnim.Stop(false); } catch { }
                    try { DestroyLocalObject(collapseAnim); } catch { }
                    collapseAnim = null;
                }

                // If no change needed, early set and return
                if (expand && collapseProgress >= 0.999f) { collapseProgress = 1f; return; }
                if (!expand && collapseProgress <= 0.001f) { collapseProgress = 0f; return; }

                collapseAnim = new Animator(300, 1);
                bool expanding = expand;

                collapseAnim.onAnimationUpdate += (t) =>
                {
                    float e = Easings.EaseOutCubic(t);
                    collapseProgress = expanding ? e : 1f - e;
                };

                collapseAnim.onAnimationEnd += () =>
                {
                    collapseProgress = expanding ? 1f : 0f;

                    // When fully collapsed, keep bitmaps disposed (already cleared by background handler)
                    if (!expanding)
                    {
                        lock (mediaLock)
                        {
                            if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                            if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                            if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                        }
                    }

                    try { DestroyLocalObject(collapseAnim); } catch { }
                    collapseAnim = null;
                };

                AddLocalObject(collapseAnim);
                collapseAnim.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaThumbnailWidget.StartCollapseOrExpand error: " + ex.Message);
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Drive animator; check pendingBitmap under lock
            animator.Update(deltaTime, () => { lock (mediaLock) { return pendingBitmap != null; } },
                onStart: () =>
                {
                    lock (mediaLock) { previousBitmap = thumbnailBitmap; }
                },
                onMidFlip: () =>
                {
                    lock (mediaLock)
                    {
                        if (thumbnailBitmap != null)
                        {
                            try { thumbnailBitmap.Dispose(); } catch { }
                        }
                        thumbnailBitmap = pendingBitmap;
                        pendingBitmap = null;

                        currentMediaKey = pendingMediaKey;
                        pendingMediaKey = null;

                        if (pendingMedia != null)
                        {
                            // We don't display textual metadata in this widget; clear pendingMedia
                            pendingMedia = null;
                        }
                    }
                },
                onFinish: () =>
                {
                    if (previousBitmap != null)
                    {
                        try { previousBitmap.Dispose(); } catch { }
                        previousBitmap = null;
                    }
                });
        }

        public override void Draw(SKCanvas canvas)
        {
            // If fully collapsed, don't draw at all
            if (collapseProgress <= 0f) return;

            base.Draw(canvas);

            var rect = GetRect().Rect;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            float size = Math.Min(rect.Width, rect.Height);
            var thumbRect = SKRect.Create(rect.Left + (rect.Width - size) / 2f, rect.Top + (rect.Height - size) / 2f, size, size);

            SKBitmap? bmp;
            lock (mediaLock) { bmp = thumbnailBitmap; }

            var path = BuildSuperellipsePath(thumbRect, 7f, 1f);

            try
            {
                float flipScale = animator.GetFlipScale();
                bool doFlip = animator.IsFlipping;

                if (doFlip)
                {
                    int save = canvas.Save();
                    float cx = thumbRect.MidX;
                    float cy = thumbRect.MidY;
                    canvas.Translate(cx, cy);
                    canvas.Scale(flipScale, 1f);
                    var localRect = SKRect.Create(-thumbRect.Width / 2f, -thumbRect.Height / 2f, thumbRect.Width, thumbRect.Height);

                    var localPath = BuildSuperellipsePath(localRect, 7f, 1f);
                    canvas.Save();
                    canvas.ClipPath(localPath, antialias: true);

                    var paint = GetPaint();
                    paint.IsAntialias = true;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;

                    if (bmp != null)
                    {
                        canvas.DrawBitmap(bmp, localRect, paint);
                    }
                    else
                    {
                        paint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        canvas.DrawRoundRect(new SKRoundRect(localRect, localRect.Width * 0.12f), paint);
                    }

                    canvas.Restore();
                    canvas.RestoreToCount(save);

                    using (var border = GetPaint())
                    {
                        border.IsStroke = true;
                        border.StrokeWidth = 1f;
                        border.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(path, border);
                    }
                }
                else
                {
                    canvas.Save();
                    canvas.ClipPath(path, antialias: true);

                    var paint = GetPaint();
                    paint.IsAntialias = true;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;

                    if (bmp != null)
                    {
                        canvas.DrawBitmap(bmp, thumbRect, paint);
                    }
                    else
                    {
                        paint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        canvas.DrawRoundRect(new SKRoundRect(thumbRect, thumbRect.Width * 0.12f), paint);
                    }

                    canvas.Restore();

                    using (var border = GetPaint())
                    {
                        border.IsStroke = true;
                        border.StrokeWidth = 1f;
                        border.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(path, border);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaThumbnailWidget.Draw error: " + ex.Message);
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChanged; } catch { }
            decodeWorkerCts?.Cancel();
            decodeWorkerCts?.Dispose();
            decodeWorkerCts = null;
            lock (mediaLock)
            {
                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
            }
        }

        // Make this small widget square: width matches height so thumbnail is not stretched
        protected override float GetWidgetWidth()
        {
            // Smoothly interpolate width according to collapseProgress
            float full = GetWidgetHeight();
            return full * collapseProgress;
        }
    }
}
