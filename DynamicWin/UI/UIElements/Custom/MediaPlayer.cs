using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.Utils;
using SkiaSharp;
using System.Diagnostics;
using System.IO;
using Windows.Media.Control;

/*
 * 
 *   Overview:
 *    - Implements media playback interface similar to the media control interface inside Apple's Dynamic Island
 *    - Supersedes Legacy Media Playback Control Widget (MediaWidget.cs)
 *
 *   Author:                 59xa
 *   GitHub:                 https://github.com/59xa
 *   Implementation Date:    26 December 2025
 *   Last Modified:          12 January 2026
 *
 */

namespace DynamicWin.UI.UIElements.Custom
{
    public class MediaPlayer : UIObject
    {
        private CancellationTokenSource? cts;
        private DynamicWin.Utils.Media? currentMedia;
        private SKBitmap? thumbnailBitmap; // Currently cached decoded bitmap (owned by this object)
        private ulong? thumbnailFingerprint; // Cached fingerprint for thumbnailBitmap
        private SKBitmap? pendingBitmap; // Newly decoded bitmap waiting to animate in
        private ulong? pendingFingerprint;
        private DynamicWin.Utils.Media? pendingMedia; // Pending metadata object
        private readonly object mediaLock = new object();
        // How often the background loop waits between iterations (cooperative wait broken into steps)
        private TimeSpan fetchInterval = TimeSpan.FromMilliseconds(250); // faster timeline updates

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
        private string? prevFullTitleText = null; // Track previous to avoid re-measuring
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

        // Optimistic toggle to update UI immediately when user presses play/pause
        private bool optimisticState = false;
        private bool optimisticActive = false; // Remains active until a timeline sample updates

        // Animated progress fill
        private float displayFill = 0f;

        private float timelineHeight = 6f; // Thickness of the bar
        private SKColor timelineBgColor; // subtle background
        private Col timelineFgColor = Theme.TextMain; // active fill
        private float timelineBarPadding = 12f; // vertical padding below buttons
        private float timelineSidePadding = 40f; // space on left/right for timeline text
        private Col timelineTextColor = Theme.TextMain.Override(a: 55);
        private float timelineTextSize = 10f;

        // Add lastSampleKey to detect new samples
        private string? lastSampleKey = null;

        // Latest timeline sample (elapsed since start) and timestamp when it was received
        private TimeSpan? lastSampleElapsed = null;
        private TimeSpan? lastSampleDuration = null;
        private DateTime lastSampleReceivedAt = DateTime.MinValue;
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

        // Keep the latest MediaTimeline for the current session (metadata kept in currentMedia)
        private MediaTimeline? currentTimeline = null;

        // Only fetch timeline once per media change; let local clock advance between fetches to avoid jitter
        private bool timelineFetchedOnce = false;

        private DateTime lastTimelineResync = DateTime.MinValue;

        // If the user is interacting with the timeline (seeking), set this to true and update userSeekElapsed
        private bool userIsSeeking = false;
        private TimeSpan userSeekElapsed = TimeSpan.Zero;
        private bool mouseDownOverTimeline = false;
        // Whether the cursor is hovering over the timeline bar (used to increase bar height)
        private bool isHoveringOverTimeline = false;

        // Smoothed displayed elapsed seconds to avoid integer-second jitter in the UI text
        private float displayedElapsedSeconds = 0f;
        private bool displayedElapsedInitialized = false;
        // Extra height applied to timeline when hovering/seeking (smoothed)
        private float timelineExtraHeight = 0f;

        // Lightweight fingerprint for bitmap equality: sample a few pixels and dimensions
        private static ulong ComputeFingerprint(SKBitmap bmp)
        {
            if (bmp == null) return 0ul;
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV offset basis
                h ^= (ulong)bmp.Width; h *= 1099511628211UL;
                h ^= (ulong)bmp.Height; h *= 1099511628211UL;

                // Sample up to 8 points: corners, mid-edges, center. Use modulo to clamp.
                int w = Math.Max(1, bmp.Width);
                int hgt = Math.Max(1, bmp.Height);
                (int x, int y)[] samples = new (int, int)[] {
                    (0,0), (w-1,0), (0,hgt-1), (w-1,hgt-1),
                    (w/2, hgt/2), (w/2,0), (w/2,hgt-1), (0,hgt/2)
                };

                foreach (var s in samples)
                {
                    try
                    {
                        var c = bmp.GetPixel(Math.Max(0, Math.Min(s.x, w-1)), Math.Max(0, Math.Min(s.y, hgt-1)));
                        // Pack ARGB into ulong
                        ulong val = ((ulong)c.Alpha << 24) | ((ulong)c.Red << 16) | ((ulong)c.Green << 8) | (ulong)c.Blue;
                        h ^= val; h *= 1099511628211UL;
                    }
                    catch
                    {
                        // Ignore sampling errors
                    }
                }

                return h;
            }
        }

