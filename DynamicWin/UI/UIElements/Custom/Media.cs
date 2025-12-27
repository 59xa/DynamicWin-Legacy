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

/*
 * 
 *   Overview:
 *    - Implements media playback interface similar to the media control interface inside Apple's Dynamic Island
 *    - Supersedes Legacy Media Playback Control Widget (MediaWidget.cs)
 *
 *   Author:                 59xa
 *   GitHub:                 https://github.com/59xa
 *   Implementation Date:    26 December 2025
 *   Last Modified:          27 December 2025
 *
 */

namespace DynamicWin.UI.UIElements.Custom
{
    public class Media : UIObject
    {
        private CancellationTokenSource? cts;
        private DynamicWin.Utils.Media? currentMedia;
        private SKBitmap? thumbnailBitmap; // Currently cached decoded bitmap
        private SKBitmap? pendingBitmap; // Newly decoded bitmap waiting to animate in
        private DynamicWin.Utils.Media? pendingMedia; // Pending metadata object
        private readonly object mediaLock = new object();
        private TimeSpan fetchInterval = TimeSpan.FromSeconds(1);

        // Keys to detect duplicates
        private string? currentMediaKey;
        private string? pendingMediaKey;

        // Animation state
        enum AnimState { Idle, BlurIn, Flip, BlurOut }
        private AnimState animState = AnimState.Idle;
        private float animTimer = 0f;
        private float blurAmount = 0f;
        private SKBitmap? previousBitmap = null; // Bitmap that is being replaced

        // Animation durations (seconds)
        private const float blurDur = 0.18f;
        private const float flipDur = 0.36f;
        private const float blurOutDur = 0.18f;
        private const float maxBlur = 8f;

        // Playback controls and progress
        private MediaController controller;
        private DWImageButton? btnPrev;
        private DWImageButton? btnPlay;
        private DWImageButton? btnNext;
        private DWProgressBar? progressBar;

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

        public Media(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, size, alignment)
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
            // 59xa: kinda broken rn ngl
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

            // TODO: need to make this functional
            progressBar = new DWProgressBar(this, new Vec2(0, -10), new Vec2(25, 6), UIAlignment.BottomCenter)
            {
                roundRadius = 6f,
                contentColor = Theme.Primary.Override(a: 0.9f)
            };
            // Show progress bar
            progressBar.SilentSetActive(false);
            AddLocalObject(progressBar);
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

            // Drive animation
            if (animState != AnimState.Idle)
            {
                animTimer += deltaTime;

                if (animState == AnimState.BlurIn)
                {
                    float t = Math.Min(1f, animTimer / blurDur);
                    float e = Easings.EaseInOutCubic(t);
                    blurAmount = Mathf.Lerp(0f, maxBlur, e);
                    if (t >= 1f)
                    {
                        // Start flip
                        animState = AnimState.Flip;
                        animTimer = 0f;
                    }
                }
                else if (animState == AnimState.Flip)
                {
                    float t = Math.Min(1f, animTimer / flipDur);
                    float e = Easings.EaseInOutCubic(t);
                    // At halfway (eased) swap bitmaps
                    if (e >= 0.5f && pendingBitmap != null)
                    {
                        lock (mediaLock)
                        {
                            // Swap displayed bitmap and metadata
                            if (thumbnailBitmap != null && !thumbnailBitmap.IsNull)
                            {
                                try { thumbnailBitmap.Dispose(); } catch { }
                            }
                            thumbnailBitmap = pendingBitmap;
                            pendingBitmap = null;

                            // Update metadata key and currentMedia only when swapped
                            currentMediaKey = pendingMediaKey;
                            pendingMediaKey = null;

                            // Commit pending metadata as current
                            if (pendingMedia != null)
                            {
                                currentMedia = pendingMedia;
                                pendingMedia = null;
                            }
                        }
                    }

                    // End flip
                    if (t >= 1f)
                    {
                        animState = AnimState.BlurOut;
                        animTimer = 0f;
                    }
                }
                else if (animState == AnimState.BlurOut)
                {
                    float t = Math.Min(1f, animTimer / blurOutDur);
                    float e = Easings.EaseInOutCubic(t);
                    blurAmount = Mathf.Lerp(maxBlur, 0f, e);
                    if (t >= 1f)
                    {
                        // Finish
                        blurAmount = 0f;
                        animState = AnimState.Idle;
                        animTimer = 0f;

                        // Dispose previousBitmap if exists
                        if (previousBitmap != null)
                        {
                            try { previousBitmap.Dispose(); } catch { }
                            previousBitmap = null;
                        }
                    }
                }
            }
            else
            {
                // Start animation if a pendingBitmap exists when idle
                lock (mediaLock)
                {
                    if (pendingBitmap != null)
                    {
                        previousBitmap = thumbnailBitmap;
                        animState = AnimState.BlurIn;
                        animTimer = 0f;
                    }
                }
            }

