using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.Utils;
using SkiaSharp;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
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
 *   Last Modified:          13 May 2026
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
        // How often the background loop waits between iterations (cooperative wait broken into steps)
        private readonly TimeSpan fetchInterval = TimeSpan.FromMilliseconds(250); // faster timeline updates

        // Rate-limited service bytes and flags to make thumbnail processing
        private volatile byte[]? pendingThumbnailBytesFromService = null; // bytes handed to us by service events
        private volatile bool mediaNeedsUpdate = false; // set by service event when thumbnail changed
        private int thumbnailDecodeVersion = 0;
        private DateTime lastMediaCheck = DateTime.MinValue;
        private readonly TimeSpan mediaCheckInterval = TimeSpan.FromSeconds(2); // only decode/check media every 2s
        // Debounce short-lived 'no media' signals to avoid flicker when service emits transient nulls
        private DateTime mediaClearRequestedAt = DateTime.MinValue;
        private readonly TimeSpan mediaClearDelay = TimeSpan.FromSeconds(1);

        // Keys to detect duplicates - cached to avoid repeated formatting
        private string? currentMediaKey;
        private string? pendingMediaKey;
        private StringBuilder? keyBuilder; // Reusable StringBuilder for key generation

        // Scrolling title state
        private float titleScrollOffset = 0f; // Current scroll position
        private readonly float titleScrollSpeed = 30f; // Pixels per second
        private readonly float titleScrollDelay = 1f;  // Seconds to pause before scrolling
        private float titleScrollTimer = 0f;  // Timer for delay
        private bool isTitleScrolling = false;
        private string? fullTitleText = null;
        private float titleTextWidth = 0f;
        private const int titleScrollCharThreshold = 35;

        // Animation state handled by MediaAnimator
        private readonly MediaAnimator animator = new MediaAnimator();
        private SKImage? previousImage = null; // Image that is being replaced

        // Playback controls and progress
        private readonly MediaController controller;
        private DWImageButton? btnPrev;
        private DWImageButton? btnPlay;
        private DWImageButton? btnNext;

        private readonly AudioVisualiser visualiser;

        // Timeline state
        private TimeSpan? timelinePosition;
        private TimeSpan? timelineDuration;
        private bool isPlayingFlag = false;

        // Optimistic toggle to update UI immediately when user presses play/pause
        private bool optimisticState = false;
        private bool optimisticActive = false; // Remains active until a timeline sample updates

        // Animated progress fill
        private float displayFill = 0f;

        private readonly float timelineHeight = 6f; // Thickness of the bar
        private readonly SKColor timelineBgColor; // subtle background
        private Col timelineFgColor = Theme.TextMain; // active fill
        private readonly float timelineBarPadding = 12f; // vertical padding below buttons
        private readonly float timelineSidePadding = 40f; // space on left/right for timeline text
        private Col timelineTextColor = Theme.TextMain.Override(a: 55);
        private readonly float timelineTextSize = 10f;
        private DWProgressBarEx? timelineBar;

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

        // How often to re-fetch the timeline from MediaInfo
        private readonly TimeSpan timelineFetchInterval = TimeSpan.FromSeconds(4);

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

        // Track whether we are subscribed to the thumbnail service so we can unsubscribe when not enabled
        private bool isThumbnailSubscribed = false;

        // Animation for thumbnail scale/dim
        private float thumbnailAnim = 1f; // 1 = playing, 0 = paused
        private const float thumbnailAnimSpeed = 8f;

        // Metadata fetch throttle to populate textual metadata when thumbnail exists but metadata not set
        private DateTime lastMetadataFetch = DateTime.MinValue;
        private readonly TimeSpan metadataFetchInterval = TimeSpan.FromSeconds(1);

        // Cached layout values to avoid recalculation
        private SKRect cachedWidgetBounds = SKRect.Empty;
        private float cachedBarWidth;
        private float cachedBarX;
        private float cachedBarY;
        private SKRect cachedTimelineBarRect = SKRect.Empty;
        private DateTime lastLayoutCacheTime = DateTime.MinValue;

        // Paint cache for text rendering (reduces allocation pressure)
        private SKPaint? cachedTitlePaint;
        private SKPaint? cachedArtistPaint;
        private SKPaint? cachedTimelineTextPaint;

        public MediaPlayer(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            timelineBgColor = GetColor(Theme.WidgetBackground.Override(a: 200)).Value();
            controller = new MediaController();
            keyBuilder = new StringBuilder(128);

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
                        bool ok = await MediaInfo.TryTogglePlayPauseAsync().ConfigureAwait(false);

                        if (!ok)
                        {
                            // Fallback toggle if specific call isn't supported
                            controller.PlayPause();
                        }

                        // Immediately update local timeline/playback state optimistically so UI responds fast
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
                                    lastSampleElapsed = lastSampleElapsed.Value + (now - lastSampleReceivedAt);
                                }

                                lastSampleReceivedAt = now;
                                lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                                optimisticActive = false;
                                timelineFetchedOnce = true;
                            }
                        }
                    }
                    catch
                    {
                        controller.PlayPause();
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
            timelineBar = new DWProgressBarEx(this, new Vec2(0, 0), new Vec2(200, timelineHeight), UIAlignment.TopCenter,
                background: Theme.WidgetBackground.Override(a: 0.06f), foreground: timelineFgColor);
            if (timelineBar != null)
            {
                timelineBar.CornerRadius = timelineHeight / 2f;
                timelineBar.Smoothing = 30f;
                timelineBar.SetValueImmediate(0f);
                AddLocalObject(timelineBar);
            }

            // Subscribe to central thumbnail service event
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;
            isThumbnailSubscribed = true;

            // Try to initialise thumbnail from service cache so it doesn't disappear when re-opening
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
                // Re-subscribe to thumbnail service if needed
                if (!isThumbnailSubscribed)
                {
                    MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;
                    isThumbnailSubscribed = true;
                }

                StartFetchLoop();

                // If service has a cached bitmap bytes, ensure it's used (queue as pending to animate in)
                var bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                if (bytes != null && bytes.Length > 0)
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
                                thumbnailImage?.Dispose();
                                thumbnailImage = img;
                                thumbnailFingerprint = fp;
                            }
                        }
                    }
                }
                else
                {
                    // No cached service bitmap yet - do a one-shot fetch so first-open has a thumbnail.
                    _ = Task.Run(async () =>
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
                                        currentMediaKey = BuildMediaKey(meta, b.Length);
                                        optimisticActive = false;
                                    }
                                    else if (thumbnailImage == null && pendingImage == null)
                                    {
                                        pendingImage = img;
                                        pendingFingerprint = fp;
                                        pendingMedia = meta;
                                        pendingMediaKey = BuildMediaKey(meta, b.Length);
                                    }
                                    else
                                    {
                                        pendingImage?.Dispose();
                                        pendingImage = img;
                                        pendingFingerprint = fp;
                                        pendingMedia = meta;
                                        pendingMediaKey = BuildMediaKey(meta, b.Length);
                                    }
                                }
                            }
                        }
                        else if (meta != null)
                        {
                            lock (mediaLock)
                            {
                                currentMedia = meta;
                                currentMediaKey = BuildMediaKey(meta, 0);
                                optimisticActive = false;
                            }
                        }
                        else
                        {
                            lock (mediaLock)
                            {
                                thumbnailImage?.Dispose();
                                thumbnailImage = null;
                                thumbnailFingerprint = null;
                                pendingImage?.Dispose();
                                pendingImage = null;
                                pendingFingerprint = null;
                                previousImage?.Dispose();
                                previousImage = null;
                                currentMediaKey = null;
                                pendingMediaKey = null;
                                currentMedia = null;
                                optimisticActive = false;
                            }
                        }
                    });
                }
            }
            else
            {
                // When disabled, stop background work but keep subscribed to the thumbnail service
                StopFetchLoop(disposeCached: true);
                ResetThumbnailState();
            }
        }

        /// <summary>
        /// Helper to build a media key efficiently using StringBuilder to avoid repeated allocations.
        /// </summary>
        private string BuildMediaKey(DynamicWin.Utils.Media? media, int bytesLength)
        {
            if (media == null) return string.Empty;

            if (keyBuilder == null) keyBuilder = new StringBuilder(128);
            keyBuilder.Clear();

            keyBuilder.Append(media.Title ?? "");
            keyBuilder.Append('|');
            keyBuilder.Append(media.Artist ?? "");
            keyBuilder.Append('|');
            keyBuilder.Append(bytesLength);

            return keyBuilder.ToString();
        }

        private static string FormatTimeSpanForDisplay(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            }
            return string.Format("{0:D2}:{1:D2}", (int)ts.TotalMinutes, ts.Seconds);
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Early exit if not visible
            var home = Res.HomeMenu;
            if (home == null || home.currentBigMenuMode != HomeMenu.BigMenuMode.Media) return;

            var mainInstance = RendererMain.Instance;
            if (mainInstance?.MainIsland == null || !mainInstance.MainIsland.IsHovering) return;

            // Animate thumbnail scale/dim
            bool isPaused;
            lock (mediaLock) { isPaused = !GetEffectivePlayingState(); }
            float target = isPaused ? 0f : 1f;
            thumbnailAnim = Mathf.Lerp(thumbnailAnim, target, Math.Min(1f, thumbnailAnimSpeed * deltaTime));

            if (cts == null) StartFetchLoop();

            lock (mediaLock)
            {
                if (mediaClearRequestedAt != DateTime.MinValue && (DateTime.UtcNow - mediaClearRequestedAt) >= mediaClearDelay)
                {
                    animator.ForceFinish();
                    if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                    if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; }
                    if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
                    currentMedia = null;
                    currentMediaKey = null;
                    pendingMedia = null;
                    pendingMediaKey = null;
                    optimisticActive = false;
                    timelineFetchedOnce = false;
                    mediaClearRequestedAt = DateTime.MinValue;
                }
            }

            // Cache widget bounds once per frame
            var rectData = GetRect();
            cachedWidgetBounds = rectData.Rect;

            // Pre-calculate bar geometry used for both seeking and hovering
            float barWidth = cachedWidgetBounds.Width - 2 * timelineSidePadding;
            if (barWidth < 1f) barWidth = 1f;

            float barX = cachedWidgetBounds.Left + (cachedWidgetBounds.Width - barWidth) / 2f;

            // Determine button bottom safely
            float btnBottom = (btnPrev != null)
                ? (btnPrev.LocalPosition.Y + btnPrev.Size.Y)
                : (cachedWidgetBounds.Bottom - 20f);

            float barY = cachedWidgetBounds.Top + btnBottom + timelineBarPadding;
            if (barY + timelineHeight > cachedWidgetBounds.Bottom)
                barY = cachedWidgetBounds.Bottom - timelineHeight - timelineBarPadding;

            cachedBarWidth = barWidth;
            cachedBarX = barX;
            cachedBarY = barY;
            cachedTimelineBarRect = SKRect.Create(barX, barY, barWidth, timelineHeight);

            var mousePos = RendererMain.CursorPosition;

            // Animator step - reuse delegates if possible
            animator.Update(deltaTime,
                () => { lock (mediaLock) { return pendingImage != null; } },
                onStart: () => { lock (mediaLock) { previousImage = thumbnailImage; } },
                onMidFlip: OnAnimatorMidFlip,
                onFinish: OnAnimatorFinish);

            // Timeline snapshot logic
            TimeSpan? sampleElapsed = null;
            TimeSpan? sampleDuration = null;

            lock (mediaLock)
            {
                if (lastSampleElapsed.HasValue && lastSampleReceivedAt != DateTime.MinValue)
                {
                    sampleDuration = lastSampleDuration;

                    if (userIsSeeking)
                    {
                        sampleElapsed = userSeekElapsed;
                    }
                    else if (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        sampleElapsed = lastSampleElapsed.Value + (DateTime.UtcNow - lastSampleReceivedAt);
                    }
                    else
                    {
                        sampleElapsed = lastSampleElapsed.Value;
                    }

                    // Sync external timeline object if available
                    if (currentTimeline != null && sampleElapsed.HasValue)
                    {
                        currentTimeline.Position = currentTimeline.StartTime + sampleElapsed.Value;
                        if (sampleDuration.HasValue)
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
            if (sampleElapsed.HasValue)
            {
                timelinePosition = sampleElapsed.Value;

                if (sampleDuration.HasValue)
                {
                    timelineDuration = sampleDuration.Value;

                    if (timelinePosition < TimeSpan.Zero) timelinePosition = TimeSpan.Zero;
                    if (timelinePosition > timelineDuration) timelinePosition = timelineDuration;
                }
                else
                {
                    timelineDuration = null;
                }

                isPlayingFlag = (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
            else
            {
                timelinePosition = null;
                timelineDuration = null;
                isPlayingFlag = false;
            }

            // Visual interpolation
            double totalDurSeconds = (timelineDuration.HasValue) ? timelineDuration.Value.TotalSeconds : 0;
            float targetFill = 0f;
            bool haveTarget = false;

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
                float t = Math.Min(1f, 30f * deltaTime);
                displayFill = Mathf.Lerp(displayFill, targetFill, t);
            }

            // Updating layout
            float padding = 0f;
            float thumbSize = Math.Min(cachedWidgetBounds.Height - padding * 2f, cachedWidgetBounds.Height);
            SKRect thumbRect = SKRect.Create(cachedWidgetBounds.Left + padding, cachedWidgetBounds.Top + padding, thumbSize, thumbSize);

            float btnSize = 28f;
            float btnSpacing = 8f;
            float buttonsYOffset = 16f + 14f + 12f + 24f;
            float startXLocal = thumbRect.Right - cachedWidgetBounds.Left - 45f;

            if (btnPrev != null) btnPrev.LocalPosition = new Vec2(startXLocal, buttonsYOffset);
            if (btnPlay != null)
            {
                btnPlay.LocalPosition = new Vec2(startXLocal + (btnSize + btnSpacing), buttonsYOffset);

                bool effectivePlaying = GetEffectivePlayingState();
                var icon = effectivePlaying ? (Resources.Res.Pause ?? Resources.Res.Stop) : Resources.Res.Play;
                if (btnPlay.Image.Image != icon)
                {
                    btnPlay.Image.Image = icon;
                    btnPlay.Image.Color = Theme.IconColor;
                }
            }
            if (btnNext != null) btnNext.LocalPosition = new Vec2(startXLocal + 2 * (btnSize + btnSpacing), buttonsYOffset);

            // Input handling for seeking and hover states
            bool isMouseInBar = cachedTimelineBarRect.Contains(mousePos.X, mousePos.Y);
            isHoveringOverTimeline = isMouseInBar && IsHovering && (timelineBar == null || !timelineBar.IsLocked);

            // Mouse down (start seek)
            if (IsHovering && IsMouseDown && !mouseDownOverTimeline && isMouseInBar && (timelineBar == null || !timelineBar.IsLocked))
            {
                mouseDownOverTimeline = true;
                userIsSeeking = true;

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
                        var seekTarget = start.Value + userSeekElapsed;
                        _ = Task.Run(async () =>
                        {
                            var ok = await MediaInfo.SeekCurrentSessionAsync(seekTarget).ConfigureAwait(false);
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
                        });
                    }
                    userIsSeeking = false;
                }
            }

            string newTitle = !string.IsNullOrEmpty(currentMedia?.Title) ? currentMedia.Title : "No media playing";

            if (fullTitleText != newTitle)
            {
                fullTitleText = newTitle;
                var paint = GetPaint();
                paint.TextSize = 14f;
                paint.Typeface = Res.SFProBold;
                titleTextWidth = paint.MeasureText(fullTitleText);

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
            if (sampleElapsed.HasValue)
            {
                float desired = (float)(userIsSeeking ? userSeekElapsed.TotalSeconds : sampleElapsed.Value.TotalSeconds);

                if (userIsSeeking || !displayedElapsedInitialized)
                {
                    displayedElapsedSeconds = desired;
                    displayedElapsedInitialized = true;
                }
                else if (!isPlayingFlag)
                {
                    displayedElapsedSeconds = desired;
                }
                else
                {
                    displayedElapsedSeconds = Mathf.Lerp(displayedElapsedSeconds, desired, Math.Min(1f, 12f * deltaTime));
                }
            }

            float targetExtra = userIsSeeking ? 3f : (isHoveringOverTimeline ? 6f : 0f);
            timelineExtraHeight = Mathf.Lerp(timelineExtraHeight, targetExtra, Math.Min(1f, 12f * deltaTime));

            // Ensure metadata is populated when we have an image but no metadata
            bool needMeta = false;
            lock (mediaLock)
            {
                needMeta = (currentMedia == null) && (thumbnailImage != null || pendingImage != null);
            }

            if (needMeta && (DateTime.UtcNow - lastMetadataFetch) >= metadataFetchInterval)
            {
                lastMetadataFetch = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    var meta = await MediaInfo.FetchCurrentMediaAsync(forceRefresh: false).ConfigureAwait(false);
                    if (meta != null)
                    {
                        lock (mediaLock)
                        {
                            if (currentMedia == null)
                            {
                                currentMedia = meta;
                                currentMediaKey = BuildMediaKey(meta, 0);
                                optimisticActive = false;
                            }
                        }
                    }
                });
            }
        }

        private void OnAnimatorMidFlip()
        {
            lock (mediaLock)
            {
                if (pendingImage == null) return;

                thumbnailImage?.Dispose();
                thumbnailImage = pendingImage;
                thumbnailFingerprint = pendingFingerprint;
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
        }

        private void OnAnimatorFinish()
        {
            if (previousImage != null)
            {
                previousImage.Dispose();
                previousImage = null;
            }
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            try
            {
                int changeVersion = Interlocked.Increment(ref thumbnailDecodeVersion);
                var bytes = e.ThumbnailBytes;
                var media = e.Media;

                // Adopt metadata immediately
                lock (mediaLock)
                {
                    if (media != null)
                    {
                        currentMedia = media;
                        currentMediaKey = $"{media.Title ?? ""}|{media.Artist ?? ""}|{(bytes?.Length ?? 0)}";
                        optimisticActive = false;
                        timelineFetchedOnce = false;
                        lastTimelineResync = DateTime.MinValue;
                    }

                    // Clear any one-shot pending bytes; we'll handle decoding directly below
                    pendingThumbnailBytesFromService = null;
                    pendingMedia = media;
                    mediaNeedsUpdate = false;
                    mediaClearRequestedAt = DateTime.MinValue;
                }

                // Helper to queue a background decode and set pendingImage when appropriate
                void DecodeAndQueueBytes(byte[] bts, Media? md, int version)
                {
                    _ = Task.Run(() =>
                    {
                        if (version != Volatile.Read(ref thumbnailDecodeVersion)) return;

                        SKImage? img = null;
                        ulong? fp = null;
                        try
                        {
                            img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(bts, out fp);
                        }
                        catch { img = null; fp = null; }

                        if (img == null) return;

                        lock (mediaLock)
                        {
                            if (version != Volatile.Read(ref thumbnailDecodeVersion))
                            {
                                try { img.Dispose(); } catch { }
                                return;
                            }

                            // If visually identical to current, adopt metadata only
                            if (fp.HasValue && thumbnailFingerprint.HasValue && fp.Value == thumbnailFingerprint.Value)
                            {
                                if (md != null)
                                {
                                    currentMedia = md;
                                    currentMediaKey = $"{md.Title ?? ""}|{md.Artist ?? ""}|{bts.Length}";
                                }
                                optimisticActive = false;
                                try { img.Dispose(); } catch { }
                                return;
                            }

                            // Replace any existing pending image
                            if (pendingImage != null)
                            {
                                try { pendingImage.Dispose(); } catch { }
                                pendingImage = null;
                                pendingFingerprint = null;
                                pendingMediaKey = null;
                                pendingMedia = null;
                            }

                            pendingImage = img;
                            pendingFingerprint = fp;
                            pendingMedia = md;
                            pendingMediaKey = (md == null) ? string.Empty : $"{md.Title ?? ""}|{md.Artist ?? ""}|{bts.Length}";
                        }
                    });
                }

                // If event provided bytes, decode them immediately
                if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        var cloned = (byte[])bytes.Clone();
                        DecodeAndQueueBytes(cloned, media, changeVersion);
                    }
                    catch { }

                    return;
                }

                // No bytes in event: prefer service cached bytes (fast path)
                if (media != null)
                {
                    try
                    {
                        var svcBytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                        if (svcBytes != null && svcBytes.Length > 0)
                        {
                            var cloned = (byte[])svcBytes.Clone();
                            DecodeAndQueueBytes(cloned, media, changeVersion);
                            return;
                        }
                    }
                    catch { }

                    // If no cached bytes, trigger a one-shot fetch but do not block UI thread
                    var now = DateTime.UtcNow;
                    if ((now - lastMediaCheck) > TimeSpan.FromMilliseconds(500))
                    {
                        lastMediaCheck = now;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var b = await MediaInfo.FetchCurrentThumbnailBytesAsync(forceRefresh: true).ConfigureAwait(false);
                                if (changeVersion != Volatile.Read(ref thumbnailDecodeVersion)) return;
                                if (b != null && b.Length > 0)
                                {
                                    lock (mediaLock)
                                    {
                                        // stash as consumed so other loops won't duplicate work
                                        pendingThumbnailBytesFromService = null;
                                        pendingMedia = media;
                                        mediaNeedsUpdate = false;
                                    }

                                    DecodeAndQueueBytes((byte[])b.Clone(), media, changeVersion);
                                }
                            }
                            catch { }
                        });
                    }
                }
                else
                {
                    // No media and no bytes: clear thumbnail after a short debounce to avoid flicker
                    lock (mediaLock)
                    {
                        mediaClearRequestedAt = DateTime.UtcNow;
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
                        // Timeline fetch: perform an initial fetch when we do not yet have a timeline sample
                        // and also periodically re-sync the timeline so external changes (pause/seek) are detected
                        try
                        {
                            // Timeline re-sync: perform an initial fetch if missing, then refresh at configured interval
                            if (!userIsSeeking && (!timelineFetchedOnce || (DateTime.UtcNow - lastTimelineResync) >= timelineFetchInterval))
                            {
                                // Force refresh to bypass small MediaInfo timeline cache so external changes are detected
                                var tl = await MediaInfo.FetchCurrentTimelineAsync(forceRefresh: true).ConfigureAwait(false);
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
                                    int processVersion = Volatile.Read(ref thumbnailDecodeVersion);
                                    SKImage? img = null;
                                    ulong? fp = null;
                                    try
                                    {
                                        img = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(svcBytes, out fp);
                                    }
                                    catch { img = null; fp = null; }

                                    bool skipPending = false;
                                    bool staleDecode = false;
                                    lock (mediaLock)
                                    {
                                        if (processVersion != Volatile.Read(ref thumbnailDecodeVersion))
                                        {
                                            if (img != null) { try { img.Dispose(); } catch { } }
                                            staleDecode = true;
                                        }
                                        else if (img != null && fp.HasValue && thumbnailFingerprint.HasValue && fp.Value == thumbnailFingerprint.Value)
                                        {
                                            // Visually identical - adopt metadata only
                                            if (svcMedia != null)
                                            {
                                                currentMedia = svcMedia;
                                                currentMediaKey = key;
                                            }
                                            optimisticActive = false;
                                            skipPending = true;
                                        }
                                    }
                                    if (staleDecode)
                                    {
                                        continue;
                                    }
                                    if (skipPending)
                                    {
                                        if (img != null) { try { img.Dispose(); } catch { } }
                                        continue;
                                    }

                                    lock (mediaLock)
                                    {
                                        if (img != null)
                                        {
                                            // Queue as pending (replace any existing pending)
                                            if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; pendingMediaKey = null; pendingMedia = null; }

                                            pendingImage = img;
                                            pendingFingerprint = fp;
                                            pendingMedia = svcMedia;
                                            pendingMediaKey = key;

                                            // Do not set thumbnailImage here; animator will swap on flip
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

        /// <summary>
        /// Stop the background fetch loop. If disposeCached is true, also free cached images and metadata.
        /// </summary>
        private void StopFetchLoop(bool disposeCached = false)
        {
            if (cts == null)
            {
                // Still optionally free resources
                if (disposeCached)
                {
                    lock (mediaLock)
                    {
                        // If animator is mid-animation, keep images so flip can complete when UI resumes.
                        if (animator.State == MediaAnimator.AnimState.Idle)
                        {
                            if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; }
                            if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                            if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
                        }

                        pendingMediaKey = null; pendingMedia = null;
                        currentMedia = null; currentMediaKey = null;
                    }
                }
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch { }
            try
            {
                cts.Dispose();
            }
            catch { }
            cts = null;

            lock (mediaLock)
            {
                // Only dispose pendingImage if animator is idle; otherwise keep it so the pending swap can complete when UI resumes
                if (animator.State == MediaAnimator.AnimState.Idle)
                {
                    if (pendingImage != null)
                    {
                        try { pendingImage.Dispose(); } catch { }
                        pendingImage = null;
                        pendingFingerprint = null;
                    }
                }

                pendingMediaKey = null;
                pendingMedia = null;

                if (disposeCached)
                {
                    if (animator.State == MediaAnimator.AnimState.Idle)
                    {
                        if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                        if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
                    }
                    currentMedia = null;
                    currentMediaKey = null;
                }
            }
        }

        // Reset thumbnail/animation state for menu close/deactivation
        private void ResetThumbnailState()
        {
            Interlocked.Increment(ref thumbnailDecodeVersion);

            lock (mediaLock)
            {
                // Reset animator to idle
                animatorReset();
                // Dispose and clear all images and fingerprints
                if (thumbnailImage != null) { try { thumbnailImage.Dispose(); } catch { } thumbnailImage = null; thumbnailFingerprint = null; }
                if (pendingImage != null) { try { pendingImage.Dispose(); } catch { } pendingImage = null; pendingFingerprint = null; }
                if (previousImage != null) { try { previousImage.Dispose(); } catch { } previousImage = null; }
                currentMedia = null;
                currentMediaKey = null;
                pendingMedia = null;
                pendingMediaKey = null;
                optimisticActive = false;
                timelineFetchedOnce = false;
                lastTimelineResync = DateTime.MinValue;
            }
        }

        // Helper to reset animator state
        private void animatorReset()
        {
            animator.ForceFinish();
        }

        public override void Draw(SKCanvas canvas)
        {
            // Early exit for visibility - eliminate try-catch overhead
            var home = Res.HomeMenu;
            if (home == null || home.currentBigMenuMode != HomeMenu.BigMenuMode.Media) return;

            var mainInstance = RendererMain.Instance;
            if (mainInstance?.MainIsland == null || !mainInstance.MainIsland.IsHovering) return;

            if (!IsEnabled || (Parent != null && !Parent.IsEnabled)) return;

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

                // Shrink and dim thumbnail if paused, animated
                float thumbScale = 0.8f + 0.2f * thumbnailAnim; // 0.8 (paused) to 1.0 (playing)
                float dimAlpha = (1f - thumbnailAnim) * 120f; // 0 (playing) to 120 (paused)
                float centerX = thumbRect.MidX;
                float centerY = thumbRect.MidY;

                if (doFlip)
                {
                    float cx = thumbRect.MidX;
                    float cy = thumbRect.MidY;
                    canvas.Translate(cx, cy);
                    canvas.Scale(flipScale * thumbScale, thumbScale);
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
                        if (dimAlpha > 0.5f)
                        {
                            using var dimPaint = GetPaint();
                            dimPaint.Color = new SKColor(0, 0, 0, (byte)dimAlpha);
                            canvas.DrawRect(localRect, dimPaint);
                        }
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
                }
                else
                {
                    canvas.Save();
                    canvas.Translate(centerX, centerY);
                    canvas.Scale(thumbScale, thumbScale);
                    canvas.Translate(-centerX, -centerY);
                    canvas.ClipPath(squirclePath, antialias: Settings.AntiAliasing);

                    var paint = GetPaint();
                    paint.IsAntialias = Settings.AntiAliasing;
                    paint.IsStroke = false;
                    paint.ImageFilter = animator.BlurAmount > 0f ? SKImageFilter.CreateBlur(animator.BlurAmount, animator.BlurAmount) : null;
                    paint.BlendMode = SKBlendMode.SrcOver;
                    if (displayImg != null)
                    {
                        try { canvas.DrawImage(displayImg, thumbRect, paint); } catch { }
                        if (dimAlpha > 0.5f)
                        {
                            using var dimPaint = GetPaint();
                            dimPaint.Color = new SKColor(0, 0, 0, (byte)dimAlpha);
                            canvas.DrawRect(thumbRect, dimPaint);
                        }
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
                }
            }
            catch { }

            // Draw texts and timeline (reuse existing code from earlier)
            float textX = thumbRect.Right + 14f;
            float textY = rect.Top + 16f;

            var titlePaint = GetPaint();
            titlePaint.IsStroke = false;
            titlePaint.TextSize = 14f;
            titlePaint.Typeface = Resources.Res.SFProBold;
            titlePaint.Color = GetColor(Theme.TextMain).Value();

            var artistPaint = GetPaint();
            artistPaint.IsStroke = false;
            artistPaint.TextSize = 12f;
            artistPaint.Typeface = Resources.Res.SFProRegular;
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
                    paint.Typeface = Res.SFProRegular;

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
            isThumbnailSubscribed = false;

            StopFetchLoop(disposeCached: true);
            // Reset thumbnail/animation state 
            ResetThumbnailState();
        }

    }
}