        public MediaPlayer(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            timelineBgColor = GetColor(Theme.WidgetBackground.Override(a: 200)).Value();
            controller = new MediaController();

            // Create interactive playback buttons and progress UI as local objects; will be positioned in Update
            btnPrev = new DWImageButton(this, Res.Previous, new Vec2(0, 0), new Vec2(28, 28), () => { controller.Previous(); }, alignment: UIAlignment.TopLeft)
            {
                roundRadius = 14f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.7f
            };
            AddLocalObject(btnPrev);

            // Hook play/pause button to also toggle optimistic UI state
            btnPlay = new DWImageButton(this, Res.Play, new Vec2(0, 0), new Vec2(32, 32), () => {
                // Optimistic toggle
                optimisticState = !GetEffectivePlayingState();
                optimisticActive = true;
                // Send play/pause command
                controller.PlayPause();
                // Update icon immediately
                if (btnPlay != null)
                {
                    btnPlay.Image.Image = optimisticState ? (Res.Pause ?? Res.Stop) : Res.Play;
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

            btnNext = new DWImageButton(this, Res.Next, new Vec2(0, 0), new Vec2(28, 28), () => { controller.Next(); }, alignment: UIAlignment.TopLeft)
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
                UseThumbnailBackground = true,
                EnableColourTransition = false,
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
                            thumbnailFingerprint = ComputeFingerprint(bmp);
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
                                pendingFingerprint = ComputeFingerprint(clone);
                                pendingMedia = null;
                                pendingMediaKey = null;
                            }
                            else
                            {
                                // Replace directly
                                try { thumbnailBitmap.Dispose(); } catch { }
                                thumbnailBitmap = clone;
                                thumbnailFingerprint = ComputeFingerprint(clone);
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
                                    ulong fp = ComputeFingerprint(newBmp);
                                    lock (mediaLock)
                                    {
                                        // Queue as pending so animator will run even on first show
                                        // If the decoded bitmap is visually identical to current thumbnail (fingerprint), adopt metadata and skip animation
                                        if (thumbnailBitmap != null && thumbnailFingerprint.HasValue && thumbnailFingerprint.Value == fp)
                                        {
                                            currentMedia = meta;
                                            currentMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{bytes.Length}";

                                            // Fresh timeline sample arrived -> cancel optimistic UI
                                            optimisticActive = false;
                                        }
                                        else
                                        {
                                            if (thumbnailBitmap == null && pendingBitmap == null)
                                            {
                                                pendingBitmap = newBmp;
                                                pendingFingerprint = fp;
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
                                                pendingFingerprint = fp;
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
                                        if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; thumbnailFingerprint = null; }
                                        if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingFingerprint = null; }
                                        if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                        currentMediaKey = null;
                                        pendingMediaKey = null;
                                        currentMedia = null;

                                        // Fresh timeline sample (none) -> cancel optimistic UI
                                        optimisticActive = false;
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

        private static string FormatTimeSpanForDisplay(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            }
            else
            {
                return string.Format("{0:D2}:{1:D2}", (int)ts.TotalMinutes, ts.Seconds);
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
                        thumbnailFingerprint = pendingFingerprint;
                        pendingBitmap = null;
                        pendingFingerprint = null;

                        currentMediaKey = pendingMediaKey;
                        pendingMediaKey = null;

                        if (pendingMedia != null)
                        {
                            currentMedia = pendingMedia;
                            pendingMedia = null;

                            // Fresh timeline sample arrived -> cancel optimistic UI
                            optimisticActive = false;

                            // Media changed -> we should fetch a fresh timeline next loop
                            timelineFetchedOnce = false;
                            lastTimelineResync = DateTime.MinValue;
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

            // Snapshot currentMedia under lock so Update/Draw see consistent values
            TimeSpan? sampleElapsed = null;
            TimeSpan? sampleDuration = null;
            GlobalSystemMediaTransportControlsSessionPlaybackStatus samplePlayback = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            string sampleKey = null; // left unused for now

            lock (mediaLock)
            {
                if (lastSampleElapsed.HasValue && lastSampleDuration.HasValue && lastSampleReceivedAt != DateTime.MinValue)
                {
                    // Compute elapsed since sample received
                    var since = DateTime.UtcNow - lastSampleReceivedAt;

                    if (userIsSeeking)
                    {
                        sampleElapsed = userSeekElapsed;
                    }
                    else
                    {
                        // If playback was Playing at sample time, advance; otherwise keep snapshot
                        if (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            sampleElapsed = lastSampleElapsed.Value + since;
                        }
                        else
                        {
                            sampleElapsed = lastSampleElapsed.Value;
                        }
                    }

                    sampleDuration = lastSampleDuration;

                    // Keep currentTimeline in sync for UI consumers (text metadata remains in currentMedia)
                    if (currentTimeline != null)
                    {
                        try
                        {
                            currentTimeline.Position = currentTimeline.StartTime + sampleElapsed.Value;
                            currentTimeline.EndTime = currentTimeline.StartTime + sampleDuration.Value;
                            currentTimeline.PlaybackStatus = lastPlaybackStatus;
                        }
                        catch { }
                    }
                }
                else
                {
                    optimisticActive = false;
                    // No timeline snapshot available -> clear currentTimeline so UI hides
                    currentTimeline = null;
                }
            }

            // Update timeline info using snapshot 
            if (sampleElapsed.HasValue && sampleDuration.HasValue)
            {
                timelinePosition = sampleElapsed.Value;
                timelineDuration = sampleDuration.Value;

                if (timelinePosition < TimeSpan.Zero) timelinePosition = TimeSpan.Zero;

                // Determine play state from lastPlaybackStatus (snapshot)
                isPlayingFlag = (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

                // If not playing, do not advance timelinePosition further; it was computed above using snapshot
                if (!isPlayingFlag)
                {
                    // Ensure we don't advance optimistically
                }

                // Cap at end
                if (timelineDuration.HasValue && timelinePosition > timelineDuration)
                {
                    timelinePosition = timelineDuration;
                }
            }
            else
            {
                timelinePosition = null;
                timelineDuration = null;
                isPlayingFlag = false;
                lastSampleKey = null;

                // No timeline -> do NOT reset displayFill here to preserve smooth visual; leave displayFill as-is
            }

            // Animate displayFill towards actual progress (after timelinePosition updated)
            float targetFill = 0f;
            bool haveTarget = false;

            if (userIsSeeking)
            {
                if (timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
                {
                    targetFill = Math.Clamp((float)(userSeekElapsed.TotalSeconds / timelineDuration.Value.TotalSeconds), 0f, 1f);
                    haveTarget = true;
                }
            }
            else if (timelinePosition.HasValue && timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
            {
                targetFill = Math.Clamp((float)(timelinePosition.Value.TotalSeconds / timelineDuration.Value.TotalSeconds), 0f, 1f);
                haveTarget = true;
            }

            // Smoothly interpolate displayFill to target to avoid snapping on resync
            if (haveTarget)
            {
                const float smoothing = 30f; // more responsive interpolation
                displayFill = Mathf.Lerp(displayFill, targetFill, Math.Min(1f, smoothing * deltaTime));
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
                float startXLocal = thumbRect.Right - rect.Left - 45f; // Gap from thumbnail
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
#if DEBUG
                Debug.WriteLine("Update layout error: " + ex.Message);
#endif
            }

            // Handle timeline mouse interactions (seeking)
            try
            {
                // Compute timeline bar rect in screen coordinates
                var rr = GetRect();
                float barWidth = rr.Rect.Width - 2 * timelineSidePadding;
                float barX = rr.Rect.Left + (rr.Rect.Width - barWidth) / 2f;
                float btnBottom = btnPrev != null ? btnPrev.LocalPosition.Y + btnPrev.Size.Y : rr.Rect.Bottom - 20f;
                float barY = rr.Rect.Top + btnBottom + timelineBarPadding;
                if (barY + timelineHeight > rr.Rect.Bottom) barY = rr.Rect.Bottom - timelineHeight - timelineBarPadding;

                var barRect = SKRect.Create(barX, barY, barWidth, timelineHeight);

                var mousePos = RendererMain.CursorPosition;

                // Mouse down over timeline begins seeking
                if (IsHovering && IsMouseDown && !mouseDownOverTimeline && barRect.Contains(mousePos.X, mousePos.Y))
                {
                    mouseDownOverTimeline = true;
                    userIsSeeking = true;
                    // Compute initial seek position
                    // Prefer initialising from current snapshot so drag feels continuous
                    if (timelinePosition.HasValue && timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
                    {
                        userSeekElapsed = timelinePosition.Value;
                        float rel = Math.Clamp((mousePos.X - barX) / barWidth, 0f, 1f);
                        userSeekElapsed = TimeSpan.FromSeconds(rel * timelineDuration.Value.TotalSeconds);
                    }
                }

                // While mouse is down and over timeline, update seek position
                if (mouseDownOverTimeline && IsMouseDown)
                {
                    if (timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
                    {
                        float rel = Math.Clamp((mousePos.X - barX) / barWidth, 0f, 1f);
                        userSeekElapsed = TimeSpan.FromSeconds(rel * timelineDuration.Value.TotalSeconds);
                    }
                }

                // On mouse up, if seeking, commit the seek
                if (mouseDownOverTimeline && !IsMouseDown)
                {
                    mouseDownOverTimeline = false;
                    if (userIsSeeking)
                    {
                        // Compute target absolute position as StartTime + userSeekElapsed
                        TimeSpan? start = currentTimeline?.StartTime;
                        if (start.HasValue)
                        {
                            var target = start.Value + userSeekElapsed;
                            
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var ok = await MediaInfo.SeekCurrentSessionAsync(target).ConfigureAwait(false);
                                    if (ok)
                                    {
                                        // On success, force timeline resync next loop
                                        lock (mediaLock)
                                        {
                                            // Update local snapshot to the seeked position so UI reflects the change immediately
                                            lastSampleElapsed = userSeekElapsed;
                                            lastSampleReceivedAt = DateTime.UtcNow;
                                            lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                                            timelineFetchedOnce = false;
                                        }
                                     }
                                }
                                catch { }
                            });
                        }

                        // Keep userIsSeeking true until after commit to allow UI to show seek state briefly
                        userIsSeeking = false;
                     }
                 }
             }
             catch { }
            
            if (!string.IsNullOrEmpty(currentMedia?.Title))
            {
                fullTitleText = currentMedia.Title;

                // Only re-measure text width when title actually changes
                if (prevFullTitleText != fullTitleText)
                {
                    var paint = GetPaint();
                    paint.TextSize = 14f;
                    paint.Typeface = Resources.Res.SatoshiBold;
                    titleTextWidth = paint.MeasureText(fullTitleText);
                    prevFullTitleText = fullTitleText;
                }

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
                // Ensure we show a clear placeholder when no media title is available
                fullTitleText = "No media playing";

                if (prevFullTitleText != fullTitleText)
                {
                    var paint = GetPaint();
                    paint.TextSize = 14f;
                    paint.Typeface = Res.SatoshiBold;
                    titleTextWidth = paint.MeasureText(fullTitleText);
                    prevFullTitleText = fullTitleText;
                }

                isTitleScrolling = false;
                titleScrollOffset = 0f;
            }

            // Update displayed elapsed seconds using the visible snapshot (sampleElapsed) to avoid second-level jitter
            // sampleElapsed already includes local clock advancement when playback is Playing
            if (sampleElapsed.HasValue && sampleDuration.HasValue)
            {
                float desired = (float)sampleElapsed.Value.TotalSeconds;

                if (userIsSeeking)
                {
                    // When seeking, jump the smoothed value to the seek target for immediate feedback
                    displayedElapsedSeconds = (float)userSeekElapsed.TotalSeconds;
                    displayedElapsedInitialized = true;
                }
                else if (!displayedElapsedInitialized)
                {
                    // Initialise to current value
                    displayedElapsedSeconds = desired;
                    displayedElapsedInitialized = true;
                }
                else
                {
                    // If paused/stopped, snap to sample exactly to avoid backward corrections
                    if (!isPlayingFlag)
                    {
                        displayedElapsedSeconds = desired;
                    }
                    else
                    {
                        // Smoothly interpolate towards desired when playing to keep motion continuous
                        const float smoothing = 12f;
                        displayedElapsedSeconds = Mathf.Lerp(displayedElapsedSeconds, desired, Math.Min(1f, smoothing * deltaTime));
                    }
                }
            }

            // Update hover/seek-driven timeline extra height (smooth transition)
            try
            {
                var rr2 = GetRect();
                float barWidth2 = rr2.Rect.Width - 2 * timelineSidePadding;
                float barX2 = rr2.Rect.Left + (rr2.Rect.Width - barWidth2) / 2f;
                float btnBottom2 = btnPrev != null ? btnPrev.LocalPosition.Y + btnPrev.Size.Y : rr2.Rect.Bottom - 20f;
                float barY2 = rr2.Rect.Top + btnBottom2 + timelineBarPadding;
                if (barY2 + timelineHeight > rr2.Rect.Bottom) barY2 = rr2.Rect.Bottom - timelineHeight - timelineBarPadding;
                var barRect2 = SKRect.Create(barX2, barY2, barWidth2, timelineHeight);
                var mousePos2 = RendererMain.CursorPosition;
                isHoveringOverTimeline = barRect2.Contains(mousePos2.X, mousePos2.Y) && IsHovering;

                float targetExtra = userIsSeeking ? 3f : (isHoveringOverTimeline ? 6f : 0f);
                timelineExtraHeight = Mathf.Lerp(timelineExtraHeight, targetExtra, Math.Min(1f, 12f * deltaTime));
            }
            catch { }
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

                        if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; thumbnailFingerprint = null; }
                        if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingFingerprint = null; }
                        if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }

                        pendingMedia = null;
                        pendingMediaKey = null;

                        // Fresh timeline sample (none) -> cancel optimistic UI
                        optimisticActive = false;
                    }

                    return;
                }

                // Build key similar to prior logic
                string key = (media == null) ? string.Empty : $"{media.Title ?? ""}|{media.Artist ?? ""}|{(bytes?.Length ?? 0)}";

                lock (mediaLock)
                {
                    if (key == currentMediaKey || key == pendingMediaKey)
                    {
                        // Nothing to do; avoid decode
                        return;
                    }
                }

                SKBitmap? newBmp = null;
                ulong? newFp = null;

                // Prefer using the central service decoded bitmap if available to avoid re-decoding bytes repeatedly
                var svcBmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (svcBmp != null)
                {
                    try
                    {
                        var clone = new SKBitmap(svcBmp.Info);
                        svcBmp.CopyTo(clone);
                        newBmp = clone;
                        newFp = ComputeFingerprint(newBmp);
                    }
                    catch
                    {
                        if (newBmp != null) { try { newBmp.Dispose(); } catch { } newBmp = null; }
                        newFp = null;
                    }
                }
                else if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        using var ms = new SKMemoryStream(bytes);
                        var decoded = SKBitmap.Decode(ms);
                        newBmp = decoded;
                        if (newBmp != null) newFp = ComputeFingerprint(newBmp);
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine("Thumbnail decode in service handler failed: " + ex.Message);
#endif
                        if (newBmp != null) { try { newBmp.Dispose(); } catch { } }
                        newBmp = null;
                        newFp = null;
                    }
                }

                lock (mediaLock)
                {
                    // If key now matches current or pending, bail
                    if (key == currentMediaKey || key == pendingMediaKey)
                    {
                        if (newBmp != null) { try { newBmp.Dispose(); } catch { } }
                        return;
                    }

                    // If the incoming bitmap is visually identical to the currently-displayed thumbnail (fingerprint), adopt metadata/key instead
                    if (newFp.HasValue && thumbnailFingerprint.HasValue && newFp.Value == thumbnailFingerprint.Value)
                    {
                        currentMedia = media;
                        currentMediaKey = key;
                        if (newBmp != null) { try { newBmp.Dispose(); } catch { } }
                        pendingMedia = null;
                        pendingMediaKey = null;

                        // Fresh timeline sample arrived -> cancel optimistic UI
                        optimisticActive = false;
                        return;
                    }

                    // If there's no current thumbnail yet, queue as pending to animate in (so first show animates)
                    if (thumbnailBitmap == null && newBmp != null)
                    {
                        pendingBitmap = newBmp;
                        pendingFingerprint = newFp;
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
                                pendingFingerprint = null;
                                pendingMediaKey = null;
                                pendingMedia = null;
                            }

                            pendingBitmap = newBmp;
                            pendingFingerprint = newFp;
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
                                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; thumbnailFingerprint = null; }
                                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingFingerprint = null; }

                                pendingMediaKey = null;
                                pendingMedia = null;
                            }

