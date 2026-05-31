using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.Utils;
using SkiaSharp;
using System.Threading;
using System.Threading.Tasks;
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
 *   Last Modified:          31 May 2026
 *
 */

namespace DynamicWin.UI.UIElements.Custom
{
    public class MediaPlayer : UIObject
    {
        private const float TitleTextSize = 14f;
        private const float ArtistTextSize = 12f;
        private const float TimelineTextSize = 10f;
        private const float TimelineHeight = 6f;
        private const float TimelineSidePadding = 40f;
        private const float TimelineBarPadding = 12f;
        private const float TitleScrollSpeed = 30f;
        private const float TitleScrollDelay = 1f;
        private const float ThumbnailAnimSpeed = 8f;
        private const int TitleTruncateChars = 35;
        private const int ArtistTruncateChars = 45;

        private readonly object mediaLock = new object();
        private readonly MediaController controller;
        private readonly MediaAnimator animator = new MediaAnimator();

        private readonly DWImageButton btnPrev;
        private readonly DWImageButton btnPlay;
        private readonly DWImageButton btnNext;
        private readonly AudioVisualiser visualiser;

        private readonly SKPaint thumbnailPaint;
        private readonly SKPaint placeholderPaint;
        private readonly SKPaint dimPaint;
        private readonly SKPaint titlePaint;
        private readonly SKPaint artistPaint;
        private readonly SKPaint timelineTextPaint;
        private readonly SKPaint timelineTrackPaint;
        private readonly SKPaint timelineFillPaint;

        private readonly SKFont titleFont;
        private readonly SKFont artistFont;
        private readonly SKFont timelineFont;

        private SKImage? thumbnailImage;
        private ulong? thumbnailFingerprint;
        private SKImage? pendingImage;
        private ulong? pendingFingerprint;
        private SKImage? previousImage;
        private DynamicWin.Utils.Media? currentMedia;
        private DynamicWin.Utils.Media? pendingMedia;
        private string currentMediaKey = string.Empty;
        private string pendingMediaKey = string.Empty;
        private int thumbnailDecodeVersion;
        private DateTime mediaClearRequestedAt = DateTime.MinValue;
        private readonly TimeSpan mediaClearDelay = TimeSpan.FromSeconds(1);
        private DateTime lastMissingThumbnailFetch = DateTime.MinValue;
        private readonly TimeSpan missingThumbnailFetchInterval = TimeSpan.FromSeconds(1);
        private readonly TimeSpan optimisticStatusGrace = TimeSpan.FromMilliseconds(900);

        private CancellationTokenSource? timelineCts;
        private int timelineFetchRunning;
        private readonly TimeSpan timelineFetchInterval = TimeSpan.FromSeconds(2);

        private TimeSpan? lastSampleElapsed;
        private TimeSpan? lastSampleDuration;
        private DateTime lastSampleReceivedAt = DateTime.MinValue;
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        private MediaTimeline? currentTimeline;

        private TimeSpan? timelinePosition;
        private TimeSpan? timelineDuration;
        private bool isPlayingFlag;
        private bool optimisticState;
        private bool optimisticActive;
        private DateTime optimisticStartedAt = DateTime.MinValue;
        private bool userIsSeeking;
        private TimeSpan userSeekElapsed = TimeSpan.Zero;
        private bool mouseDownOverTimeline;
        private bool isHoveringOverTimeline;
        private float displayFill;
        private float displayedElapsedSeconds;
        private bool displayedElapsedInitialized;
        private float timelineExtraHeight;
        private float thumbnailAnim = 1f;

        private bool isThumbnailSubscribed;
        private bool childrenAreActive;
        private bool visualiserCaptureEnabled;

        private SKRect layoutRect = SKRect.Empty;
        private SKRect thumbnailRect = SKRect.Empty;
        private SKRect localThumbnailRect = SKRect.Empty;
        private SKRect titleClipRect = SKRect.Empty;
        private SKRect timelineBarRect = SKRect.Empty;
        private SKRect timelineBaseRect = SKRect.Empty;
        private SKPath? thumbnailPath;
        private SKPath? localThumbnailPath;
        private float textX;
        private float titleBaseline;
        private float artistBaseline;
        private float timelineTextBaseline;
        private float timelineLeftX;
        private float timelineRightX;
        private float timelineBarBaseY;
        private float layoutTimelineExtraHeight = -1f;
        private float layoutTimelineRightTextWidth = -1f;
        private bool layoutDirty = true;

        private string fullTitleText = "No media playing";
        private string truncatedTitleText = "No media playing";
        private string artistText = "No media playing";
        private float titleTextWidth;
        private float titleScrollOffset;
        private float titleScrollTimer;
        private bool isTitleScrolling;
        private SKTextBlob? fullTitleBlob;
        private SKTextBlob? truncatedTitleBlob;
        private SKTextBlob? artistBlob;

        private string cachedTimelineLeftText = "--:--";
        private string cachedTimelineRightText = "--:--";
        private float cachedTimelineRightTextWidth;
        private int cachedTimelineElapsedSecond = int.MinValue;
        private int cachedTimelineDurationSecond = int.MinValue;
        private SKTextBlob? timelineLeftBlob;
        private SKTextBlob? timelineRightBlob;

