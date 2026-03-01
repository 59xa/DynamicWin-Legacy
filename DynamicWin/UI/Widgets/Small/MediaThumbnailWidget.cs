using DynamicWin.Utils;
using DynamicWin.UI.UIElements;
using SkiaSharp;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using DynamicWin.Resources;
using DynamicWin.Main;

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
        private SKBitmap? thumbnailBitmap;           // Currently displayed (owned)
        private SKBitmap? pendingBitmap;             // Decoded and waiting to animate in (owned)
        private SKBitmap? previousBitmap;            // Previous used for disposal after animation
        private Media? pendingMedia;
        private string? currentMediaKey;
        private string? pendingMediaKey;

        private readonly MediaAnimator animator = new MediaAnimator();

        private volatile bool hasMedia = false;

        private float collapseProgress = 0f;
        private Animator? collapseAnim = null;

        // Short debounce for rapid events (avoid decode storm)
        private DateTime lastDecodeTime = DateTime.MinValue;
        private readonly TimeSpan minDecodeInterval = TimeSpan.FromMilliseconds(150);

        // Animation for thumbnail scale/dim
        private float thumbnailAnim = 1f; // 1 = playing, 0 = paused
        private const float thumbnailAnimSpeed = 8f;

        // Fingerprints to avoid unnecessary animations
        private ulong? currentBitmapFingerprint = null;
        private ulong? pendingBitmapFingerprint = null;

        public MediaThumbnailWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            // Ensure shared setting loaded
            try { RegisterSmallVisualiserWidgetSettings.SharedMediaSettings.Load(); } catch { }

            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;

            // Try to initialise from service canonical bitmap (fast path)
            try
            {
                var svcBmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (svcBmp != null)
                {
                    // Clone to owned bitmap
                    try
                    {
                        using var img = SKImage.FromBitmap(svcBmp);
                        var bmp = SKBitmap.FromImage(img);
                        lock (mediaLock)
                        {
                            thumbnailBitmap = bmp;
                            try { currentBitmapFingerprint = BitmapUtils.GetBitmapFingerprint(bmp); } catch { currentBitmapFingerprint = null; }
                            hasMedia = true;

                            // Respect shared hide-when-idle setting: if enabled and media has been paused for longer than 30 seconds, start collapsed
                            bool hideWhenIdle = false;
                            try { hideWhenIdle = RegisterSmallVisualiserWidgetSettings.SharedMediaSettings.HideMediaWhenIdle; } catch { hideWhenIdle = false; }
                            if (hideWhenIdle)
                            {
                                try
                                {
                                    bool pausedLong = MediaThumbnailService.Instance.IsPausedLongerThan(TimeSpan.FromSeconds(30));
                                    collapseProgress = pausedLong ? 0f : 1f;
                                }
                                catch { collapseProgress = 1f; }
                            }
                            else
                            {
                                collapseProgress = 1f;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    collapseProgress = 0f;
                }
            }
            catch { }
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            // Adopt metadata immediately and ensure widget expanded when media exists
            lock (mediaLock)
            {
                // If shared setting requests hiding media when idle, only expand when playback active
                bool hideWhenIdle = false;
                try { hideWhenIdle = RegisterSmallVisualiserWidgetSettings.SharedMediaSettings.HideMediaWhenIdle; } catch { hideWhenIdle = false; }

                if (e.Media != null)
                {
                    hasMedia = true;

                    // If hideWhenIdle is enabled, determine expand state based on playback status and pause duration
                    if (hideWhenIdle)
                    {
                        try
                        {
                            var status = DynamicWin.Utils.MediaThumbnailService.Instance?.LastPlaybackStatus;
                            bool playing = status.HasValue && status.Value == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            
                            // If not playing, check if paused longer than 30 seconds
                            bool shouldExpand = playing;
                            if (!playing)
                            {
                                bool pausedLong = MediaThumbnailService.Instance.IsPausedLongerThan(TimeSpan.FromSeconds(30));
                                shouldExpand = !pausedLong;
                            }
                            
                            BeginInvokeUI(() => StartCollapseOrExpand(shouldExpand));
                        }
                        catch
                        {
                            BeginInvokeUI(() => StartCollapseOrExpand(true));
                        }
                    }
                    else
                    {
                        BeginInvokeUI(() => StartCollapseOrExpand(true));
                    }
                }
                else
                {
                    // No media -> collapse after a short delay to avoid flicker
                    hasMedia = false;
                    BeginInvokeUI(() => StartCollapseOrExpand(false));
                }
            }

            // Prefer bytes provided by event; if missing, prefer canonical service bytes
            byte[]? bytes = e.ThumbnailBytes;
            Media? media = e.Media;

            if (bytes == null || bytes.Length == 0)
            {
                // Try to read cached bytes from service (fast, non-blocking)
                try { bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes(); } catch { bytes = null; }
            }

            if (bytes != null && bytes.Length > 0)
            {
                // Throttle rapid decode attempts
                var now = DateTime.UtcNow;
                if ((now - lastDecodeTime) < minDecodeInterval)
                {
                    // Schedule a short delayed decode to coalesce rapid events
                    Task.Delay((int)minDecodeInterval.TotalMilliseconds).ContinueWith(_ => DecodeAndQueue(bytes, media));
                }
                else
                {
                    lastDecodeTime = now;
                    _ = Task.Run(() => DecodeAndQueue(bytes, media));
                }
            }
            else
            {
                // No bytes available; nothing to decode. Widget will remain showing existing image (if any) or collapsed state.
                // Update currentMediaKey to reflect new metadata (without triggering animation)
                if (media != null)
                {
                    lock (mediaLock)
                    {
                        currentMediaKey = $"{media.Title ?? string.Empty}|{media.Artist ?? string.Empty}|0";
                    }
                }
            }
        }

        private void DecodeAndQueue(byte[] bytes, Media? media)
        {
            SKBitmap? bmp = null;
            try
            {
                using var ms = new SKMemoryStream(bytes);
                bmp = SKBitmap.Decode(ms);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaThumbnailWidget.Decode failed: " + ex.Message);
                bmp = null;
            }

            if (bmp == null) return;

            ulong? fp = null;
            try { fp = BitmapUtils.GetBitmapFingerprint(bmp); } catch { fp = null; }

            lock (mediaLock)
            {
                // If image visually identical to currently displayed, adopt metadata only
                if (fp.HasValue && currentBitmapFingerprint.HasValue && fp.Value == currentBitmapFingerprint.Value)
                {
                    try { bmp.Dispose(); } catch { }
                    if (media != null)
                    {
                        currentMediaKey = $"{media.Title ?? string.Empty}|{media.Artist ?? string.Empty}|{bytes.Length}";
                    }
                    return;
                }

                // If we already have a pending bitmap, replace it
                if (pendingBitmap != null)
                {
                    try { pendingBitmap.Dispose(); } catch { }
                    pendingBitmap = null;
                    pendingBitmapFingerprint = null;
                    pendingMediaKey = null;
                    pendingMedia = null;
                }

                pendingBitmap = bmp;
                pendingBitmapFingerprint = fp;
                pendingMedia = media;
                pendingMediaKey = (media == null) ? string.Empty : $"{media.Title ?? string.Empty}|{media.Artist ?? string.Empty}|{bytes.Length}";

                // Ensure animator sees there is pending content
                // (animator.Update will be called from Update loop)
            }
        }

        private void StartCollapseOrExpand(bool expand)
        {
            try
            {
                if (collapseAnim != null)
                {
                    try { collapseAnim.Stop(false); } catch { }
                    try { DestroyLocalObject(collapseAnim); } catch { }
                    collapseAnim = null;
                }

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

                    if (!expanding)
                    {
                        lock (mediaLock)
                        {
                            if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; currentBitmapFingerprint = null; }
                            if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingBitmapFingerprint = null; }
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

            // Animate thumbnail scale/dim
            bool isPaused = false;
            try
            {
                var status = MediaThumbnailService.Instance?.LastPlaybackStatus;
                if (status.HasValue)
                {
                    isPaused = status.Value != Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
            }
            catch { }
            float target = isPaused ? 0f : 1f;
            thumbnailAnim = Mathf.Lerp(thumbnailAnim, target, Math.Min(1f, thumbnailAnimSpeed * deltaTime));

            // Drive animator; check pendingBitmap under lock
            animator.Update(deltaTime, () => { lock (mediaLock) { return pendingBitmap != null; } },
                onStart: () => { lock (mediaLock) { previousBitmap = thumbnailBitmap; } },
                onMidFlip: () =>
                {
                    lock (mediaLock)
                    {
                        if (thumbnailBitmap != null)
                        {
                            try { thumbnailBitmap.Dispose(); } catch { }
                        }
                        thumbnailBitmap = pendingBitmap;
                        currentBitmapFingerprint = pendingBitmapFingerprint;
                        pendingBitmap = null;
                        pendingBitmapFingerprint = null;

                        currentMediaKey = pendingMediaKey;
                        pendingMediaKey = null;

                        if (pendingMedia != null)
                        {
                            // Clear pending metadata (widget doesn't display textual metadata)
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

                float thumbScale = 0.8f + 0.2f * thumbnailAnim;
                float dimAlpha = (1f - thumbnailAnim) * 120f;
                float centerX = thumbRect.MidX;
                float centerY = thumbRect.MidY;

                if (doFlip)
                {
                    int save = canvas.Save();
                    float cx = thumbRect.MidX;
                    float cy = thumbRect.MidY;
                    canvas.Translate(cx, cy);
                    canvas.Scale(flipScale * thumbScale, thumbScale);
                    var localRect = SKRect.Create(-thumbRect.Width / 2f, -thumbRect.Height / 2f, thumbRect.Width, thumbRect.Height);
                    var localPath = BuildSuperellipsePath(localRect, 7f, 1f);
                    canvas.Save();
                    canvas.ClipPath(localPath, antialias: Settings.AntiAliasing);
                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    if (bmp != null)
                    {
                        canvas.DrawBitmap(bmp, localRect, paint);
                        if (dimAlpha > 0.5f)
                        {
                            using var dimPaint = GetPaint();
                            dimPaint.Color = new SKColor(0, 0, 0, (byte)dimAlpha);
                            canvas.DrawRect(localRect, dimPaint);
                        }
                    }
                    else
                    {
                        paint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        canvas.DrawRoundRect(new SKRoundRect(localRect, localRect.Width * 0.12f), paint);
                    }
                    canvas.Restore();
                    canvas.RestoreToCount(save);
                }
                else
                {
                    canvas.Save();
                    canvas.Translate(centerX, centerY);
                    canvas.Scale(thumbScale, thumbScale);
                    canvas.Translate(-centerX, -centerY);
                    canvas.ClipPath(path, antialias: Settings.AntiAliasing);
                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    if (bmp != null)
                    {
                        canvas.DrawBitmap(bmp, thumbRect, paint);
                        if (dimAlpha > 0.5f)
                        {
                            using var dimPaint = GetPaint();
                            dimPaint.Color = new SKColor(0, 0, 0, (byte)dimAlpha);
                            canvas.DrawRect(thumbRect, dimPaint);
                        }
                    }
                    else
                    {
                        paint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        canvas.DrawRoundRect(new SKRoundRect(thumbRect, thumbRect.Width * 0.12f), paint);
                    }
                    canvas.Restore();
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
            // Ensure animation is finished and thumbnail is visible
            ForceFinishAnimation();
            // Dispose owned bitmaps
            lock (mediaLock)
            {
                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
            }
        }

        /// <summary>
        /// Force the animator to finish and reset blur/flip state, ensuring thumbnail is always visible.
        /// </summary>
        public void ForceFinishAnimation()
        {
            animator.ForceFinish();
            lock (mediaLock)
            {
                if (pendingBitmap != null)
                {
                    if (thumbnailBitmap != null)
                    {
                        try { thumbnailBitmap.Dispose(); } catch { }
                    }
                    thumbnailBitmap = pendingBitmap;
                    currentBitmapFingerprint = pendingBitmapFingerprint;
                    pendingBitmap = null;
                    pendingBitmapFingerprint = null;
                    currentMediaKey = pendingMediaKey;
                    pendingMediaKey = null;
                    pendingMedia = null;
                }
                if (previousBitmap != null)
                {
                    try { previousBitmap.Dispose(); } catch { }
                    previousBitmap = null;
                }
            }
        }

        // Make this small widget square: width matches height so thumbnail is not stretched
        protected override float GetWidgetWidth()
        {
            float full = GetWidgetHeight();
            return full * collapseProgress;
        }
    }
}
