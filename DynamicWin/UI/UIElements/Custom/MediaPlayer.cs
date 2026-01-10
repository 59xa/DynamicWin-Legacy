using DynamicWin.Main;
using DynamicWin.UI.UIElements;
using System.Collections.Generic;
using SkiaSharp;
using DynamicWin.Utils;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System;
using System.Diagnostics;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus;
using System.Linq;

/*
 * 
 *   Overview:
 *    - Implements media playback interface similar to the media control interface inside Apple's Dynamic Island
 *    - Supersedes Legacy Media Playback Control Widget (MediaWidget.cs)
 *
 *   Author:                 59xa
 *   GitHub:                 https://github.com/59xa
 *   Implementation Date:    26 December 2025
 *   Last Modified:          10 January 2026
 *
 */

namespace DynamicWin.UI.UIElements.Custom
{
    public class MediaPlayer : UIObject
    {
        private CancellationTokenSource? cts;
        private DynamicWin.Utils.Media? currentMedia;
        private SKBitmap? thumbnailBitmap; // Currently cached decoded bitmap (owned by this object)
        private SKBitmap? pendingBitmap; // Newly decoded bitmap waiting to animate in
        private DynamicWin.Utils.Media? pendingMedia; // Pending metadata object
        private readonly object mediaLock = new object();
        private TimeSpan fetchInterval = TimeSpan.FromSeconds(1);

        // Keys to detect duplicates
        private string? currentMediaKey;
        private string? pendingMediaKey;

        // Scrolling title state
        private float titleScrollOffset = 0f; // Current scroll position
        private float titleScrollSpeed = 30f; // Pixels per second
        private float titleScrollDelay = 1f;  // Seconds to pause before scrolling
        private float titleScrollTimer = 0f;  // Timer for delay
        private bool isTitleScrolling = false;
        private string? fullTitleText = null;
        private float titleTextWidth = 0f;
        private const int titleScrollCharThreshold = 35;

        // Animation state handled by MediaAnimator
        private readonly MediaAnimator animator = new MediaAnimator();
        private SKBitmap? previousBitmap = null; // Bitmap that is being replaced

        // Playback controls and progress
        private MediaController controller;
        private DWImageButton? btnPrev;
        private DWImageButton? btnPlay;
        private DWImageButton? btnNext;

        AudioVisualiser visualiser;

        // Timeline state
        private TimeSpan? timelinePosition;
        private TimeSpan? timelineDuration;
        private bool isPlayingFlag = false;
        private float timelineTimer = 0f;
        private const float timelineInterval = 0.5f;
        private bool timelineFetchInProgress = false;

        // Optimistic toggle to update UI immediately when user presses play/pause
        private bool optimisticState = false;
        private bool optimisticActive = false; // Remains active until a timeline sample updates

        // Animated progress fill
        private float displayFill = 0f;

        // Compare two SKBitmaps for visual equality. Uses fast pixel-by-pixel comparison to avoid allocations.
        private static bool AreBitmapsEqual(SKBitmap? a, SKBitmap? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Width != b.Width || a.Height != b.Height) return false;

            try
            {
                // Fast pixel-by-pixel compare using SKBitmap.GetPixel (returns SKColor)
                int w = a.Width;
                int h = a.Height;

                // Compare row by row and bail out early on mismatch
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var ca = a.GetPixel(x, y);
                        var cb = b.GetPixel(x, y);
                        if (ca != cb) return false;
                    }
                }