            // Update timeline periodically
            timelineTimer += deltaTime;
            if (timelineTimer >= timelineInterval && !timelineFetchInProgress)
            {
                timelineTimer = 0f;
            }

            // Position controls relative to layout
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

                // Draw progress bar manually in Draw(); avoid activating child progressBar to prevent duplicate rendering
                if (progressBar != null)
                {
                    float barY = btnY + btnSize + 8f;
                    // Make the progress bar fill the remaining horizontal space of the widget
                    // (start at left padding and extend to right padding)
                    float barLeftLocal = padding; // Local X relative to rect.Left
                    float barWidth = Math.Max(80f, rect.Width - padding * 2f);
                    progressBar.Size = new Vec2(barWidth, 6f);
                    progressBar.LocalPosition = new Vec2(barLeftLocal, barY);

                    // Do not change child active state as manual draw uses timelinePosition/timelineDuration directly
                    // Update child's value for completeness (not used for rendering)
                    if (timelineDuration.HasValue && timelinePosition.HasValue && timelineDuration.Value.TotalSeconds > 0)
                    {
                        progressBar.value = (float)Math.Max(0.0, Math.Min(1.0, timelinePosition.Value.TotalSeconds / timelineDuration.Value.TotalSeconds));
                    }
                    else
                    {
                        progressBar.value = 0f;
                    }
                }

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
                                // If there's no current thumbnail yet, set immediately
                                if (thumbnailBitmap == null && newBmp != null)
                                {
                                    thumbnailBitmap = newBmp;
                                    newBmp = null;
                                    currentMedia = media;
                                    currentMediaKey = key;
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

            lock (mediaLock)
            {
                currentMedia = null;
                if (thumbnailBitmap != null)
                {
                    try { thumbnailBitmap.Dispose(); } catch { }
                    thumbnailBitmap = null;
                }

                if (pendingBitmap != null)
                {
                    try { pendingBitmap.Dispose(); } catch { }
                    pendingBitmap = null;
                }

                if (previousBitmap != null)
                {
                    try { previousBitmap.Dispose(); } catch { }
                    previousBitmap = null;
                }

                currentMediaKey = null;
                pendingMediaKey = null;
                pendingMedia = null;

                // Hide controls
                if (progressBar != null) progressBar.SilentSetActive(false);
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

            // Draw background matching other widgets unless fully transparent
            var bgCol = GetColor(Theme.IslandBackground);
            if (bgCol.a > 0.001f)
            {
                using (var bgPaint = GetPaint())
                {
                    bgPaint.IsStroke = false;
                    bgPaint.IsAntialias = true;
                    // Use widget background colour so visuals match other widgets
                    bgPaint.Color = bgCol.Value();
                    // Ensure no image filter remains that could produce visible artefacts when alpha is low
                    bgPaint.ImageFilter = null;
                    bgPaint.BlendMode = SKBlendMode.SrcOver;
                    canvas.DrawRoundRect(rr, bgPaint);
                }
            }

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
                // Compute flip scale
                float flipScale = 1f;
                bool doFlip = (animState == AnimState.Flip);
                if (doFlip)
                {
                    float t = Math.Min(1f, animTimer / flipDur);
                    float e = Easings.EaseInOutCubic(t);
                    flipScale = (float)Math.Cos(e * (float)Math.PI); // Eased 1 -> 0 -> -1
                }

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
                    paint.ImageFilter = blurAmount > 0f ? SKImageFilter.CreateBlur(blurAmount, blurAmount) : null;
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
                            p.ImageFilter = blurAmount > 0f ? SKImageFilter.CreateBlur(blurAmount, blurAmount) : null;
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
                    paint.ImageFilter = blurAmount > 0f ? SKImageFilter.CreateBlur(blurAmount, blurAmount) : null;
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
                            p.ImageFilter = blurAmount > 0f ? SKImageFilter.CreateBlur(blurAmount, blurAmount) : null;
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

            // Truncate to fit available width
            float availableWidth = rect.Width - (textX - rect.Left);
            if (!string.IsNullOrEmpty(title))
            {
                var displayTitle = DWText.Truncate(title, 60);
                canvas.DrawText(displayTitle, textX, textY + titlePaint.TextSize, titlePaint);
            }

            if (!string.IsNullOrEmpty(artist))
            {
                var displayArtist = DWText.Truncate(artist, 60);
                canvas.DrawText(displayArtist, textX, textY + titlePaint.TextSize + artistPaint.TextSize + 6f, artistPaint);
            }

            // Draw progress bar and times manually so it's always visible
            try
            {
                // Position the progress bar to span the full width inside padding
                float titleHeight = 14f;
                float artistHeight = 12f;
                float buttonsYOffset = 16f + titleHeight + artistHeight + 24f;
                float btnSize = 28f;
                float btnSpacing = 8f;
                float buttonsTotal = btnSize * 3f + btnSpacing * 2f;
                float btnY = buttonsYOffset;

                float barY = btnY + btnSize + 8f;
                float barWidth = Math.Max(80f, rect.Width * 2f);

                // Clamp fill to [0,1] and smooth value
                float targetFill = 0f;
                if (timelineDuration.HasValue && timelinePosition.HasValue && timelineDuration.Value.TotalSeconds > 0)
                {
                    targetFill = (float)Math.Max(0.0, Math.Min(1.0, timelinePosition.Value.TotalSeconds / timelineDuration.Value.TotalSeconds));
                }

                // Smooth displayed fill
                displayFill = Mathf.Lerp(displayFill, targetFill, 8f * RendererMain.Instance.DeltaTime);

                var barRect = SKRect.Create(rect.Left, rect.Top + barY, barWidth, 6f);

                // Background track
                using (var p = GetPaint())
                {
                    p.IsAntialias = true;
                    p.IsStroke = false;
                    p.Color = GetColor(new Col(0.05f, 0.05f, 0.05f, 0.9f)).Value(); // Dark subtle track
                    canvas.DrawRoundRect(new SKRoundRect(barRect, 3f), p);
                }

                // Fill
                var fillRect = SKRect.Create(barRect.Left, barRect.Top, barRect.Width * displayFill, barRect.Height);
                using (var p2 = GetPaint())
                {
                    p2.IsAntialias = true;
                    p2.IsStroke = false;
                    p2.Color = GetColor(Theme.Primary).Value();
                    canvas.DrawRoundRect(new SKRoundRect(fillRect, 3f), p2);
                }

                // Subtle overlay to mimic inset
                using (var p3 = GetPaint())
                {
                    p3.IsAntialias = true;
                    p3.IsStroke = true;
                    p3.StrokeWidth = 1f;
                    p3.Color = GetColor(new Col(0f, 0f, 0f, 0.12f)).Value();
                    canvas.DrawRoundRect(new SKRoundRect(barRect, 3f), p3);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Draw progress error: " + ex.Message);
            }
        }
    }
}
