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
 *   Last Modified:          26 January 2026
 *
 */

namespace DynamicWin.UI.UIElements.Custom
{
    public class MediaPlayer : UIObject
    {
        private CancellationTokenSource? cts;
        private DynamicWin.Utils.Media? currentMedia;
        // Use SKImage for owned renderable images to avoid drawing shared/disposed SKBitmap
        private SKImage? thumbnailImage; // Currently cached decoded image (owned by this object)
        private ulong? thumbnailFingerprint; // Cached fingerprint for image (computed from a temporary SKBitmap during decode)
        private SKImage? pendingImage; // Newly decoded image waiting to animate in
        private ulong? pendingFingerprint;
        private DynamicWin.Utils.Media? pendingMedia; // Pending metadata object
        private readonly object mediaLock = new object();
        // Guard to ensure only one decode loop runs at a time
        private int thumbnailDecodeRunning = 0;
        // How often the background loop waits between iterations (cooperative wait broken into steps)
        private TimeSpan fetchInterval = TimeSpan.FromMilliseconds(250); // faster timeline updates

        // Rate-limited service bytes and flags to make thumbnail processing
        private volatile byte[]? pendingThumbnailBytesFromService = null; // bytes handed to us by service events
        private volatile bool mediaNeedsUpdate = false; // set by service event when thumbnail changed
        private DateTime lastMediaCheck = DateTime.MinValue;
        private TimeSpan mediaCheckInterval = TimeSpan.FromSeconds(2); // only decode/check media every 2s
        // Debounce short-lived 'no media' signals to avoid flicker when service emits transient nulls
        private DateTime mediaClearRequestedAt = DateTime.MinValue;
        private readonly TimeSpan mediaClearDelay = TimeSpan.FromSeconds(1);

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
        private SKImage? previousImage = null; // Image that is being replaced

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
        private readonly SKColor timelineBgColor; // subtle background
        private Col timelineFgColor = Theme.TextMain; // active fill
        private float timelineBarPadding = 12f; // vertical padding below buttons
        private float timelineSidePadding = 40f; // space on left/right for timeline text
        private Col timelineTextColor = Theme.TextMain.Override(a: 55);
        private float timelineTextSize = 10f;
        private DWProgressBarEx? timelineBar;

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
                // Determine desired action (play or pause)
                bool currentlyPlaying = GetEffectivePlayingState();
                bool willPlay = !currentlyPlaying;

                optimisticState = willPlay;
                optimisticActive = true;

                // Update icon immediately
                if (btnPlay != null)
                {
                    btnPlay.Image.Image = optimisticState ? (Res.Pause ?? Res.Stop) : Res.Play;
                }

                // Try WinRT play/pause separately (Play vs Pause) and force a timeline refresh afterwards
                _ = Task.Run(async () =>
                {
                    try
                    {
                        bool ok;
                        if (willPlay)
                            ok = await MediaInfo.TryPlayAsync().ConfigureAwait(false);
                        else
                            ok = await MediaInfo.TryPauseAsync().ConfigureAwait(false);

                        if (!ok)
                        {
                            // Fallback toggle if specific call isn't supported
                            controller.PlayPause();
                        }

                        // Immediately update local timeline/playback state optimistically so UI responds fast
                        try
                        {
                            lock (mediaLock)
                            {
                                var now = DateTime.UtcNow;
                                if (willPlay)
                                {
                                    // Resume: mark as playing and record reference time so virtual progression continues from lastSampleElapsed
                                    if (!lastSampleElapsed.HasValue)
                                    {
                                        // If there's no sample, set to zero
                                        lastSampleElapsed = TimeSpan.Zero;
                                    }
                                    lastSampleReceivedAt = now;
                                    lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                                    optimisticActive = false;
                                    timelineFetchedOnce = true;
                                }
                                else
                                {
                                    // Pause: capture current elapsed and mark paused so virtual progression stops
                                    if (!lastSampleElapsed.HasValue)
                                    {
                                        lastSampleElapsed = TimeSpan.Zero;
                                    }

                                    if (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && lastSampleReceivedAt != DateTime.MinValue)
                                    {
                                        try
                                        {
                                            lastSampleElapsed = lastSampleElapsed.Value + (now - lastSampleReceivedAt);
                                        }
                                        catch { }
                                    }

                                    lastSampleReceivedAt = now;
                                    lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                                    optimisticActive = false;
                                    timelineFetchedOnce = true;
                                }
                            }
                        }
                        catch { }
                    }
                    catch
                    {
                        try { controller.PlayPause(); } catch { }
                    }
                });
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