        public MediaPlayer(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
        {
            controller = new MediaController();

            titleFont = new SKFont(Res.SFProBold, TitleTextSize);
            artistFont = new SKFont(Res.SFProRegular, ArtistTextSize);
            timelineFont = new SKFont(Res.SFProRegular, TimelineTextSize);

            thumbnailPaint = CreateFillPaint();
            placeholderPaint = CreateFillPaint();
            dimPaint = CreateFillPaint();
            titlePaint = CreateTextPaint(Res.SFProBold, TitleTextSize);
            artistPaint = CreateTextPaint(Res.SFProRegular, ArtistTextSize);
            timelineTextPaint = CreateTextPaint(Res.SFProRegular, TimelineTextSize);
            timelineTrackPaint = CreateFillPaint();
            timelineFillPaint = CreateFillPaint();

            btnPrev = new DWImageButton(this, Res.Previous, new Vec2(0, 0), new Vec2(28, 28), () => controller.Previous(), UIAlignment.TopLeft)
            {
                roundRadius = 14f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.7f
            };
            AddLocalObject(btnPrev);

            btnPlay = new DWImageButton(this, Res.Play, new Vec2(0, 0), new Vec2(32, 32), HandlePlayPauseClick, UIAlignment.TopLeft)
            {
                roundRadius = 16f,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.06f),
                clickColor = Col.White.Override(a: 0.12f),
                imageScale = 0.78f
            };
            AddLocalObject(btnPlay);

            btnNext = new DWImageButton(this, Res.Next, new Vec2(0, 0), new Vec2(28, 28), () => controller.Next(), UIAlignment.TopLeft)
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
                ThumbnailBlurAmount = 5f,
            };
            AddLocalObject(visualiser);
            visualiser.SetCapturing(false);
            visualiser.SetThumbnailSubscription(false);
            visualiser.SilentSetActive(false);

            ReplaceTextBlob(ref fullTitleBlob, fullTitleText, titleFont);
            ReplaceTextBlob(ref truncatedTitleBlob, truncatedTitleText, titleFont);
            ReplaceTextBlob(ref artistBlob, artistText, artistFont);
            ReplaceTextBlob(ref timelineLeftBlob, cachedTimelineLeftText, timelineFont);
            ReplaceTextBlob(ref timelineRightBlob, cachedTimelineRightText, timelineFont);

            SetChildInteraction(false);
        }

        protected override void OnActiveChanged(bool isEnabled)
        {
            base.OnActiveChanged(isEnabled);

            if (isEnabled)
            {
                visualiser.SetThumbnailSubscription(true);
                SubscribeToThumbnailService();
                StartTimelineLoop();
                RequestTimelineRefresh();
            }
            else
            {
                SetChildInteraction(false);
                SetVisualiserCapture(false);
                visualiser.SetThumbnailSubscription(false);
                StopTimelineLoop();
                UnsubscribeFromThumbnailService();
                ResetMediaState(clearText: true);
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            bool visible = ShouldRenderMediaPlayer();
            SetChildInteraction(visible);
            if (!visible) return;

            StartTimelineLoop();
            ClearMediaAfterDebounce();

            EnsureLayout();
            StepThumbnailAnimation(deltaTime);
            StepThumbnailSwapAnimation(deltaTime);
            UpdateTimelineSnapshot();
            UpdateDisplayFill(deltaTime);
            UpdateButtonLayoutAndIcon();
            HandleTimelineInput();
            UpdateTextCache(deltaTime);
            UpdateTimelineTextCache();
            UpdateVisualiserState();
        }

        public override void Draw(SKCanvas canvas)
        {
            if (!ShouldRenderMediaPlayer()) return;

            EnsureLayout();

            SKImage? image;
            bool hasMedia;
            lock (mediaLock)
            {
                image = thumbnailImage;
                hasMedia = currentMedia != null || thumbnailImage != null || pendingImage != null;
            }

            DrawThumbnail(canvas, image);
            DrawText(canvas);
            DrawTimeline(canvas, hasMedia);
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            UnsubscribeFromThumbnailService();
            StopTimelineLoop();
            visualiser.SetThumbnailSubscription(false);
            ResetMediaState(clearText: true);

            thumbnailPaint.Dispose();
            placeholderPaint.Dispose();
            dimPaint.Dispose();
            titlePaint.Dispose();
            artistPaint.Dispose();
            timelineTextPaint.Dispose();
            timelineTrackPaint.Dispose();
            timelineFillPaint.Dispose();
            titleFont.Dispose();
            artistFont.Dispose();
            timelineFont.Dispose();
            thumbnailPath?.Dispose();
            localThumbnailPath?.Dispose();
            fullTitleBlob?.Dispose();
            truncatedTitleBlob?.Dispose();
            artistBlob?.Dispose();
            timelineLeftBlob?.Dispose();
            timelineRightBlob?.Dispose();
        }

        private SKPaint CreateFillPaint()
        {
            var paint = GetPaint();
            paint.IsStroke = false;
            paint.IsAntialias = Settings.AntiAliasing;
            paint.BlendMode = SKBlendMode.SrcOver;
            return paint;
        }

        private SKPaint CreateTextPaint(SKTypeface typeface, float size)
        {
            var paint = GetPaint();
            paint.IsStroke = false;
            paint.IsAntialias = Settings.AntiAliasing;
            paint.TextSize = size;
            paint.Typeface = typeface;
            return paint;
        }

        private bool ShouldRenderMediaPlayer()
        {
            if (!IsEnabled || (Parent != null && !Parent.IsEnabled)) return false;

            var home = Res.HomeMenu;
            var main = RendererMain.Instance;
            return home != null &&
                   main?.MainIsland != null &&
                   home.currentBigMenuMode == HomeMenu.BigMenuMode.Media &&
                   main.MainIsland.IsHovering;
        }

        private void SetChildInteraction(bool enabled)
        {
            if (childrenAreActive == enabled) return;

            childrenAreActive = enabled;
            drawLocalObjects = enabled;
            btnPrev.SilentSetActive(enabled);
            btnPlay.SilentSetActive(enabled);
            btnNext.SilentSetActive(enabled);

            if (!enabled)
            {
                visualiser.SilentSetActive(false);
                SetVisualiserCapture(false);
            }
        }

        private void SetVisualiserCapture(bool enabled)
        {
            if (visualiserCaptureEnabled == enabled) return;

            visualiserCaptureEnabled = enabled;
            visualiser.SetCapturing(enabled, resetBarsOnStop: false);
        }

        private void SubscribeToThumbnailService()
        {
            if (isThumbnailSubscribed) return;

            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChanged;
            isThumbnailSubscribed = true;
        }

        private void UnsubscribeFromThumbnailService()
        {
            if (!isThumbnailSubscribed) return;

            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChanged; } catch { }
            isThumbnailSubscribed = false;
        }