                return true;
            }
            catch
            {
                // Fallback: if direct pixel compare fails for any reason, conservatively return false so caller can update
                return false;
            }
        }

        public MediaPlayer(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            controller = new MediaController();

            // Create interactive playback buttons and progress UI as local objects; will be positioned in Update
            btnPrev = new DWImageButton(this, Resources.Res.Previous, new Vec2(0, 0), new Vec2(28, 28), () => { controller.Previous(); }, alignment: UIAlignment.TopLeft)
            {
                roundRadius = 14f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.7f
            };
            AddLocalObject(btnPrev);

            // Hook play/pause button to also toggle optimistic UI state
            btnPlay = new DWImageButton(this, Resources.Res.Play, new Vec2(0, 0), new Vec2(32, 32), () => {
                // Optimistic toggle
                optimisticState = !GetEffectivePlayingState();
                optimisticActive = true;
                // Send play/pause command
                controller.PlayPause();
                // Update icon immediately
                if (btnPlay != null)
                {
                    btnPlay.Image.Image = optimisticState ? (Resources.Res.Pause ?? Resources.Res.Stop) : Resources.Res.Play;
                }
            }, alignment: UIAlignment.TopLeft)
            {
                roundRadius = 16f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.78f
            };
            AddLocalObject(btnPlay);

            btnNext = new DWImageButton(this, Resources.Res.Next, new Vec2(0, 0), new Vec2(28, 28), () => { controller.Next(); }, alignment: UIAlignment.TopLeft)
            {
                roundRadius = 14f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.7f
            };
            AddLocalObject(btnNext);

            visualiser = new AudioVisualiser(this, new Vec2(-20, 33), new Vec2(28, 28), UIAlignment.TopRight)
            {
                UseThumbnailBackground = true
            };
            AddLocalObject(visualiser);

            // Subscribe to central thumbnail service event
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;

            // Try to initialise thumbnail from service cache so it doesn't disappear when re-opening
            try
            {
                var serviceBmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (serviceBmp != null)
                {
                    // Clone into our own SKBitmap
                    try
                    {
                        // Prefer a safe pixel copy to avoid sharing ownership
                        var bmp = new SKBitmap(serviceBmp.Info);
                        serviceBmp.CopyTo(bmp);
                        lock (mediaLock)
                        {
                            thumbnailBitmap = bmp;
                            // No currentMedia metadata here
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool GetEffectivePlayingState()
        {
            // If optimistic is active, prefer that until timeline updates arrive
            if (optimisticActive) return optimisticState;
            return isPlayingFlag;
        }

        protected override void OnActiveChanged(bool isEnabled)
        {
            base.OnActiveChanged(isEnabled);

            if (isEnabled)
            {
                StartFetchLoop();

                // If service has a cached bitmap, ensure it's used (queue as pending to animate in)
                var svcBmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (svcBmp != null)
                {
                    try
                    {
                        var clone = new SKBitmap(svcBmp.Info);
                        svcBmp.CopyTo(clone);
                        lock (mediaLock)
                        {
                            // Queue as pending to trigger animator
                            if (thumbnailBitmap == null)
                            {
                                pendingBitmap = clone;
                                pendingMedia = null;
                                pendingMediaKey = null;
                            }
                            else
                            {
                                // Replace directly
                                try { thumbnailBitmap.Dispose(); } catch { }
                                thumbnailBitmap = clone;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // No cached service bitmap yet – do a one-shot fetch so first-open has a thumbnail.
                    Task.Run(async () =>
                    {
                        try
                        {
                            var bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync().ConfigureAwait(false);
                            var meta = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);

                            if (bytes != null && bytes.Length > 0)
                            {
                                SKBitmap? newBmp = null;
                                try
                                {
                                    using var ms = new SKMemoryStream(bytes);
                                    newBmp = SKBitmap.Decode(ms);
                                }
                                catch { newBmp = null; }

                                if (newBmp != null)
                                {
                                    lock (mediaLock)
                                    {
                                        // Queue as pending so animator will run even on first show
                                        // If the decoded bitmap is visually identical to current thumbnail, adopt metadata and skip animation
                                        if (thumbnailBitmap != null && AreBitmapsEqual(newBmp, thumbnailBitmap))
                                        {
                                            currentMedia = meta;
                                            currentMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{bytes.Length}";
                                            try { newBmp.Dispose(); } catch { }
                                        }
                                        else
                                        {
                                            if (thumbnailBitmap == null && pendingBitmap == null)
                                            {
                                                pendingBitmap = newBmp;
                                                pendingMedia = meta;
                                                pendingMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{bytes.Length}";
                                            }
                                            else
                                            {
                                                // If thumbnail already exists, set as pending to animate
                                                if (pendingBitmap != null)
                                                {
                                                    try { pendingBitmap.Dispose(); } catch { }
                                                }

                                                pendingBitmap = newBmp;
                                                pendingMedia = meta;
                                                pendingMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{bytes.Length}";
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // If there really is no media (no bytes and no metadata), ensure we wipe any cached thumbnails
                                if (meta == null)
                                {
                                    lock (mediaLock)
                                    {
                                        if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                                        if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                                        if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                        currentMediaKey = null;
                                        pendingMediaKey = null;
                                        currentMedia = null;
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                }
            }
            else
            {
                StopFetchLoop();
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Ensure fetch loop only runs while media UI is actually visible in HomeMenu Media tab
            bool visible = false;
            try
            {
                var home = Res.HomeMenu;
                visible = (home != null && home.currentBigMenuMode == HomeMenu.BigMenuMode.Media && RendererMain.Instance.MainIsland.IsHovering);
            }
            catch { visible = false; }

            if (visible)
            {
                if (cts == null)
                    StartFetchLoop();
            }
            else
            {
                if (cts != null)
                    StopFetchLoop();

                // If not visible, skip the rest of Update to avoid changing child visibility/layout
                return;
            }

            // Drive animator
            animator.Update(deltaTime, () => { lock (mediaLock) { return pendingBitmap != null; } },
                onStart: () =>
                {
                    // Owner should capture previousBitmap
                    lock (mediaLock)
                    {
                        previousBitmap = thumbnailBitmap;
                    }
                },
                onMidFlip: () =>
                {
                    // Swap bitmaps/metadata mid-flip
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
                            currentMedia = pendingMedia;
                            pendingMedia = null;
                        }
                    }
                },
                onFinish: () =>
                {
                    // Dispose previousBitmap
                    if (previousBitmap != null)
                    {
                        try { previousBitmap.Dispose(); } catch { }
                        previousBitmap = null;
                    }
                });

            // Update timeline periodically
            timelineTimer += deltaTime;
            if (timelineTimer >= timelineInterval && !timelineFetchInProgress)
            {
                timelineTimer = 0f;
            }

            // Position controls relative to layout (unchanged)
            try
            {
                var rr = GetRect();
                var rect = rr.Rect;
                float padding = 0f;
                float thumbSize = Math.Min(rect.Height - padding * 2f, rect.Height * 1f);
                SKRect thumbRect = SKRect.Create(rect.Left + padding, rect.Top + padding, thumbSize, thumbSize);

                float textSpacing = 14f;
                float textX = thumbRect.Right + textSpacing;
                float availableWidth = rect.Width - (textX - rect.Left) - padding;

                // Compute positions in local coordinates (relative to rect.TopLeft)
                float localBaseX = textX - rect.Left;
                float titleY = 16f;
                float titleHeight = 14f;
                float artistHeight = 12f;

                float buttonsYOffset = titleY + titleHeight + artistHeight + 24f; // Below texts
                float btnSize = 28f;
                // Reduce spacing so buttons sit closer to thumbnail/text
                float btnSpacing = 8f;
                float buttonsTotal = btnSize * 3f + btnSpacing * 2f;
                // Place buttons directly to the right of the thumbnail (closer to thumbnail)
                float startXLocal = thumbRect.Right - rect.Left - 25f; // 25px gap from thumbnail
                float btnY = buttonsYOffset;

                if (btnPrev != null) btnPrev.LocalPosition = new Vec2(startXLocal, btnY);
                if (btnPlay != null) btnPlay.LocalPosition = new Vec2(startXLocal + (btnSize + btnSpacing), btnY); // Play in middle
                if (btnNext != null) btnNext.LocalPosition = new Vec2(startXLocal + 2 * (btnSize + btnSpacing), btnY);

                // Update play/pause icon based on detected playback state (consider optimistic)
                if (btnPlay != null)
                {
                    bool effectivePlaying = GetEffectivePlayingState();
                    var icon = effectivePlaying ? (Resources.Res.Pause ?? Resources.Res.Stop) : Resources.Res.Play;
                    try
                    {
                        btnPlay.Image.Image = icon;
                        btnPlay.Image.Color = Theme.IconColor; // Ensure visible tint
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Update layout error: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(currentMedia?.Title))
            {
                fullTitleText = currentMedia.Title;

                var paint = GetPaint();
                paint.TextSize = 14f;
                paint.Typeface = Resources.Res.SatoshiBold;

                titleTextWidth = paint.MeasureText(fullTitleText);

                // Trigger scrolling if longer than threshold
                if (fullTitleText.Length > titleScrollCharThreshold)
                {
                    isTitleScrolling = true;

                    if (titleScrollTimer < titleScrollDelay)
                    {
                        titleScrollTimer += deltaTime; // Wait before scroll
                    }
                    else
                    {
                        titleScrollOffset += titleScrollSpeed * deltaTime;
                        if (titleScrollOffset > titleTextWidth + 20f) // Wrap after text + gap
                        {
                            titleScrollOffset = 0f;
                            titleScrollTimer = 0f; // Pause before next scroll
                        }
                    }
                }
                else
                {
                    isTitleScrolling = false;
                    titleScrollOffset = 0f;
                }
            }
            else
            {
                isTitleScrolling = false;
                titleScrollOffset = 0f;
            }
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            // Called from MediaThumbnailService loop (background). We only care about bytes/metadata presence.
            // If bytes present, decode into a bitmap for pending swap; if null, just update metadata.
            Task.Run(() =>
            {
                var media = e.Media;
                var bytes = e.ThumbnailBytes;

                // If there's really no media (no metadata and no bytes), wipe cached thumbnails
                if (media == null && (bytes == null || bytes.Length == 0))
                {
                    lock (mediaLock)
                    {
                        currentMedia = null;
                        currentMediaKey = null;

                        if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                        if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                        if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }

                        pendingMedia = null;
                        pendingMediaKey = null;
                    }

                    return;
                }

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
                        Debug.WriteLine("Thumbnail decode in service handler failed: " + ex.Message);
                        newBmp = null;
                    }
                }

                lock (mediaLock)
                {
                    // Build key similar to prior logic
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
                        // If the incoming bitmap is visually identical to the currently displayed thumbnail,
                        // treat it as not-new: dispose the decoded bitmap and update metadata/key instead
                        if (newBmp != null && thumbnailBitmap != null && AreBitmapsEqual(newBmp, thumbnailBitmap))
                        {
                            // Adopt metadata without triggering a flip animation
                            currentMedia = media;
                            currentMediaKey = key;
                            try { newBmp.Dispose(); } catch { }
                            pendingMedia = null;
                            pendingMediaKey = null;
                            return; // exit the Task.Run delegate early
                        }

                        // If there's no current thumbnail yet, queue as pending to animate in (so first show animates)
                        if (thumbnailBitmap == null && newBmp != null)
                        {
                            pendingBitmap = newBmp;
                            pendingMedia = media;
                            pendingMediaKey = key;
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
                            }

                            if ((newBmp == null) && key != currentMediaKey && pendingMediaKey == null)
                            {
                                currentMedia = media;
                                currentMediaKey = key;

                                // If no media, wipe thumbnail cache so UI doesn't show stale artwork
                                if (media == null)
                                {
                                    if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                                    if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                    if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                                    pendingMediaKey = null;
                                    pendingMedia = null;
                                }
                            }
                        }
                    }
                }
            });
        }

        private void StartFetchLoop()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var media = await MediaInfo.FetchCurrentMediaAsync();

                        // Build lightweight key to detect duplicates (title|artist|thumbLen)
                        string key = (media == null) ? string.Empty : $"{media.Title ?? ""}|{media.Artist ?? ""}|{(media.ThumbnailData?.Length ?? 0)}";

                        // Convert thumbnail bytes to SKBitmap on background thread and update fields under lock
                        SKBitmap? newBmp = null;
                        try
                        {
                            if (media?.ThumbnailData != null && media.ThumbnailData.Length > 0)
                            {
                                using var ms = new MemoryStream(media.ThumbnailData);
                                newBmp = SKBitmap.Decode(ms);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Thumbnail decode failed: " + ex.Message);
                            newBmp = null;
                        }

                        lock (mediaLock)
                        {
                            // If key matches current or pending, skip updates entirely
                            if (key == currentMediaKey || key == pendingMediaKey)
                            {
                                // No change
                                if (newBmp != null)
                                {
                                    try { newBmp.Dispose(); } catch { }
                                }
                            }
                            else
                            {
                                // If the newly-decoded bitmap matches the currently-displayed bitmap, adopt metadata/key
                                // and avoid queuing an animation.
                                if (newBmp != null && thumbnailBitmap != null && AreBitmapsEqual(newBmp, thumbnailBitmap))
                                {
                                    currentMedia = media;
                                    currentMediaKey = key;
                                    try { newBmp.Dispose(); } catch { }
                                    continue;
                                }

                                // If there's no current thumbnail yet, queue as pending to animate in
                                if (thumbnailBitmap == null && newBmp != null)
                                {
                                    pendingBitmap = newBmp;
                                    pendingMedia = media;
                                    pendingMediaKey = key;
                                }
                                else
                                {
                                    // Queue as pending (replace any existing pending)
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

                                        // Store pending metadata to update textual fields when swapped
                                    }

                                    // If no thumbnail changes, but metadata changed and no pending, update currentMedia immediately
                                    if ((newBmp == null) && key != currentMediaKey && pendingMediaKey == null)
                                    {
                                        currentMedia = media;
                                        currentMediaKey = key;

                                        // If no media present, wipe cached thumbnails so UI can't show stale artwork
                                        if (media == null)
                                        {
                                            if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                                            if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                            if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }

                                            pendingMediaKey = null;
                                            pendingMedia = null;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Media fetch loop error: " + ex.Message);
                    }

                    // Cooperative non-throwing wait: break long interval into short steps and check token
                    int totalMs = (int)fetchInterval.TotalMilliseconds;
                    int waited = 0;
                    const int step = 250; // 250ms check

                    while (waited < totalMs && !token.IsCancellationRequested)
                    {
                        int delay = Math.Min(step, totalMs - waited);
                        try
                        {
                            await Task.Delay(delay).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Swallow
                        }
                        waited += delay;
                    }
                }
            }, token);
        }

        private void StopFetchLoop()
        {
            if (cts == null) return;
            cts.Cancel();
            cts.Dispose();
            cts = null;

            // Preserve cached thumbnails when stopping the loop so the UI shows the last image
            lock (mediaLock)
            {
                // Keep currentMedia and thumbnailBitmap so thumbnail remains visible when re-opening
                // Only clear transient pending state
                if (pendingBitmap != null)
                {
                    try { pendingBitmap.Dispose(); } catch { }
                    pendingBitmap = null;
                }

                pendingMediaKey = null;
                pendingMedia = null;

                // Do not dispose thumbnailBitmap or previousBitmap here; keep cached for quick re-show
                // currentMediaKey remains so duplicate detection still works

                // Hide controls
                // If (progressBar != null) progressBar.SilentSetActive(false);
            }
        }

        public override void Draw(SKCanvas canvas)
        {
            // Extra visibility guard: only draw when HomeMenu is present and showing Media and island is hovered
            try
            {
                var home = Res.HomeMenu;
                if (home == null) return;
                if (home.currentBigMenuMode != HomeMenu.BigMenuMode.Media) return;
                if (!RendererMain.Instance.MainIsland.IsHovering) return;
            }
            catch
            {
                // Swallow if something goes wrong
                return;
            }

            // Do not draw if this UIObject is not enabled or its parent is not enabled
            if (!IsEnabled) return;
            if (Parent != null && !Parent.IsEnabled) return;

            var rr = GetRect();
            var rect = rr.Rect; // SKRect

            // Guard: don't draw if rect is degenerate
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // Limit thumbnail size so it never dominates the widget or leaks visually
            float maxThumb = Math.Min(90f, rect.Width * 0.35f); // Cap to 90px and a fraction of width
            float thumbSize = Math.Min(Math.Min(rect.Height * 2f, rect.Height * 1f), maxThumb);
            thumbSize = Math.Max(12f, thumbSize); // Ensure reasonable minimum

            float thumbRadius = Math.Max(12f, thumbSize * 0.18f); // Base radius for fallback

            SKRect thumbRect = SKRect.Create(rect.Left, rect.Top, thumbSize, thumbSize);

            string title = "No media playing";
            string artist = "No media playing";
            SKBitmap? bmp = null;

            lock (mediaLock)
            {
                if (currentMedia != null)
                {
                    title = currentMedia.Title ?? "No media playing";
                    artist = currentMedia.Artist ?? "No media playing";
                }
                bmp = thumbnailBitmap;
            }

            // If no thumbnail and metadata, don't draw media-specific chrome
            if (bmp == null && string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist)) return;

            // Determine current display bitmap and possible previous
            SKBitmap? displayBmp = bmp;
            SKBitmap? prevBmp = previousBitmap;

            // Build squircle path for thumbnail
            var squirclePath = BuildSuperellipsePath(thumbRect, n: 4f, stepsPerQuarter: 18);

            // Draw thumbnail with animation transforms
            try
            {
                // Compute flip scale from animator
                float flipScale = animator.GetFlipScale();
                bool doFlip = animator.IsFlipping;

                int save = canvas.Save();

                // Apply horizontal flip transform around thumb center
                if (doFlip)
                {
                    float cx = thumbRect.MidX;
                    float cy = thumbRect.MidY;
                    canvas.Translate(cx, cy);
                    canvas.Scale(flipScale, 1f);
                    // Draw contents centered at origin
                    var localRect = SKRect.Create(-thumbSize / 2f, -thumbSize / 2f, thumbSize, thumbSize);

                    // Clip to squircle
                    var localPath = BuildSuperellipsePath(localRect, n: 4f, stepsPerQuarter: 10);
                    canvas.Save();
                    canvas.ClipPath(localPath, antialias: true);

                    var paint = GetPaint();
                    paint.IsAntialias = true;
                    paint.IsStroke = false;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    paint.BlendMode = SKBlendMode.SrcOver;

                    if (displayBmp != null)
                    {
                        var dest = localRect;
                        canvas.DrawBitmap(displayBmp, dest, paint);
                    }
                    else
                    {
                        // Placeholder
                        using (var p = GetPaint())
                        {
                            p.IsAntialias = true;
                            p.IsStroke = false;
                            p.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                            p.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                            p.BlendMode = SKBlendMode.SrcOver;
                            canvas.DrawRoundRect(new SKRoundRect(localRect, thumbRadius), p);
                        }
                    }

                    // Restore clip after drawing
                    canvas.Restore();

                    canvas.RestoreToCount(save);

                    // Draw border in normal coordinates (not flipped) so border doesn't mirror oddly
                    using (var borderPaint = GetPaint())
                    {
                        borderPaint.IsStroke = true;
                        borderPaint.IsAntialias = true;
                        borderPaint.StrokeWidth = 1.0f;
                        borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(squirclePath, borderPaint);
                    }
                }
                else
                {
                    // Not flipping: draw normally clipped to squircle
                    canvas.Save();
                    canvas.ClipPath(squirclePath, antialias: true);

                    var paint = GetPaint();
                    paint.IsAntialias = true;
                    paint.IsStroke = false;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    paint.BlendMode = SKBlendMode.SrcOver;

                    if (displayBmp != null)
                    {
                        canvas.DrawBitmap(displayBmp, thumbRect, paint);
                    }
                    else
                    {
                        using (var p = GetPaint())
                        {
                            p.IsAntialias = true;
                            p.IsStroke = false;
                            p.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                            p.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                            p.BlendMode = SKBlendMode.SrcOver;
                            canvas.DrawRoundRect(new SKRoundRect(thumbRect, thumbRadius), p);
                        }
                    }

                    // Restore to pre-clip state
                    canvas.Restore();

                    // Subtle border
                    using (var borderPaint = GetPaint())
                    {
                        borderPaint.IsStroke = true;
                        borderPaint.IsAntialias = true;
                        borderPaint.StrokeWidth = 1.0f;
                        borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(squirclePath, borderPaint);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Draw thumbnail error: " + ex.Message);
            }

            // Draw title and artist to the right of thumbnail
            float textX;
            float textY = rect.Top + 16f; // Starting "y" for first line

            // Add explicit spacing between thumbnail and text
            float textSpacing = 14f;
            textX = thumbRect.Right + textSpacing;

            var titlePaint = GetPaint();
            titlePaint.IsStroke = false;
            titlePaint.TextSize = 14f;
            titlePaint.Typeface = Resources.Res.SatoshiBold;
            titlePaint.Color = GetColor(Theme.TextMain).Value();

            var artistPaint = GetPaint();
            artistPaint.IsStroke = false;
            artistPaint.TextSize = 12f;
            artistPaint.Typeface = Resources.Res.SatoshiRegular;
            artistPaint.Color = GetColor(Theme.TextSecond).Value();

            if (!string.IsNullOrEmpty(fullTitleText))
            {
                float maxWidth = rect.Width - (textX - rect.Left) - 45f; // Max width for text

                if (isTitleScrolling)
                {
                    // Draw scrolling text
                    canvas.Save();
                    // Clip to visible width
                    canvas.ClipRect(SKRect.Create(textX, textY, maxWidth, titlePaint.TextSize + 2f), antialias: true);

                    float xPos = textX - titleScrollOffset;
                    canvas.DrawText(fullTitleText, xPos, textY + titlePaint.TextSize, titlePaint);

                    // Draw second copy for seamless wrap
                    if (xPos + titleTextWidth < textX + maxWidth)
                    {
                        canvas.DrawText(fullTitleText, xPos + titleTextWidth + 20f, textY + titlePaint.TextSize, titlePaint);
                    }

                    canvas.Restore();
                }
                else
                {
                    // Draw truncated text normally
                    var truncated = DWText.Truncate(fullTitleText, titleScrollCharThreshold);
                    canvas.DrawText(truncated, textX, textY + titlePaint.TextSize, titlePaint);
                }
            }

            if (!string.IsNullOrEmpty(artist))
            {
                var displayArtist = DWText.Truncate(artist, 45);
                canvas.DrawText(displayArtist, textX, textY + titlePaint.TextSize + artistPaint.TextSize + 6f, artistPaint);
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            // Unsubscribe from thumbnail service
            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChanged; } catch { }

            // Ensure fetch loop stopped and bitmaps cleaned
            StopFetchLoop();

            lock (mediaLock)
            {
                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; }
                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; }
                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
            }
        }
    }
}