            // Timeline progress bar (created as local object; size/pos updated in Update)
            try
            {
                timelineBar = new DWProgressBarEx(this, new Vec2(0, 0), new Vec2(200, timelineHeight), UIAlignment.TopCenter,
                    background: Theme.WidgetBackground.Override(a: 0.06f), foreground: timelineFgColor);
                timelineBar.CornerRadius = timelineHeight / 2f;
                timelineBar.Smoothing = 30f;
                timelineBar.SetValueImmediate(0f);
                AddLocalObject(timelineBar);
            }
            catch { timelineBar = null; }

            // Subscribe to central thumbnail service event
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;

            // Try to initialise thumbnail from service cache so it doesn't disappear when re-opening
            try
            {
                var bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                if (bytes != null && bytes.Length > 0)
                {
                    var img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(bytes, out ulong? fp);
                    if (img != null)
                    {
                        lock (mediaLock)
                        {
                            thumbnailImage = img;
                            thumbnailFingerprint = fp;
                        }
                    }
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

                // If service has a cached bitmap bytes, ensure it's used (queue as pending to animate in)
                var bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        var img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(bytes, out ulong? fp);
                        if (img != null)
                        {
                            lock (mediaLock)
                            {
                                if (thumbnailImage == null)
                                {
                                    pendingImage = img;
                                    pendingFingerprint = fp;
                                    pendingMedia = null;
                                    pendingMediaKey = null;
                                }
                                else
                                {
                                    try { thumbnailImage.Dispose(); } catch { }
                                    thumbnailImage = img;
                                    thumbnailFingerprint = fp;
                                }
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // No cached service bitmap yet - do a one-shot fetch so first-open has a thumbnail.
                    Task.Run(async () =>
                    {
                        try
                        {
                            var b = await MediaInfo.FetchCurrentThumbnailBytesAsync().ConfigureAwait(false);
                            var meta = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);

                            if (b != null && b.Length > 0)
                            {
                                var img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(b, out ulong? fp);
                                if (img != null)
                                {
                                    lock (mediaLock)
                                    {
                                        if (thumbnailImage != null && thumbnailFingerprint.HasValue && thumbnailFingerprint.Value == fp)
                                        {
                                            currentMedia = meta;
                                            currentMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{b.Length}";
                                            optimisticActive = false;
                                        }
                                        else
                                        {
                                            if (thumbnailImage == null && pendingImage == null)
                                            {
                                                pendingImage = img;
                                                pendingFingerprint = fp;
                                                pendingMedia = meta;
                                                pendingMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{b.Length}";
                                            }
                                            else
                                            {
                                                if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } }
                                                pendingImage = img;
                                                pendingFingerprint = fp;
                                                pendingMedia = meta;
                                                pendingMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|{b.Length}";
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // If no thumbnail bytes but metadata is available, adopt the metadata so title/artist and
                                // timeline information are shown immediately even when a thumbnail hasn't been provided.
                                // This prevents the UI from showing empty text while MediaController has already fetched metadata.
                                if (meta != null)
                                {
                                    lock (mediaLock)
                                    {
                                        currentMedia = meta;
                                        currentMediaKey = (meta == null) ? string.Empty : $"{meta.Title ?? ""}|{meta.Artist ?? ""}|0";
                                        optimisticActive = false;
                                    }
                                }

                                if (meta == null)
                                {
                                    lock (mediaLock)
                                    {
                                        if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                                        if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; }
                                        if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
                                        currentMediaKey = null;
                                        pendingMediaKey = null;
                                        currentMedia = null;

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

            // Use null checks instead, exceptions kill performance on Update()
            bool visible = false;
            var home = Res.HomeMenu;
            if (home != null &&
                home.currentBigMenuMode == HomeMenu.BigMenuMode.Media &&
                RendererMain.Instance?.MainIsland != null && // Null check Instance
                RendererMain.Instance.MainIsland.IsHovering)
            {
                visible = true;
            }

            if (visible)
            {
                if (cts == null) StartFetchLoop();
            }
            else
            {
                if (cts != null) StopFetchLoop();
                return; // Skip the rest if not visible
            }

            // Geometry and caching step
            // Calculate these ONCE per frame to reuse in Layout, Seek, and Hover logic
            var widgetBounds = GetRect().Rect; // Call GetRect only once

            // Pre-calculate bar geometry used for both seeking and hovering
            float barWidth = widgetBounds.Width - 2 * timelineSidePadding;
            // Safety check for negative width
            if (barWidth < 1f) barWidth = 1f;

            float barX = widgetBounds.Left + (widgetBounds.Width - barWidth) / 2f;

            // Determine button bottom safely
            float btnBottom = (btnPrev != null)
                ? (btnPrev.LocalPosition.Y + btnPrev.Size.Y)
                : (widgetBounds.Bottom - 20f);

            float barY = widgetBounds.Top + btnBottom + timelineBarPadding;
            if (barY + timelineHeight > widgetBounds.Bottom)
                barY = widgetBounds.Bottom - timelineHeight - timelineBarPadding;

            var timelineBarRect = SKRect.Create(barX, barY, barWidth, timelineHeight);
            var mousePos = RendererMain.CursorPosition;


            // Animator step
            // (If possible, cache these delegates as fields to avoid per-frame GC allocation)
            animator.Update(deltaTime,
                () => { lock (mediaLock) { return pendingImage != null; } },
                onStart: () => { lock (mediaLock) { previousImage = thumbnailImage; } },
                onMidFlip: () =>
                {
                    lock (mediaLock)
                    {
                        thumbnailImage?.Dispose();
                        thumbnailImage = pendingImage;
                        pendingImage = null;
                        pendingFingerprint = null;

                        currentMediaKey = pendingMediaKey;
                        pendingMediaKey = null;

                        if (pendingMedia != null)
                        {
                            currentMedia = pendingMedia;
                            pendingMedia = null;
                            optimisticActive = false;
                            timelineFetchedOnce = false;
                            lastTimelineResync = DateTime.MinValue;
                        }
                    }
                },
                onFinish: () =>
                {
                    if (previousImage != null)
                    {
                        try { previousImage.Dispose(); } catch { }
                        previousImage = null;
                    }
                });


            // Timeline snapshot logic
            TimeSpan? sampleElapsed = null;
            TimeSpan? sampleDuration = null;

            lock (mediaLock)
            {
                if (lastSampleElapsed.HasValue && lastSampleDuration.HasValue && lastSampleReceivedAt != DateTime.MinValue)
                {
                    sampleDuration = lastSampleDuration;

                    if (userIsSeeking)
                    {
                        sampleElapsed = userSeekElapsed;
                    }
                    else
                    {
                        // Only do the DateTime math if we are actually playing
                        if (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            sampleElapsed = lastSampleElapsed.Value + (DateTime.UtcNow - lastSampleReceivedAt);
                        }
                        else
                        {
                            sampleElapsed = lastSampleElapsed.Value;
                        }
                    }

                    // Sync external timeline object
                    if (currentTimeline != null)
                    {
                        // Only write if values actually changed to avoid property notify overhead
                        // (Assuming currentTimeline checks for equality, otherwise simple assignment is fine)
                        currentTimeline.Position = currentTimeline.StartTime + sampleElapsed.Value;
                        currentTimeline.EndTime = currentTimeline.StartTime + sampleDuration.Value;
                        currentTimeline.PlaybackStatus = lastPlaybackStatus;
                    }
                }
                else
                {
                    optimisticActive = false;
                    currentTimeline = null;
                }
            }

            // Timeline update logic
            if (sampleElapsed.HasValue && sampleDuration.HasValue)
            {
                timelinePosition = sampleElapsed.Value;
                timelineDuration = sampleDuration.Value;

                if (timelinePosition < TimeSpan.Zero) timelinePosition = TimeSpan.Zero;

                isPlayingFlag = (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

                if (timelinePosition > timelineDuration) timelinePosition = timelineDuration;
            }
            else
            {
                timelinePosition = null;
                timelineDuration = null;
                isPlayingFlag = false;
                lastSampleKey = null;
            }


            // Visual interpolation
            float targetFill = 0f;
            bool haveTarget = false;

            // Use cached duration total seconds to avoid repeated property access
            double totalDurSeconds = (timelineDuration.HasValue) ? timelineDuration.Value.TotalSeconds : 0;

            if (totalDurSeconds > 0)
            {
                if (userIsSeeking)
                {
                    targetFill = Math.Clamp((float)(userSeekElapsed.TotalSeconds / totalDurSeconds), 0f, 1f);
                    haveTarget = true;
                }
                else if (timelinePosition.HasValue)
                {
                    targetFill = Math.Clamp((float)(timelinePosition.Value.TotalSeconds / totalDurSeconds), 0f, 1f);
                    haveTarget = true;
                }
            }

            if (haveTarget)
            {
                // 30f * deltaTime is simple, but ensure deltaTime isn't huge (spike protection)
                float t = Math.Min(1f, 30f * deltaTime);
                displayFill = Mathf.Lerp(displayFill, targetFill, t);
            }


            // Updating layout
            // Use widgetBounds
            float padding = 0f;
            float thumbSize = Math.Min(widgetBounds.Height - padding * 2f, widgetBounds.Height * 1f);
            SKRect thumbRect = SKRect.Create(widgetBounds.Left + padding, widgetBounds.Top + padding, thumbSize, thumbSize);

            // Pre-calculate common layout values
            float btnSize = 28f;
            float btnSpacing = 8f;
            float buttonsYOffset = 16f + 14f + 12f + 24f; // titleY + titleH + artistH + gap
            float startXLocal = thumbRect.Right - widgetBounds.Left - 45f;

            // Set positions (null checks instead of try-catch)
            if (btnPrev != null) btnPrev.LocalPosition = new Vec2(startXLocal, buttonsYOffset);
            if (btnPlay != null)
            {
                btnPlay.LocalPosition = new Vec2(startXLocal + (btnSize + btnSpacing), buttonsYOffset);

                // Icon update
                bool effectivePlaying = GetEffectivePlayingState();
                var icon = effectivePlaying ? (Resources.Res.Pause ?? Resources.Res.Stop) : Resources.Res.Play;

                // Only update the image if it actually changed (avoids invalidation overhead)
                if (btnPlay.Image.Image != icon)
                {
                    btnPlay.Image.Image = icon;
                    btnPlay.Image.Color = Theme.IconColor;
                }
            }
            if (btnNext != null) btnNext.LocalPosition = new Vec2(startXLocal + 2 * (btnSize + btnSpacing), buttonsYOffset);


            // Input handling for seeking and hover states
            // Logic consolidated to use 'timelineBarRect'
            bool isMouseInBar = timelineBarRect.Contains(mousePos.X, mousePos.Y);

            // Hover state
            isHoveringOverTimeline = isMouseInBar && IsHovering && !timelineBar.IsLocked;

            // Seeking state
            // Mouse down (start seek)
            if (IsHovering && IsMouseDown && !mouseDownOverTimeline && isMouseInBar && !timelineBar.IsLocked)
            {
                mouseDownOverTimeline = true;
                userIsSeeking = true;

                // Calculate initial seek
                if (totalDurSeconds > 0)
                {
                    userSeekElapsed = timelinePosition ?? TimeSpan.Zero;
                    float rel = Math.Clamp((mousePos.X - barX) / barWidth, 0f, 1f);
                    userSeekElapsed = TimeSpan.FromSeconds(rel * totalDurSeconds);
                }
            }

            // Dragging
            if (mouseDownOverTimeline && IsMouseDown)
            {
                if (totalDurSeconds > 0)
                {
                    float rel = Math.Clamp((mousePos.X - barX) / barWidth, 0f, 1f);
                    userSeekElapsed = TimeSpan.FromSeconds(rel * totalDurSeconds);
                }
            }

            // Mouse Up (Commit)
            if (mouseDownOverTimeline && !IsMouseDown)
            {
                mouseDownOverTimeline = false;
                if (userIsSeeking)
                {
                    TimeSpan? start = currentTimeline?.StartTime;
                    if (start.HasValue)
                    {
                        var target = start.Value + userSeekElapsed;

                        // Fire and forget task
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var ok = await MediaInfo.SeekCurrentSessionAsync(target).ConfigureAwait(false);
                                if (ok)
                                {
                                    lock (mediaLock)
                                    {
                                        lastSampleElapsed = userSeekElapsed;
                                        lastSampleReceivedAt = DateTime.UtcNow;
                                        lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                                        timelineFetchedOnce = false;
                                    }
                                }
                            }
                            catch { } // Task exception is isolated here
                        });
                    }
                    userIsSeeking = false;
                }
            }

            string newTitle = !string.IsNullOrEmpty(currentMedia?.Title) ? currentMedia.Title : "No media playing";

            if (fullTitleText != newTitle)
            {
                fullTitleText = newTitle;
                // Only measure when string changes
                var paint = GetPaint();
                paint.TextSize = 14f;
                paint.Typeface = Res.SatoshiBold; // Accessing Res property might be slight overhead, ensure cached if possible
                titleTextWidth = paint.MeasureText(fullTitleText);

                // Reset scroll on change
                isTitleScrolling = false;
                titleScrollOffset = 0f;
                titleScrollTimer = 0f;
            }

            if (fullTitleText.Length > titleScrollCharThreshold)
            {
                isTitleScrolling = true;
                if (titleScrollTimer < titleScrollDelay)
                {
                    titleScrollTimer += deltaTime;
                }
                else
                {
                    titleScrollOffset += titleScrollSpeed * deltaTime;
                    if (titleScrollOffset > titleTextWidth + 20f)
                    {
                        titleScrollOffset = 0f;
                        titleScrollTimer = 0f;
                    }
                }
            }


            // Elapsed time display
            // Use the values previously calculated to avoid recalculating
            if (sampleElapsed.HasValue && sampleDuration.HasValue)
            {
                float desired = (float)(userIsSeeking ? userSeekElapsed.TotalSeconds : sampleElapsed.Value.TotalSeconds);

                if (userIsSeeking || !displayedElapsedInitialized)
                {
                    displayedElapsedSeconds = desired;
                    displayedElapsedInitialized = true;
                }
                else
                {
                    if (!isPlayingFlag)
                    {
                        displayedElapsedSeconds = desired;
                    }
                    else
                    {
                        displayedElapsedSeconds = Mathf.Lerp(displayedElapsedSeconds, desired, Math.Min(1f, 12f * deltaTime));
                    }
                }
            }

            // Re-use calculation
            float targetExtra = userIsSeeking ? 3f : (isHoveringOverTimeline ? 6f : 0f);
            timelineExtraHeight = Mathf.Lerp(timelineExtraHeight, targetExtra, Math.Min(1f, 12f * deltaTime));
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            // Record the raw bytes and metadata; decoding is deferred and rate-limited in the fetch loop.
            try
            {
                var bytes = e.ThumbnailBytes;
                var media = e.Media;

                lock (mediaLock)
                {
                    // If there's metadata, adopt it immediately so UI updates without waiting for decode
                    if (media != null)
                    {
                        currentMedia = media;
                        currentMediaKey = (media == null) ? string.Empty : $"{media.Title ?? ""}|{media.Artist ?? ""}|{(bytes?.Length ?? 0)}";
                        optimisticActive = false;
                        timelineFetchedOnce = false;
                        lastTimelineResync = DateTime.MinValue;
                    }

                    if ((media == null) && (bytes == null || bytes.Length == 0))
                    {
                        // Service indicates no media; request a delayed clear to avoid transient wipe
                        pendingThumbnailBytesFromService = null;
                        pendingMedia = null;
                        mediaNeedsUpdate = true;
                        mediaClearRequestedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        // If bytes exist, prefer them for decoding; metadata is already adopted above
                        // keep a copy in the service buffer as a fallback
                        pendingThumbnailBytesFromService = (bytes != null && bytes.Length > 0) ? (byte[])bytes.Clone() : null;
                        pendingMedia = media;
                        mediaNeedsUpdate = true;
                        mediaClearRequestedAt = DateTime.MinValue;
                    }
                }

                // If bytes are present, decode immediately in background so thumbnail updates alongside metadata
                if (bytes != null && bytes.Length > 0)
                {
                    // Ensure the latest bytes are stored for the decode loop to consume
                    // A single decode loop will coalesce rapid updates and avoid spawning many Tasks
                    // Use Interlocked to start the loop only once
                    // pendingThumbnailBytesFromService already set above
                    if (Interlocked.CompareExchange(ref thumbnailDecodeRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (true)
                                {
                                    byte[]? localBytes = null;
                                    DynamicWin.Utils.Media? localMedia = null;

                                    lock (mediaLock)
                                    {
                                        if (pendingThumbnailBytesFromService != null && pendingThumbnailBytesFromService.Length > 0)
                                        {
                                            localBytes = (byte[])pendingThumbnailBytesFromService.Clone();
                                            localMedia = pendingMedia;
                                            // Mark consumed - keep metadata in pendingMedia until animator swaps
                                            pendingThumbnailBytesFromService = null;
                                            mediaNeedsUpdate = false;
                                        }
                                    }

                                    if (localBytes == null)
                                    {
                                        // No work to do; break out and allow new loop to be started on next event
                                        break;
                                    }

                                    SKImage? img = null;
                                    ulong? fp = null;
                                    try
                                    {
                                        img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(localBytes, out fp);
                                    }
                                    catch { img = null; fp = null; }

                                    if (img != null)
                                    {
                                        lock (mediaLock)
                                        {
                                            string key = (localMedia == null) ? string.Empty : $"{localMedia.Title ?? ""}|{localMedia.Artist ?? ""}|{localBytes.Length}";

                                            if (fp.HasValue && thumbnailFingerprint.HasValue && fp.Value == thumbnailFingerprint.Value)
                                            {
                                                // Visually identical - adopt metadata only
                                                if (localMedia != null)
                                                {
                                                    currentMedia = localMedia;
                                                    currentMediaKey = key;
                                                }
                                                optimisticActive = false;
                                                try { img.Dispose(); } catch { }
                                            }
                                            else
                                            {
                                                // Queue as pending for animator; replace any existing pending
                                                if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; pendingMediaKey = null; pendingMedia = null; }

                                                pendingImage = img;
                                                pendingFingerprint = fp;
                                                pendingMedia = localMedia;
                                                pendingMediaKey = key;
                                            }
                                        }
                                    }

                                    // Debounce window: wait a short amount to allow more rapid updates to arrive and be coalesced
                                    try { await Task.Delay(200).ConfigureAwait(false); } catch { }
                                }
                            }
                            finally
                            {
                                System.Threading.Interlocked.Exchange(ref thumbnailDecodeRunning, 0);
                            }
                        });
                    }
                }
            }
            catch { }
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
                        // Timeline fetch: only perform an initial fetch when we do not yet have a timeline sample
                        // We intentionally avoid periodic re-syncs here so the UI advances virtually and only
                        // Corrects itself when an explicit action forces a refresh (metadata change or play/pause)
                        try
                        {
                            if (!userIsSeeking && !timelineFetchedOnce)
                            {
                                var tl = await MediaInfo.FetchCurrentTimelineAsync().ConfigureAwait(false);
                                if (tl != null)
                                {
                                    lock (mediaLock)
                                    {
                                        var absElapsed = tl.Position - tl.StartTime;
                                        var now = DateTime.UtcNow;

                                        lastSampleElapsed = absElapsed;
                                        lastSampleReceivedAt = now;
                                        lastSampleDuration = tl.EndTime - tl.StartTime;
                                        lastPlaybackStatus = tl.PlaybackStatus;
                                        currentTimeline = tl;

                                        timelineFetchedOnce = true;
                                        lastTimelineResync = DateTime.UtcNow;

                                        optimisticActive = false;
                                    }
                                }
                                else
                                {
                                    lock (mediaLock)
                                    {
                                        lastSampleElapsed = null;
                                        lastSampleDuration = null;
                                        currentTimeline = null;
                                        timelineFetchedOnce = false;

                                        optimisticActive = false;
                                    }
                                }
                            }
                        }
                        catch (Exception) { }

                        byte[]? svcBytes = null;
                        DynamicWin.Utils.Media? svcMedia = null;

                        // Only attempt media/thumbnail decoding occasionally or when service signalled a change
                        bool shouldProcessMedia = false;
                        try
                        {
                            if (mediaNeedsUpdate || (DateTime.UtcNow - lastMediaCheck) >= mediaCheckInterval)
                                shouldProcessMedia = true;
                        }
                        catch { shouldProcessMedia = false; }

                        if (shouldProcessMedia)
                        {
                            lastMediaCheck = DateTime.UtcNow;

                            // Prefer bytes captured from service event (cheap) over querying MediaInfo repeatedly
                            lock (mediaLock)
                            {
                                if (pendingThumbnailBytesFromService != null && pendingThumbnailBytesFromService.Length > 0)
                                {
                                    svcBytes = (byte[])pendingThumbnailBytesFromService.Clone();
                                    svcMedia = pendingMedia; // adopt whatever metadata was provided
                                    // mark as consumed; decoding still controlled by loop
                                    mediaNeedsUpdate = false;
                                }
                                else if (mediaNeedsUpdate && pendingMedia != null)
                                {
                                    // Service signalled a change but did not provide bytes (metadata-only update)
                                    // Use the pending metadata directly to avoid relying on MediaInfo cache
                                    svcBytes = null;
                                    svcMedia = pendingMedia;
                                    mediaNeedsUpdate = false;
                                }
                            }

                            // If no bytes came from service events, only then query MediaInfo (infrequent)
                            if (svcBytes == null)
                            {
                                try
                                {
                                    var media = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);
                                    svcMedia = media;
                                    svcBytes = media?.ThumbnailData != null && media.ThumbnailData.Length > 0 ? (byte[])media.ThumbnailData.Clone() : null;
                                }
                                catch { svcBytes = null; svcMedia = null; }
                            }

                            // Build lightweight key to detect duplicates
                            string key = (svcMedia == null) ? string.Empty : $"{svcMedia.Title ?? ""}|{svcMedia.Artist ?? ""}|{(svcBytes?.Length ?? 0)}";

                            // If key matches current or pending, skip decode
                            if (key == currentMediaKey || key == pendingMediaKey)
                            {
                                // nothing to do
                                if (svcBytes != null) { /*keep bytes for later*/ }
                            }
                            else
                            {
                                // Decode bytes into SKImage (rate-limited) only when we have new bytes
                                if (svcBytes != null && svcBytes.Length > 0)
                                {
                                    SKImage? img = null;
                                    ulong? fp = null;
                                    try
                                    {
                                        img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(svcBytes, out fp);
                                    }
                                    catch { img = null; fp = null; }

                                    lock (mediaLock)
                                    {
                                        if (img != null)
                                        {
                                            // If visually identical to displayed image by fingerprint, adopt metadata instead
                                            if (fp.HasValue && thumbnailFingerprint.HasValue && fp.Value == thumbnailFingerprint.Value)
                                            {
                                                // If the decoded image fingerprint matches the currently displayed thumbnail,
                                                // adopt the metadata only if it's provided. Do NOT clear currentMedia when svcMedia is null
                                                if (svcMedia != null)
                                                {
                                                    currentMedia = svcMedia;
                                                    currentMediaKey = key;
                                                }
                                                optimisticActive = false;
                                                try { img.Dispose(); } catch { }
                                            }
                                            else
                                            {
                                                // Queue as pending (replace any existing pending)
                                                if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; pendingMediaKey = null; pendingMedia = null; }

                                                pendingImage = img;
                                                pendingFingerprint = fp;
                                                pendingMedia = svcMedia;
                                                pendingMediaKey = key;

                                                // Do not set thumbnailImage here; animator will swap on flip
                                            }
                                        }
                                        else
                                        {
                                            // No image decoded: if there's metadata but no bytes, adopt metadata if changed
                                            // Only adopt metadata when svcMedia is non-null. Avoid clearing currentMedia when media lookup returned null
                                            if ((svcBytes == null || svcBytes.Length == 0) && svcMedia != null && key != currentMediaKey && pendingMediaKey == null)
                                            {
                                                currentMedia = svcMedia;
                                                currentMediaKey = key;
                                                optimisticActive = false;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception) { }

                    int totalMs = (int)fetchInterval.TotalMilliseconds;
                    int waited = 0;
                    const int step = 250;

                    while (waited < totalMs && !token.IsCancellationRequested)
                    {
                        int delay = Math.Min(step, totalMs - waited);
                        try { await Task.Delay(delay).ConfigureAwait(false); } catch { }
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

            lock (mediaLock)
            {
                if (pendingImage != null)
                {
                    try { pendingImage.Dispose(); } catch { }
                    pendingImage = null;
                    pendingFingerprint = null;
                }

                pendingMediaKey = null;
                pendingMedia = null;
            }
        }

        public override void Draw(SKCanvas canvas)
        {
            // Extra visibility guard
            try
            {
                var home = Res.HomeMenu;
                if (home == null) return;
                if (home.currentBigMenuMode != HomeMenu.BigMenuMode.Media) return;
                if (!RendererMain.Instance.MainIsland.IsHovering) return;
            }
            catch { return; }

            if (!IsEnabled) return;
            if (Parent != null && !Parent.IsEnabled) return;

            var rr = GetRect();
            var rect = rr.Rect;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            float maxThumb = Math.Min(90f, rect.Width * 0.35f);
            float thumbSize = Math.Min(Math.Min(rect.Height * 2f, rect.Height * 1f), maxThumb);
            thumbSize = Math.Max(12f, thumbSize);
            float thumbRadius = Math.Max(12f, thumbSize * 0.18f);
            SKRect thumbRect = SKRect.Create(rect.Left, rect.Top, thumbSize, thumbSize);

            string title = "No media playing";
            string artist = "No media playing";
            SKImage? img = null;

            lock (mediaLock)
            {
                if (currentMedia != null)
                {
                    title = currentMedia.Title ?? "No media playing";
                    artist = currentMedia.Artist ?? "No media playing";
                }
                img = thumbnailImage;
            }

            if (img == null && string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist)) return;

            SKImage? displayImg = img;
            SKImage? prevImg = previousImage;

            var squirclePath = BuildSuperellipsePath(thumbRect, 30f, 1f);

            try
            {
                float flipScale = animator.GetFlipScale();
                bool doFlip = animator.IsFlipping;
                int save = canvas.Save();

                if (doFlip)
                {
                    float cx = thumbRect.MidX;
                    float cy = thumbRect.MidY;
                    canvas.Translate(cx, cy);
                    canvas.Scale(flipScale, 1f);
                    var localRect = SKRect.Create(-thumbSize / 2f, -thumbSize / 2f, thumbSize, thumbSize);
                    var localPath = BuildSuperellipsePath(localRect, 30f, 1f);
                    canvas.Save();
                    canvas.ClipPath(localPath, antialias: Settings.AntiAliasing);

                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.IsStroke = false;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    paint.BlendMode = SKBlendMode.SrcOver;

                    if (displayImg != null)
                    {
                        try { canvas.DrawImage(displayImg, localRect, paint); } catch { }
                    }
                    else
                    {
                        using var p = GetPaint();
                        p.IsAntialias = Settings.AntiAliasing;
                        p.IsStroke = false;
                        p.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        p.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                        p.BlendMode = SKBlendMode.SrcOver;
                        canvas.DrawRoundRect(new SKRoundRect(localRect, thumbRadius), p);
                    }

                    canvas.Restore();
                    canvas.RestoreToCount(save);

                    using var borderPaint = GetPaint();
                    borderPaint.IsStroke = true;
                    borderPaint.IsAntialias = Settings.AntiAliasing;
                    borderPaint.StrokeWidth = 1.0f;
                    borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                    canvas.DrawPath(squirclePath, borderPaint);
                }
                else
                {
                    canvas.Save();
                    canvas.ClipPath(squirclePath, antialias: Settings.AntiAliasing);

                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.IsStroke = false;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    paint.BlendMode = SKBlendMode.SrcOver;

                    if (displayImg != null)
                    {
                        try { canvas.DrawImage(displayImg, thumbRect, paint); } catch { }
                    }
                    else
                    {
                        using var p = GetPaint();
                        p.IsAntialias = Settings.AntiAliasing;
                        p.IsStroke = false;
                        p.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
                        p.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                        p.BlendMode = SKBlendMode.SrcOver;
                        canvas.DrawRoundRect(new SKRoundRect(thumbRect, thumbRadius), p);
                    }

                    canvas.Restore();

                    using var borderPaint = GetPaint();
                    borderPaint.IsStroke = true;
                    borderPaint.IsAntialias = Settings.AntiAliasing;
                    borderPaint.StrokeWidth = 1.0f;
                    borderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.08f)).Value();
                    canvas.DrawPath(squirclePath, borderPaint);
                }
            }
            catch { }

            // Draw texts and timeline (reuse existing code from earlier)
            float textX = thumbRect.Right + 14f;
            float textY = rect.Top + 16f;

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
                float maxWidth = rect.Width - (textX - rect.Left) - 45f;
                if (isTitleScrolling)
                {
                    canvas.Save();
                    canvas.ClipRect(SKRect.Create(textX, textY, maxWidth, titlePaint.TextSize + 2f), antialias: Settings.AntiAliasing);
                    float xPos = textX - titleScrollOffset;
                    canvas.DrawText(fullTitleText, xPos, textY + titlePaint.TextSize, titlePaint);
                    if (xPos + titleTextWidth < textX + maxWidth)
                    {
                        canvas.DrawText(fullTitleText, xPos + titleTextWidth + 20f, textY + titlePaint.TextSize, titlePaint);
                    }
                    canvas.Restore();
                }
                else
                {
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
                float barWidth = rect.Width - 2 * timelineSidePadding;
                float barX = rect.Left + (rect.Width - barWidth) / 2f;
                float barY = rect.Top + 95f + timelineBarPadding;
                if (barY + timelineHeight > rect.Bottom) barY = rect.Bottom - timelineHeight - timelineBarPadding;

                float drawTimelineHeight = timelineHeight + timelineExtraHeight;

                // If we have a DWProgressBarEx instance, position it and draw it
                if (timelineBar != null)
                {
                    // Set size and local position relative to this object's rect
                    timelineBar.Size = new Vec2(barWidth, drawTimelineHeight);
                    timelineBar.LocalPosition = new Vec2(barX - rect.Left - 40f, barY - rect.Top + 3.5f);
                    timelineBar.CornerRadius = drawTimelineHeight / 2f;
                    // timelineBar target value is driven from Update to respect locking; do not set Value here.
                    timelineBar.ForegroundColor = timelineFgColor.Override(a: 0.6f);
                    timelineBar.BackgroundColor = Theme.WidgetBackground.Override(a: 0.04f);
                    // If there's no media playing, lock and force the bar to zero immediately
                    if (currentMedia == null)
                    {
                        timelineBar.IsLocked = true;
                        timelineBar.ForceSetImmediate(0f);
                    }
                    else
                    {
                        timelineBar.IsLocked = false;
                        // Drive the target value so smoothing animates the visual
                        timelineBar.ForceSetValue(displayFill);
                    }

                    // Draw the progress bar as a child at the computed location
                    timelineBar.Draw(canvas);
                }

                string leftText;
                string rightText;

                if (timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0)
                {
                    var leftTs = TimeSpan.FromSeconds(displayedElapsedSeconds);
                    var rightRemain = timelineDuration.Value - TimeSpan.FromSeconds(displayedElapsedSeconds);
                    leftText = FormatTimeSpanForDisplay(leftTs);
                    rightText = "-" + FormatTimeSpanForDisplay(rightRemain);
                }
                else
                {
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

                    float timelineTextY = barY + timelineHeight + timelineTextSize - 10f;
                    float leftX = barX - timelineSidePadding + 4f;
                    canvas.DrawText(leftText, leftX, timelineTextY, paint);

                    float rightTextWidth = paint.MeasureText(rightText);
                    float rightX = barX + barWidth + timelineSidePadding - rightTextWidth - 4f;
                    canvas.DrawText(rightText, rightX, timelineTextY, paint);
                }
            }
            catch { }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChanged; } catch { }

            StopFetchLoop();

            lock (mediaLock)
            {
                if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; }
                if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
            }
        }

    }
}