        private void StartTimelineLoop()
        {
            if (timelineCts != null) return;

            timelineCts = new CancellationTokenSource();
            var token = timelineCts.Token;

            _ = Task.Run(async () =>
            {
                await FetchTimelineSampleAsync(token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(timelineFetchInterval, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    await FetchTimelineSampleAsync(token).ConfigureAwait(false);
                }
            }, token);
        }

        private void StopTimelineLoop()
        {
            var source = timelineCts;
            timelineCts = null;

            if (source == null) return;

            try { source.Cancel(); } catch { }
            try { source.Dispose(); } catch { }
        }

        private void RequestTimelineRefresh()
        {
            var token = timelineCts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested) return;

            _ = FetchTimelineSampleAsync(token);
        }

        private async Task FetchTimelineSampleAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested || userIsSeeking) return;
            if (Interlocked.CompareExchange(ref timelineFetchRunning, 1, 0) != 0) return;

            try
            {
                var timeline = await MediaInfo.FetchCurrentTimelineAsync(forceRefresh: true).ConfigureAwait(false);
                if (token.IsCancellationRequested || userIsSeeking) return;

                lock (mediaLock)
                {
                    if (timeline == null)
                    {
                        lastSampleElapsed = null;
                        lastSampleDuration = null;
                        lastSampleReceivedAt = DateTime.MinValue;
                        lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                        currentTimeline = null;
                        optimisticActive = false;
                        return;
                    }

                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

                    var elapsed = timeline.Position - timeline.StartTime;
                    if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                    if (duration > TimeSpan.Zero && elapsed > duration) elapsed = duration;

                    lastSampleElapsed = elapsed;
                    lastSampleDuration = duration;
                    lastSampleReceivedAt = DateTime.UtcNow;
                    lastPlaybackStatus = timeline.PlaybackStatus;
                    currentTimeline = timeline;
                    optimisticActive = false;
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref timelineFetchRunning, 0);
            }
        }

        private void HandlePlayPauseClick()
        {
            bool willPlay = !GetEffectivePlayingState();
            optimisticState = willPlay;
            optimisticActive = true;
            optimisticStartedAt = DateTime.UtcNow;
            isPlayingFlag = willPlay;
            UpdatePlayIcon(willPlay);

            lock (mediaLock)
            {
                var now = optimisticStartedAt;
                if (!lastSampleElapsed.HasValue)
                    lastSampleElapsed = TimeSpan.Zero;

                if (!willPlay &&
                    lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    lastSampleReceivedAt != DateTime.MinValue)
                {
                    lastSampleElapsed += now - lastSampleReceivedAt;
                }

                lastSampleReceivedAt = now;
                lastPlaybackStatus = willPlay
                    ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await MediaInfo.TryTogglePlayPauseAsync().ConfigureAwait(false))
                        controller.PlayPause();
                }
                catch
                {
                    controller.PlayPause();
                }
                finally
                {
                    RequestTimelineRefresh();
                }
            });
        }

        private bool GetEffectivePlayingState()
        {
            return optimisticActive ? optimisticState : isPlayingFlag;
        }

        private void OnThumbnailChanged(object? sender, MediaChangedEventArgs e)
        {
            int version = Interlocked.Increment(ref thumbnailDecodeVersion);
            var media = e.Media;
            var bytes = e.ThumbnailBytes;
            ApplyServicePlaybackStatus(MediaThumbnailService.Instance.LastPlaybackStatus);

            if (media != null)
            {
                bool requestTimeline;
                lock (mediaLock)
                {
                    requestTimeline = SetCurrentMediaLocked(media);
                    mediaClearRequestedAt = DateTime.MinValue;
                }

                if (requestTimeline)
                    RequestTimelineRefresh();
            }

            if (bytes != null && bytes.Length > 0)
            {
                QueueThumbnailDecode((byte[])bytes.Clone(), media, version);
                return;
            }

            if (media != null)
            {
                RequestMissingThumbnail(media, version);
                return;
            }

            lock (mediaLock)
            {
                mediaClearRequestedAt = DateTime.UtcNow;
            }
        }

        private bool HasMedia()
        {
            lock (mediaLock)
            {
                return currentMedia != null || thumbnailImage != null || pendingImage != null;
            }
        }

        private void ApplyServicePlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
        {
            if (!status.HasValue) return;

            var value = status.Value;
            bool playing = value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var now = DateTime.UtcNow;

            if (optimisticActive &&
                playing != optimisticState &&
                (now - optimisticStartedAt) < optimisticStatusGrace)
            {
                return;
            }

            lock (mediaLock)
            {
                if (!lastSampleElapsed.HasValue)
                    lastSampleElapsed = timelinePosition ?? TimeSpan.Zero;

                if (lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    value != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    lastSampleReceivedAt != DateTime.MinValue)
                {
                    lastSampleElapsed += now - lastSampleReceivedAt;
                    if (lastSampleDuration.HasValue && lastSampleElapsed > lastSampleDuration)
                        lastSampleElapsed = lastSampleDuration;
                }

                lastPlaybackStatus = value;
                lastSampleReceivedAt = now;
                optimisticActive = false;
            }

            isPlayingFlag = playing;
        }

        private void RequestMissingThumbnail(DynamicWin.Utils.Media media, int version)
        {
            var now = DateTime.UtcNow;
            if ((now - lastMissingThumbnailFetch) < missingThumbnailFetchInterval) return;

            lastMissingThumbnailFetch = now;
            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync(forceRefresh: false).ConfigureAwait(false);
                    if (version != Volatile.Read(ref thumbnailDecodeVersion)) return;
                    if (bytes != null && bytes.Length > 0)
                        QueueThumbnailDecode((byte[])bytes.Clone(), media, version);
                }
                catch
                {
                }
            });
        }

        private void QueueThumbnailDecode(byte[] bytes, DynamicWin.Utils.Media? media, int version)
        {
            string mediaKey = BuildMediaKey(media);

            _ = Task.Run(() =>
            {
                if (version != Volatile.Read(ref thumbnailDecodeVersion)) return;

                SKImage? decoded = null;
                ulong? fingerprint = null;

                try
                {
                    decoded = MediaThumbnailUtils.DecodeBytesToImageAndFingerprint(bytes, out fingerprint);
                }
                catch
                {
                    decoded = null;
                }

                if (decoded == null) return;

                bool requestTimeline = false;

                lock (mediaLock)
                {
                    if (version != Volatile.Read(ref thumbnailDecodeVersion))
                    {
                        decoded.Dispose();
                        return;
                    }

                    if (fingerprint.HasValue &&
                        thumbnailFingerprint.HasValue &&
                        fingerprint.Value == thumbnailFingerprint.Value)
                    {
                        decoded.Dispose();
                        requestTimeline = SetCurrentMediaLocked(media);
                    }
                    else if (thumbnailImage == null && animator.State == MediaAnimator.AnimState.Idle)
                    {
                        thumbnailImage = decoded;
                        thumbnailFingerprint = fingerprint;
                        requestTimeline = SetCurrentMediaLocked(media);
                    }
                    else
                    {
                        DisposeImage(ref pendingImage);
                        pendingImage = decoded;
                        pendingFingerprint = fingerprint;
                        pendingMedia = CopyMedia(media);
                        pendingMediaKey = mediaKey;
                    }
                }

                if (requestTimeline)
                    RequestTimelineRefresh();
            });
        }

        private static DynamicWin.Utils.Media? CopyMedia(DynamicWin.Utils.Media? media)
        {
            if (media == null) return null;

            return new DynamicWin.Utils.Media
            {
                Title = media.Title,
                Artist = media.Artist,
                ThumbnailData = null
            };
        }

        private static string BuildMediaKey(DynamicWin.Utils.Media? media)
        {
            if (media == null) return string.Empty;
            return $"{media.Title ?? string.Empty}|{media.Artist ?? string.Empty}";
        }

        private bool SetCurrentMediaLocked(DynamicWin.Utils.Media? media)
        {
            if (media == null) return false;

            string key = BuildMediaKey(media);
            bool changed = key != currentMediaKey;
            currentMedia = CopyMedia(media);
            currentMediaKey = key;
            optimisticActive = false;

            if (changed)
                ResetTimelineLocked();

            return changed;
        }

        private void ResetTimelineLocked()
        {
            lastSampleElapsed = null;
            lastSampleDuration = null;
            lastSampleReceivedAt = DateTime.MinValue;
            lastPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            currentTimeline = null;
        }

        private void ClearMediaAfterDebounce()
        {
            bool shouldClear;
            lock (mediaLock)
            {
                shouldClear = mediaClearRequestedAt != DateTime.MinValue &&
                              (DateTime.UtcNow - mediaClearRequestedAt) >= mediaClearDelay;
            }

            if (shouldClear)
                ResetMediaState(clearText: false);
        }

        private void ResetMediaState(bool clearText)
        {
            Interlocked.Increment(ref thumbnailDecodeVersion);

            lock (mediaLock)
            {
                animator.ForceFinish();
                DisposeAllImagesLocked();
                currentMedia = null;
                pendingMedia = null;
                currentMediaKey = string.Empty;
                pendingMediaKey = string.Empty;
                pendingFingerprint = null;
                thumbnailFingerprint = null;
                optimisticActive = false;
                userIsSeeking = false;
                mouseDownOverTimeline = false;
                mediaClearRequestedAt = DateTime.MinValue;
                ResetTimelineLocked();
            }

            displayFill = 0f;
            displayedElapsedSeconds = 0f;
            displayedElapsedInitialized = false;
            cachedTimelineElapsedSecond = int.MinValue;
            cachedTimelineDurationSecond = int.MinValue;
            thumbnailAnim = 1f;
            titleScrollOffset = 0f;
            titleScrollTimer = 0f;
            isTitleScrolling = false;

            if (clearText)
            {
                SetTextBlobs("No media playing", "No media playing");
                SetTimelineText("--:--", "--:--");
            }
        }

        private void DisposeAllImagesLocked()
        {
            var thumb = thumbnailImage;
            var pending = pendingImage;
            var previous = previousImage;

            thumbnailImage = null;
            pendingImage = null;
            previousImage = null;

            try { thumb?.Dispose(); } catch { }
            if (pending != null && !ReferenceEquals(pending, thumb))
            {
                try { pending.Dispose(); } catch { }
            }
            if (previous != null && !ReferenceEquals(previous, thumb) && !ReferenceEquals(previous, pending))
            {
                try { previous.Dispose(); } catch { }
            }
        }

        private static void DisposeImage(ref SKImage? image)
        {
            var old = image;
            image = null;
            try { old?.Dispose(); } catch { }
        }

        private void StepThumbnailAnimation(float deltaTime)
        {
            float target = GetEffectivePlayingState() ? 1f : 0f;
            thumbnailAnim = Mathf.Lerp(thumbnailAnim, target, Math.Min(1f, ThumbnailAnimSpeed * deltaTime));
        }

        private void StepThumbnailSwapAnimation(float deltaTime)
        {
            animator.Update(deltaTime,
                () =>
                {
                    lock (mediaLock) { return pendingImage != null; }
                },
                onStart: () =>
                {
                    lock (mediaLock)
                    {
                        previousImage = thumbnailImage;
                    }
                },
                onMidFlip: OnAnimatorMidFlip,
                onFinish: OnAnimatorFinish);
        }

        private void OnAnimatorMidFlip()
        {
            bool requestTimeline = false;

            lock (mediaLock)
            {
                if (pendingImage == null) return;

                thumbnailImage = pendingImage;
                thumbnailFingerprint = pendingFingerprint;
                pendingImage = null;
                pendingFingerprint = null;

                if (pendingMedia != null)
                    requestTimeline = SetCurrentMediaLocked(pendingMedia);

                pendingMedia = null;
                if (!string.IsNullOrEmpty(pendingMediaKey))
                    currentMediaKey = pendingMediaKey;
                pendingMediaKey = string.Empty;
            }

            if (requestTimeline)
                RequestTimelineRefresh();
        }

        private void OnAnimatorFinish()
        {
            if (previousImage != null && !ReferenceEquals(previousImage, thumbnailImage))
                previousImage.Dispose();

            previousImage = null;
        }

        private void UpdateTimelineSnapshot()
        {
            TimeSpan? sampleElapsed = null;
            TimeSpan? sampleDuration = null;
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus;

            lock (mediaLock)
            {
                playbackStatus = lastPlaybackStatus;

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
                }
            }

            if (sampleElapsed.HasValue)
            {
                timelinePosition = sampleElapsed.Value;
                timelineDuration = sampleDuration;

                if (timelineDuration.HasValue && timelineDuration.Value > TimeSpan.Zero)
                {
                    if (timelinePosition < TimeSpan.Zero) timelinePosition = TimeSpan.Zero;
                    if (timelinePosition > timelineDuration) timelinePosition = timelineDuration;
                }

                isPlayingFlag = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            else
            {
                timelinePosition = null;
                timelineDuration = null;
                isPlayingFlag = false;
                displayedElapsedInitialized = false;
            }
        }

        private void UpdateDisplayFill(float deltaTime)
        {
            double durationSeconds = timelineDuration?.TotalSeconds ?? 0;
            float targetFill = 0f;
            bool hasTarget = false;

            if (durationSeconds > 0)
            {
                if (userIsSeeking)
                {
                    targetFill = Math.Clamp((float)(userSeekElapsed.TotalSeconds / durationSeconds), 0f, 1f);
                    hasTarget = true;
                }
                else if (timelinePosition.HasValue)
                {
                    targetFill = Math.Clamp((float)(timelinePosition.Value.TotalSeconds / durationSeconds), 0f, 1f);
                    hasTarget = true;
                }
            }

            if (!hasTarget)
            {
                displayFill = Mathf.Lerp(displayFill, 0f, Math.Min(1f, 18f * deltaTime));
            }
            else
            {
                displayFill = Mathf.Lerp(displayFill, targetFill, Math.Min(1f, 30f * deltaTime));
            }

            if (timelinePosition.HasValue)
            {
                float desired = (float)(userIsSeeking ? userSeekElapsed.TotalSeconds : timelinePosition.Value.TotalSeconds);

                if (userIsSeeking || !displayedElapsedInitialized || !isPlayingFlag)
                {
                    displayedElapsedSeconds = desired;
                    displayedElapsedInitialized = true;
                }
                else
                {
                    displayedElapsedSeconds = Mathf.Lerp(displayedElapsedSeconds, desired, Math.Min(1f, 12f * deltaTime));
                }
            }

            float targetExtra = userIsSeeking ? 3f : (isHoveringOverTimeline ? 6f : 0f);
            timelineExtraHeight = Mathf.Lerp(timelineExtraHeight, targetExtra, Math.Min(1f, 12f * deltaTime));
        }

        private void UpdateButtonLayoutAndIcon()
        {
            const float btnSize = 28f;
            const float btnSpacing = 8f;
            const float buttonOffsetFromThumbnail = 30f;
            float buttonsYOffset = 16f + TitleTextSize + ArtistTextSize + 24f;
            float startXLocal = thumbnailRect.Right - layoutRect.Left + buttonOffsetFromThumbnail;

            SetLocalPosition(btnPrev, new Vec2(startXLocal, buttonsYOffset));
            SetLocalPosition(btnPlay, new Vec2(startXLocal + btnSize + btnSpacing, buttonsYOffset));
            SetLocalPosition(btnNext, new Vec2(startXLocal + 2f * (btnSize + btnSpacing), buttonsYOffset));

            UpdatePlayIcon(GetEffectivePlayingState());
        }

        private void UpdatePlayIcon(bool isPlaying)
        {
            var icon = isPlaying ? (Res.Pause ?? Res.Stop) : Res.Play;
            if (icon == null || ReferenceEquals(btnPlay.Image.Image, icon)) return;

            btnPlay.Image.Image = icon;
            btnPlay.Image.Color = Theme.IconColor;
        }

        private static void SetLocalPosition(UIObject obj, Vec2 position)
        {
            if (Math.Abs(obj.LocalPosition.X - position.X) <= 0.001f &&
                Math.Abs(obj.LocalPosition.Y - position.Y) <= 0.001f)
            {
                return;
            }

            obj.LocalPosition = position;
        }

        private void HandleTimelineInput()
        {
            var mousePos = RendererMain.CursorPosition;
            bool isMouseInBar = timelineBarRect.Contains(mousePos.X, mousePos.Y);
            isHoveringOverTimeline = isMouseInBar && IsHovering;
            double durationSeconds = timelineDuration?.TotalSeconds ?? 0;

            if (IsHovering && IsMouseDown && !mouseDownOverTimeline && isMouseInBar && durationSeconds > 0)
            {
                mouseDownOverTimeline = true;
                userIsSeeking = true;
                userSeekElapsed = GetSeekTime(mousePos.X, durationSeconds);
            }

            if (mouseDownOverTimeline && IsMouseDown && durationSeconds > 0)
                userSeekElapsed = GetSeekTime(mousePos.X, durationSeconds);

            if (mouseDownOverTimeline && !IsMouseDown)
            {
                mouseDownOverTimeline = false;

                if (userIsSeeking)
                {
                    TimeSpan? start;
                    lock (mediaLock)
                    {
                        start = currentTimeline?.StartTime;
                    }

                    if (start.HasValue)
                    {
                        var seekElapsed = userSeekElapsed;
                        var seekTarget = start.Value + seekElapsed;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (await MediaInfo.SeekCurrentSessionAsync(seekTarget).ConfigureAwait(false))
                                {
                                    lock (mediaLock)
                                    {
                                        lastSampleElapsed = seekElapsed;
                                        lastSampleReceivedAt = DateTime.UtcNow;
                                    }
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                RequestTimelineRefresh();
                            }
                        });
                    }

                    userIsSeeking = false;
                }
            }
        }

        private TimeSpan GetSeekTime(float mouseX, double durationSeconds)
        {
            float relative = Math.Clamp((mouseX - timelineBaseRect.Left) / Math.Max(1f, timelineBaseRect.Width), 0f, 1f);
            return TimeSpan.FromSeconds(relative * durationSeconds);
        }

        private void UpdateTextCache(float deltaTime)
        {
            string title;
            string artist;

            lock (mediaLock)
            {
                title = !string.IsNullOrEmpty(currentMedia?.Title) ? currentMedia!.Title! : "No media playing";
                artist = !string.IsNullOrEmpty(currentMedia?.Artist) ? currentMedia!.Artist! : "No media playing";
            }

            SetTextBlobs(title, artist);

            isTitleScrolling = titleTextWidth > Math.Max(1f, titleClipRect.Width);
            if (!isTitleScrolling)
            {
                titleScrollOffset = 0f;
                titleScrollTimer = 0f;
                return;
            }

            if (titleScrollTimer < TitleScrollDelay)
            {
                titleScrollTimer += deltaTime;
                return;
            }

            titleScrollOffset += TitleScrollSpeed * deltaTime;
            if (titleScrollOffset > titleTextWidth + 20f)
            {
                titleScrollOffset = 0f;
                titleScrollTimer = 0f;
            }
        }

        private void SetTextBlobs(string title, string artist)
        {
            if (fullTitleText != title)
            {
                fullTitleText = title;
                truncatedTitleText = DWText.Truncate(title, TitleTruncateChars);
                ReplaceTextBlob(ref fullTitleBlob, fullTitleText, titleFont);
                ReplaceTextBlob(ref truncatedTitleBlob, truncatedTitleText, titleFont);
                titleTextWidth = titleFont.MeasureText(fullTitleText);
                titleScrollOffset = 0f;
                titleScrollTimer = 0f;
            }

            string nextArtist = DWText.Truncate(artist, ArtistTruncateChars);
            if (artistText != nextArtist)
            {
                artistText = nextArtist;
                ReplaceTextBlob(ref artistBlob, artistText, artistFont);
            }
        }

        private void UpdateTimelineTextCache()
        {
            int elapsedSecond = displayedElapsedInitialized
                ? Math.Max(0, (int)Math.Floor(displayedElapsedSeconds))
                : -1;
            int durationSecond = timelineDuration.HasValue && timelineDuration.Value.TotalSeconds > 0
                ? Math.Max(0, (int)Math.Floor(timelineDuration.Value.TotalSeconds))
                : -1;

            if (elapsedSecond == cachedTimelineElapsedSecond &&
                durationSecond == cachedTimelineDurationSecond)
            {
                return;
            }

            cachedTimelineElapsedSecond = elapsedSecond;
            cachedTimelineDurationSecond = durationSecond;

            if (durationSecond > 0 && elapsedSecond >= 0)
            {
                elapsedSecond = Math.Min(elapsedSecond, durationSecond);
                SetTimelineText(
                    FormatTimeSpanForDisplay(TimeSpan.FromSeconds(elapsedSecond)),
                    "-" + FormatTimeSpanForDisplay(TimeSpan.FromSeconds(Math.Max(0, durationSecond - elapsedSecond))));
            }
            else
            {
                SetTimelineText("--:--", "--:--");
            }
        }

        private void SetTimelineText(string left, string right)
        {
            if (cachedTimelineLeftText != left)
            {
                cachedTimelineLeftText = left;
                ReplaceTextBlob(ref timelineLeftBlob, cachedTimelineLeftText, timelineFont);
            }

            if (cachedTimelineRightText != right)
            {
                cachedTimelineRightText = right;
                ReplaceTextBlob(ref timelineRightBlob, cachedTimelineRightText, timelineFont);
                cachedTimelineRightTextWidth = timelineFont.MeasureText(cachedTimelineRightText);
                layoutTimelineRightTextWidth = -1f;
            }
        }

        private void ReplaceTextBlob(ref SKTextBlob? blob, string text, SKFont font)
        {
            blob?.Dispose();
            blob = SKTextBlob.Create(text ?? string.Empty, font);
        }

        private static string FormatTimeSpanForDisplay(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);

            return string.Format("{0:D2}:{1:D2}", (int)ts.TotalMinutes, ts.Seconds);
        }

        private void UpdateVisualiserState()
        {
            bool hasMedia;
            lock (mediaLock)
            {
                hasMedia = currentMedia != null || thumbnailImage != null || pendingImage != null;
            }

            visualiser.SilentSetActive(hasMedia);
            SetVisualiserCapture(hasMedia && GetEffectivePlayingState());
        }

        public override bool WantsRealtimeUpdate
        {
            get
            {
                if (!ShouldRenderMediaPlayer()) return false;

                return userIsSeeking ||
                       mouseDownOverTimeline ||
                       animator.State != MediaAnimator.AnimState.Idle ||
                       Math.Abs(thumbnailAnim - (GetEffectivePlayingState() ? 1f : 0f)) > 0.01f ||
                       Math.Abs(timelineExtraHeight - (userIsSeeking ? 3f : (isHoveringOverTimeline ? 6f : 0f))) > 0.05f;
            }
        }

        public override bool WantsContinuousUpdate
        {
            get
            {
                if (!ShouldRenderMediaPlayer()) return false;
                return GetEffectivePlayingState() || isTitleScrolling || WantsRealtimeUpdate;
            }
        }

        private void EnsureLayout()
        {
            var nextRect = GetRect().Rect;
            bool baseChanged = layoutDirty || !RectsClose(layoutRect, nextRect);
            bool timelineChanged =
                baseChanged ||
                Math.Abs(layoutTimelineExtraHeight - timelineExtraHeight) > 0.001f ||
                Math.Abs(layoutTimelineRightTextWidth - cachedTimelineRightTextWidth) > 0.001f;

            if (!baseChanged && !timelineChanged) return;

            if (baseChanged)
            {
                layoutDirty = false;
                layoutRect = nextRect;

                float maxThumb = Math.Min(90f, layoutRect.Width * 0.35f);
                float thumbSize = Math.Max(12f, Math.Min(layoutRect.Height, maxThumb));
                thumbnailRect = SKRect.Create(layoutRect.Left, layoutRect.Top, thumbSize, thumbSize);
                localThumbnailRect = SKRect.Create(-thumbSize / 2f, -thumbSize / 2f, thumbSize, thumbSize);

                thumbnailPath?.Dispose();
                thumbnailPath = BuildSuperellipsePath(thumbnailRect, 30f, 1f);
                localThumbnailPath?.Dispose();
                localThumbnailPath = BuildSuperellipsePath(localThumbnailRect, 30f, 1f);

                textX = thumbnailRect.Right + 14f;
                titleBaseline = layoutRect.Top + 16f + TitleTextSize;
                artistBaseline = titleBaseline + ArtistTextSize + 6f;
                float titleWidth = Math.Max(1f, layoutRect.Width - (textX - layoutRect.Left) - 45f);
                titleClipRect = SKRect.Create(textX, layoutRect.Top + 16f, titleWidth, TitleTextSize + 4f);

                float barWidth = Math.Max(1f, layoutRect.Width - 2f * TimelineSidePadding);
                float barX = layoutRect.Left + (layoutRect.Width - barWidth) / 2f;
                timelineBarBaseY = layoutRect.Top + 95f + TimelineBarPadding;
                if (timelineBarBaseY + TimelineHeight > layoutRect.Bottom)
                    timelineBarBaseY = layoutRect.Bottom - TimelineHeight - TimelineBarPadding;

                timelineBaseRect = SKRect.Create(barX, timelineBarBaseY, barWidth, TimelineHeight);
            }

            layoutTimelineExtraHeight = timelineExtraHeight;
            layoutTimelineRightTextWidth = cachedTimelineRightTextWidth;
            float drawHeight = TimelineHeight + timelineExtraHeight;
            float drawY = timelineBarBaseY + 3.5f - ((drawHeight - TimelineHeight) / 2f);
            timelineBarRect = SKRect.Create(timelineBaseRect.Left, drawY, timelineBaseRect.Width, drawHeight);

            timelineTextBaseline = timelineBarBaseY + TimelineHeight + TimelineTextSize - 6f;
            timelineLeftX = timelineBaseRect.Left - TimelineSidePadding + 4f;
            timelineRightX = timelineBaseRect.Right + TimelineSidePadding - cachedTimelineRightTextWidth - 4f;
        }

        private static bool RectsClose(SKRect a, SKRect b)
        {
            return Math.Abs(a.Left - b.Left) <= 0.001f &&
                   Math.Abs(a.Top - b.Top) <= 0.001f &&
                   Math.Abs(a.Width - b.Width) <= 0.001f &&
                   Math.Abs(a.Height - b.Height) <= 0.001f;
        }

        private void DrawThumbnail(SKCanvas canvas, SKImage? image)
        {
            float flipScale = animator.GetFlipScale();
            bool isFlipping = animator.IsFlipping;
            float thumbScale = 0.8f + 0.2f * thumbnailAnim;
            float dimAlpha = (1f - thumbnailAnim) * 120f;
            float blurAmount = animator.BlurAmount;

            SKImageFilter? blurFilter = null;
            if (blurAmount > 0f)
            {
                blurFilter = SKImageFilter.CreateBlur(blurAmount, blurAmount);
                thumbnailPaint.ImageFilter = blurFilter;
                placeholderPaint.ImageFilter = blurFilter;
            }

            thumbnailPaint.IsAntialias = Settings.AntiAliasing;
            placeholderPaint.IsAntialias = Settings.AntiAliasing;
            placeholderPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.06f)).Value();
            dimPaint.Color = new SKColor(0, 0, 0, (byte)Math.Clamp(dimAlpha, 0f, 255f));

            if (isFlipping)
            {
                canvas.Save();
                canvas.Translate(thumbnailRect.MidX, thumbnailRect.MidY);
                canvas.Scale(flipScale * thumbScale, thumbScale);

                if (localThumbnailPath != null)
                    canvas.ClipPath(localThumbnailPath, antialias: Settings.AntiAliasing);

                DrawThumbnailContents(canvas, image, localThumbnailRect, placeholderPaint, thumbnailPaint);
                if (dimAlpha > 0.5f)
                    canvas.DrawRect(localThumbnailRect, dimPaint);

                canvas.Restore();
            }
            else
            {
                canvas.Save();
                canvas.Translate(thumbnailRect.MidX, thumbnailRect.MidY);
                canvas.Scale(thumbScale, thumbScale);
                canvas.Translate(-thumbnailRect.MidX, -thumbnailRect.MidY);

                if (thumbnailPath != null)
                    canvas.ClipPath(thumbnailPath, antialias: Settings.AntiAliasing);

                DrawThumbnailContents(canvas, image, thumbnailRect, placeholderPaint, thumbnailPaint);
                if (dimAlpha > 0.5f)
                    canvas.DrawRect(thumbnailRect, dimPaint);

                canvas.Restore();
            }

            thumbnailPaint.ImageFilter = null;
            placeholderPaint.ImageFilter = null;
            blurFilter?.Dispose();
        }

        private void DrawThumbnailContents(SKCanvas canvas, SKImage? image, SKRect rect, SKPaint placeholder, SKPaint imagePaint)
        {
            if (image != null)
            {
                canvas.DrawImage(image, rect, imagePaint);
                return;
            }

            canvas.DrawRoundRect(new SKRoundRect(rect, Math.Max(12f, rect.Width * 0.18f)), placeholder);
        }

        private void DrawText(SKCanvas canvas)
        {
            titlePaint.IsAntialias = Settings.AntiAliasing;
            artistPaint.IsAntialias = Settings.AntiAliasing;
            titlePaint.Color = GetColor(Theme.TextMain).Value();
            artistPaint.Color = GetColor(Theme.TextSecond).Value();

            if (isTitleScrolling && fullTitleBlob != null)
            {
                canvas.Save();
                canvas.ClipRect(titleClipRect, antialias: Settings.AntiAliasing);
                float x = textX - titleScrollOffset;
                canvas.DrawText(fullTitleBlob, x, titleBaseline, titlePaint);

                if (x + titleTextWidth < titleClipRect.Right)
                    canvas.DrawText(fullTitleBlob, x + titleTextWidth + 20f, titleBaseline, titlePaint);

                canvas.Restore();
            }
            else if (truncatedTitleBlob != null)
            {
                canvas.DrawText(truncatedTitleBlob, textX, titleBaseline, titlePaint);
            }

            if (artistBlob != null)
                canvas.DrawText(artistBlob, textX, artistBaseline, artistPaint);
        }

        private void DrawTimeline(SKCanvas canvas, bool hasMedia)
        {
            timelineTrackPaint.IsAntialias = Settings.AntiAliasing;
            timelineFillPaint.IsAntialias = Settings.AntiAliasing;
            timelineTextPaint.IsAntialias = Settings.AntiAliasing;

            timelineTrackPaint.Color = GetColor(Theme.WidgetBackground.Override(a: 0.04f)).Value();
            timelineFillPaint.Color = GetColor(Theme.TextMain.Override(a: 0.6f)).Value();
            timelineTextPaint.Color = GetColor(Theme.TextMain.Override(a: 0.55f)).Value();

            float radius = timelineBarRect.Height / 2f;
            canvas.DrawRoundRect(new SKRoundRect(timelineBarRect, radius), timelineTrackPaint);

            if (hasMedia && displayFill > 0.001f)
            {
                var fillRect = SKRect.Create(
                    timelineBarRect.Left,
                    timelineBarRect.Top,
                    Math.Max(radius, timelineBarRect.Width * Math.Clamp(displayFill, 0f, 1f)),
                    timelineBarRect.Height);
                canvas.DrawRoundRect(new SKRoundRect(fillRect, radius), timelineFillPaint);
            }

            if (timelineLeftBlob != null)
                canvas.DrawText(timelineLeftBlob, timelineLeftX, timelineTextBaseline, timelineTextPaint);

            if (timelineRightBlob != null)
                canvas.DrawText(timelineRightBlob, timelineRightX, timelineTextBaseline, timelineTextPaint);
        }
    }
}