                            // Fresh timeline sample arrived -> cancel optimistic UI
                            optimisticActive = false;
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
                        // Fetch lightweight timeline sample frequently so UI can update progress smoothly
                        try
                        {
                            bool needFetch = false;
                            // Always allow frequent timeline fetches (MediaInfo already caches at 250ms)
                            if (!userIsSeeking)
                                needFetch = true;

                            // Avoid fetching timeline while user is actively seeking to prevent overwriting the user's drag
                            if (needFetch && !userIsSeeking)
                            {
                                var tl = await MediaInfo.FetchCurrentTimelineAsync().ConfigureAwait(false);
                                if (tl != null)
                                {
#if DEBUG
                                    Debug.WriteLine($"[MEDIA TIMELINE] Pos={tl.Position} Start={tl.StartTime} End={tl.EndTime} Status={tl.PlaybackStatus}");
#endif
                                    lock (mediaLock)
                                    {
                                        // Compute absolute elapsed at sample
                                        var absElapsed = tl.Position - tl.StartTime;
                                        var now = DateTime.UtcNow;

                                        // If we already have a prior sample, perform a drift-tolerant update to avoid jitter
                                        if (lastSampleElapsed.HasValue && lastSampleReceivedAt != DateTime.MinValue)
                                        {
                                            // If the current playback status is not Playing (paused/stopped), anchor exactly to the sample
                                            // This prevents the UI from drifting backwards while paused.
                                            if (tl.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                            {
                                                lastSampleElapsed = absElapsed;
                                                lastSampleReceivedAt = now;
                                            }
                                            else
                                            {
                                                // Predict where the elapsed should be based on our local clock
                                                var predicted = lastSampleElapsed.Value + (now - lastSampleReceivedAt);
                                                // Error = desired - predicted
                                                var error = (absElapsed - predicted).TotalSeconds;

                                                // If error is very large, resync immediately; otherwise gently nudge the anchor
                                                const double largeResyncSeconds = 3.0; // >3s indicates a real discontinuity (seek/track change)
                                                const double nudgeFactor = 0.12; // Fraction of error to apply per sample (slow convergence)

                                                if (Math.Abs(error) > largeResyncSeconds)
                                                {
                                                    // Large discontinuity -> reset anchor to the sample
                                                    lastSampleElapsed = absElapsed;
                                                    lastSampleReceivedAt = now;
                                                    // Reset displayed elapsed as well to avoid a visible jump while converging
                                                    displayedElapsedSeconds = (float)absElapsed.TotalSeconds;
                                                    displayedElapsedInitialized = true;
                                                }
                                                else
                                                {
                                                    // Gentle correction: apply a fraction of the error to the stored anchor so predicted slowly converges
                                                    try
                                                    {
                                                        lastSampleElapsed = lastSampleElapsed.Value + TimeSpan.FromSeconds(error * nudgeFactor);
                                                        // Do NOT update lastSampleReceivedAt; keep local clock anchor so progression remains smooth
                                                        // Update duration to newest value so progress fraction is accurate
                                                        lastSampleDuration = tl.EndTime - tl.StartTime;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Anchor first sample to it
                                            lastSampleElapsed = absElapsed;
                                            lastSampleReceivedAt = now;
                                            lastSampleDuration = tl.EndTime - tl.StartTime;
                                            // Initialise smoothed displayed time to the anchored sample
                                            displayedElapsedSeconds = (float)absElapsed.TotalSeconds;
                                            displayedElapsedInitialized = true;
                                        }

                                        // Always update playback status and currentTimeline reference
                                        lastPlaybackStatus = tl.PlaybackStatus;
                                        currentTimeline = tl;

                                        timelineFetchedOnce = true; // Mark fetched
                                        lastTimelineResync = DateTime.UtcNow;

                                        // Fresh timeline sample arrived -> cancel optimistic UI so external pauses/plays are reflected
                                        optimisticActive = false;
                                    }
                                }
                                else
                                {
                                    // If timeline is null, ensure we clear snapshot so UI hides timeline
                                    lock (mediaLock)
                                    {
                                        lastSampleElapsed = null;
                                        lastSampleDuration = null;
                                        currentTimeline = null;
                                        timelineFetchedOnce = false;

                                        // Cancel optimistic UI when no timeline is available
                                        optimisticActive = false;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            Debug.WriteLine("Timeline fetch error: " + ex.Message);
#endif
                        }

                        var media = await MediaInfo.FetchCurrentMediaAsync();

                        // Build lightweight key to detect duplicates (title|artist|thumbLen)
                        string key = (media == null) ? string.Empty : $"{media.Title ?? ""}|{media.Artist ?? ""}|{(media.ThumbnailData?.Length ?? 0)}";

                        // If key matches current or pending, skip decode entirely
                        if (key == currentMediaKey || key == pendingMediaKey)
                        {
                            // Nothing to do, swallow
                        }
                        else
                        {
                            // Convert thumbnail bytes to SKBitmap on background thread and update fields under lock
                            SKBitmap? newBmp = null;
                            ulong? newFp = null;
                            try
                            {
                                // Prefer using service-decoded bitmap to avoid duplicate decoding when thumbnail service is active
                                var svcBmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                                if (svcBmp != null)
                                {
                                    try
                                    {
                                        var clone = new SKBitmap(svcBmp.Info);
                                        svcBmp.CopyTo(clone);
                                        newBmp = clone;
                                        newFp = ComputeFingerprint(newBmp);
                                    }
                                    catch { if (newBmp != null) { try { newBmp.Dispose(); } catch { } newBmp = null; } newFp = null; }
                                }
                                else if (media?.ThumbnailData != null && media.ThumbnailData.Length > 0)
                                {
                                    using var ms = new MemoryStream(media.ThumbnailData);
                                    newBmp = SKBitmap.Decode(ms);
                                    if (newBmp != null) newFp = ComputeFingerprint(newBmp);
                                }
                            }
                            catch (Exception ex)
                            {
#if DEBUG
                                Debug.WriteLine("Thumbnail decode failed: " + ex.Message);
#endif
                                if (newBmp != null)
                                {
                                    try { newBmp.Dispose(); } catch { }
                                }
                                newBmp = null;
                                newFp = null;
                            }

                            lock (mediaLock)
                            {
                                // If key matches current or pending, skip updates entirely
                                if (key == currentMediaKey || key == pendingMediaKey)
                                {
                                    if (newBmp != null)
                                    {
                                        try { newBmp.Dispose(); } catch { }
                                    }
                                }
                                else
                                {
                                    // If the newly-decoded bitmap fingerprint matches the currently-displayed bitmap, adopt metadata/key
                                    // and avoid queuing an animation.
                                    if (newFp.HasValue && thumbnailFingerprint.HasValue && newFp.Value == thumbnailFingerprint.Value)
                                    {
                                        currentMedia = media;
                                        currentMediaKey = key;
                                        if (newBmp != null) { try { newBmp.Dispose(); } catch { } }

                                        // Fresh timeline sample arrived -> cancel optimistic UI
                                        optimisticActive = false;
                                        continue;
                                    }

                                    // If there's no current thumbnail yet, queue as pending to animate in
                                    if (thumbnailBitmap == null && newBmp != null)
                                    {
                                        pendingBitmap = newBmp;
                                        pendingFingerprint = newFp;
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
                                                pendingFingerprint = null;
                                                pendingMediaKey = null;
                                                pendingMedia = null;
                                            }

                                            pendingBitmap = newBmp;
                                            pendingFingerprint = newFp;
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
                                                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; thumbnailFingerprint = null; }
                                                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
                                                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingFingerprint = null; }

                                                pendingMediaKey = null;
                                                pendingMedia = null;
                                            }

                                            // Fresh timeline sample arrived -> cancel optimistic UI
                                            optimisticActive = false;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine("Media fetch loop error: " + ex.Message);
#endif
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
                    pendingFingerprint = null;
                }

                pendingMediaKey = null;
                pendingMedia = null;
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
            var squirclePath = BuildSuperellipsePath(thumbRect, 30f, 1f);

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
                    var localPath = BuildSuperellipsePath(localRect, 30f, 1f);
                    canvas.Save();
                    canvas.ClipPath(localPath, antialias: Settings.AntiAliasing);

                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
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
                            p.IsAntialias = Settings.AntiAliasing;
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
                        borderPaint.IsAntialias = Settings.AntiAliasing;
                        borderPaint.StrokeWidth = 1.0f;
                        borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(squirclePath, borderPaint);
                    }
                }
                else
                {
                    // Not flipping: draw normally clipped to squircle
                    canvas.Save();
                    canvas.ClipPath(squirclePath, antialias: Settings.AntiAliasing);

                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
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
                            p.IsAntialias = Settings.AntiAliasing;
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
                        borderPaint.IsAntialias = Settings.AntiAliasing;
                        borderPaint.StrokeWidth = 1.0f;
                        borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                        canvas.DrawPath(squirclePath, borderPaint);
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("Draw thumbnail error: " + ex.Message);
#endif
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
                    canvas.ClipRect(SKRect.Create(textX, textY, maxWidth, titlePaint.TextSize + 2f), antialias: Settings.AntiAliasing);

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

            try
            {
                // Timeline bar width reduced to leave room for text
                float barWidth = rect.Width - 2 * timelineSidePadding;
                float barX = rect.Left + (rect.Width - barWidth) / 2f; // horizontally centered

                // Position bar slightly below buttons
                float barY = rect.Top + 95f + timelineBarPadding;

                if (barY + timelineHeight > rect.Bottom) barY = rect.Bottom - timelineHeight - timelineBarPadding;

                // Draw background
                using (var paint = GetPaint())
                {
                    paint.IsStroke = false;
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.Color = timelineBgColor;
                    canvas.DrawRoundRect(SKRect.Create(barX, barY, barWidth, timelineHeight), timelineHeight / 2f, timelineHeight / 2f, paint);
                }

                // Draw foreground fill (height increases when hovering or seeking)
                float drawTimelineHeight = timelineHeight + timelineExtraHeight;
                float barYOffset = (timelineHeight - drawTimelineHeight) / 2f;
                using (var paint = GetPaint())
                {
                    paint.IsStroke = false;
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.Color = GetColor(timelineFgColor).Value();
                    float fillWidth = barWidth * displayFill;
                    canvas.DrawRoundRect(SKRect.Create(barX, barY + barYOffset, fillWidth, drawTimelineHeight), drawTimelineHeight / 2f, drawTimelineHeight / 2f, paint);
                }

                // Draw timeline text on both sides
                string leftText;
                string rightText;

                if (timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
                {
                    // Use smoothed displayedElapsedSeconds for visible text to avoid second-level jitter
                    var leftTs = TimeSpan.FromSeconds(displayedElapsedSeconds);
                    var rightRemain = timelineDuration.Value - TimeSpan.FromSeconds(displayedElapsedSeconds);

                    leftText = FormatTimeSpanForDisplay(leftTs);
                    // Right side shows remaining with a leading '-' to indicate time left
                    rightText = "-" + FormatTimeSpanForDisplay(rightRemain);
                }
                else
                {
                    // No timeline available: show placeholder
                    leftText = "--:--";
                    rightText = "--:--";
                }

                using (var paint = GetPaint())
                {
                    paint.IsStroke = false;
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.Color = GetColor(timelineTextColor).Value();
                    paint.TextSize = timelineTextSize;
                    paint.Typeface = Resources.Res.SatoshiRegular;

                    // Text Y position just below bar
                    float timelineTextY = barY + timelineHeight + timelineTextSize - 10f;

                    // Left text
                    float leftX = barX - timelineSidePadding + 4f;
                    canvas.DrawText(leftText, leftX, timelineTextY, paint);

                    // Right text
                    float rightTextWidth = paint.MeasureText(rightText);
                    float rightX = barX + barWidth + timelineSidePadding - rightTextWidth - 4f;
                    canvas.DrawText(rightText, rightX, timelineTextY, paint);
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("Draw timeline bar error: " + ex.Message);
#endif
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
                if (thumbnailBitmap != null) { try { thumbnailBitmap.Dispose(); } catch { } thumbnailBitmap = null; thumbnailFingerprint = null; }
                if (pendingBitmap != null) { try { pendingBitmap.Dispose(); } catch { } pendingBitmap = null; pendingFingerprint = null; }
                if (previousBitmap != null) { try { previousBitmap.Dispose(); } catch { } previousBitmap = null; }
            }
        }
    }
}
