using DynamicWin.Main;
using DynamicWin.UI;
using NAudio.Wave;
using SkiaSharp;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
using System;

/*
 * 
 *   Overview:
 *    - Implement a new audio visualiser that aims to look closely similar to iOS audio visualisers.
 *    - Refactored for performance and improved responsiveness.
 *    - Reuse buffers and precompute bit-reversal indices and window.
 *    - Simplified noise-gate and normalisation so bars react more naturally.
 *    - Added optional blurred media thumbnail background fetched via MediaController/MediaInfo.
 *
 *   Author:                 59xa
 *   GitHub:                 https://github.com/59xa
 *   Implementation Date:    18 May 2025
 *   Last Modified:          11 January 2026
 *
 */

namespace DynamicWin.Utils
{
    public class AudioVisualiser : UIObject
    {
        // Initialise variables
        private const int V = 5;
        private readonly int fftLength = 2048;
        private readonly int barCount = 6;

        private float[] fftMagnitudes;
        private float[] barHeight;
        private float[] barGain;

        private float[] targetHeights; // Re-use per-frame to avoid allocations

        private float[] bandBalance = new float[] { 1f, 1f, 1.15f, 1.10f, 1.25f, 1.35f };

        private WasapiLoopbackCapture? capture;
        private readonly object fftLock = new object();

        // Precomputed
        private Complex[] fftBuffer;
        private float[] window;
        private int[] bitRevIndices;
        private int[][] barBinIndices;
        private int[] barBinCounts;

        // Running RMS per bar
        private float[] barSumSquares;

        public float attackRate = 60f;
        public float releaseRate = 20f;
        public float maxChangePerSecond = 18f;

        private float[] bandNoiseEstimate;
        public float noiseEstimateRiseRate = 0.4f;
        public float noiseEstimateFallRate = 6.0f;
        public float gateMultiplier = 1.35f;
        public float minGateThreshold = 1e-8f;

        private float[] bandPeakEstimate;
        public float peakRiseRate = 12f;
        public float peakFallRate = 6.0f;
        public float outputBoost = 1.0f;

        // Thumbnail data
        private volatile byte[]? cachedThumbnailBytes; // latest encoded bytes known to the object
        private volatile byte[]? pendingThumbnailBytes; // bytes awaiting decode (set by events)
        private SKImage? cachedThumbnailImage; // decoded image used for drawing (owned)
        private SKImage? previousThumbnailImage; // previous decoded image for crossfade
        private bool thumbnailDirty = false; // indicates pendingThumbnailBytes differs from cached
        private DateTime lastDecodeTime = DateTime.MinValue;
        private float thumbnailFade = 3f;
        public float ThumbnailFadeDuration { get; set; } = 0.35f;
        private readonly object thumbLock = new object();
        public bool UseThumbnailBackground { get; set; } = false;
        public float ThumbnailFetchInterval { get; set; } = 1.0f;
        public float ThumbnailBlurAmount { get; set; } = 5f;
        private bool thumbnailServiceSubscribed;

        public Col Primary;
        public Col Secondary;
        private SKColor thumbnailColorAdjustment;

        private float averageAmplitude = 0f;
        public float AverageAmplitude { get => averageAmplitude; }

        private bool enableColourTransition = true;
        public bool EnableColourTransition { get => enableColourTransition; set => enableColourTransition = value; }

        private bool enableDotWhenLow = true;
        public bool EnableDotWhenLow { get => enableDotWhenLow; set => enableDotWhenLow = value; }
        public float BlurAmount { get; set; } = 0f;

        private float barSpacing = 1.5f;
        public float BarSpacing
        {
            get => barSpacing;
            set => barSpacing = Math.Clamp(value, 0f, 20f);
        }
        public float BarGap { get => BarSpacing; set => BarSpacing = value; }

        // Initialise class
        public AudioVisualiser(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopRight, Col Primary = null, Col Secondary = null) : base(parent, position, size, alignment)
        {
            roundRadius = V;
            this.Primary = Primary ?? Theme.Primary;
            this.Secondary = Secondary ?? Theme.Secondary.Override(a: 0.5f);

            thumbnailColorAdjustment = GetColor(Theme.TextMain.Override(a: 215)).Value();

            fftMagnitudes = new float[fftLength / 2];
            barHeight = new float[barCount];
            barGain = new float[barCount];
            bandNoiseEstimate = new float[barCount];
            bandPeakEstimate = new float[barCount];

            targetHeights = new float[barCount];

            fftBuffer = new Complex[fftLength];
            window = new float[fftLength];
            bitRevIndices = new int[fftLength];

            // Pre-compute Hann window and bit-reversal indices
            int log2 = (int)Math.Log2(fftLength);
            for (int i = 0; i < fftLength; i++)
            {
                window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (fftLength - 1)));
                bitRevIndices[i] = BitReverse(i, log2);
            }

            for (int i = 0; i < barCount; i++)
            {
                barHeight[i] = 0f;
                barGain[i] = 1f;
                bandNoiseEstimate[i] = 1e-9f;
                bandPeakEstimate[i] = 1e-7f;
            }

            barSumSquares = new float[barCount];

            // Precompute FFT bin mapping
            InitBarBinMapping(44100f);
            SetCapturing(true);

            // Subscribe to central thumbnail service. Only capture bytes in event handlers to avoid
            // creating Skia objects on background threads which can cause native crashes.
            SetThumbnailSubscription(true);

            // Prime thumbnail cache from central service using bytes if available. Avoid creating SKImage here.
            try
            {
                var bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                if (bytes != null && bytes.Length > 0)
                {
                    lock (thumbLock)
                    {
                        cachedThumbnailBytes = (byte[])bytes.Clone();
                        pendingThumbnailBytes = cachedThumbnailBytes;
                        thumbnailDirty = true;
                        thumbnailFade = 0f;
                        lastDecodeTime = DateTime.MinValue;
                    }
                }
            }
            catch { }
        }

        private void InitBarBinMapping(float sampleRate)
        {
            barBinIndices = new int[barCount][];
            barBinCounts = new int[barCount];

            // Safety: Ensure bandBalance matches barCount if the amount is changed for later
            if (bandBalance.Length != barCount)
            {
                // Generate a generic "Pink Noise" compensation curve (boosts highs slightly)
                // if the manual array length doesn't match the bar count
                bandBalance = new float[barCount];
                for (int i = 0; i < barCount; i++)
                {
                    bandBalance[i] = 1.0f + (0.8f * (i / (float)barCount));
                }
            }

            double minFreq = 20.0;  // 20Hz - human hearing start
            double maxFreq = 6000.0; // 12kHz - frequency end

            // Use Logarithmic scale for natural frequency distribution
            double logMin = Math.Log10(minFreq);
            double logMax = Math.Log10(maxFreq);
            double range = logMax - logMin;

            for (int i = 0; i < barCount; i++)
            {
                // Calculate the frequency range for this specific bar
                double valStart = logMin + (i * range / barCount);
                double valEnd = logMin + ((i + 1) * range / barCount);

                float lowFreq = (float)Math.Pow(10, valStart);
                float highFreq = (float)Math.Pow(10, valEnd);

                // Map Frequency to FFT Bin Indices
                int lowIndex = Math.Max(0, (int)(lowFreq / sampleRate * fftLength));
                int highIndex = Math.Min((int)(highFreq / sampleRate * fftLength), fftMagnitudes.Length - 1);

                // Ensure we capture at least one bin per bar
                if (highIndex < lowIndex) highIndex = lowIndex;

                // Store the indices
                int len = highIndex - lowIndex + 1;
                var arr = new int[len];
                for (int j = 0; j < len; j++) arr[j] = lowIndex + j;

                barBinIndices[i] = arr;
                barBinCounts[i] = len;
            }
        }

        public void SetCapturing(bool enabled)
        {
            if (enabled)
                StartCapture();
            else
                StopCapture(resetBars: true);
        }

        public void SetThumbnailSubscription(bool enabled)
        {
            if (thumbnailServiceSubscribed == enabled) return;

            if (enabled)
            {
                MediaThumbnailService.Instance.Subscribe(OnThumbnailChanged);
                MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChangedEvent;
                thumbnailServiceSubscribed = true;
                return;
            }

            try { MediaThumbnailService.Instance.Unsubscribe(OnThumbnailChanged); } catch { }
            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChangedEvent; } catch { }
            thumbnailServiceSubscribed = false;

            lock (thumbLock)
            {
                cachedThumbnailBytes = null;
                pendingThumbnailBytes = null;
                thumbnailDirty = false;
                cachedThumbnailImage?.Dispose();
                cachedThumbnailImage = null;
                previousThumbnailImage?.Dispose();
                previousThumbnailImage = null;
            }
        }

        private void StartCapture()
        {
            if (capture != null || DynamicWinMain.defaultDevice == null) return;

            try
            {
                capture = new WasapiLoopbackCapture(DynamicWinMain.defaultDevice);
                capture.DataAvailable += OnDataAvailable;
                InitBarBinMapping(capture.WaveFormat.SampleRate);
                capture.StartRecording();
            }
            catch
            {
                StopCapture(resetBars: true);
            }
        }

        private void StopCapture(bool resetBars)
        {
            var activeCapture = capture;
            capture = null;

            if (activeCapture != null)
            {
                try { activeCapture.DataAvailable -= OnDataAvailable; } catch { }
                try { activeCapture.StopRecording(); } catch { }
                try { activeCapture.Dispose(); } catch { }
            }

            if (resetBars)
            {
                lock (fftLock)
                {
                    Array.Clear(barSumSquares, 0, barSumSquares.Length);
                    Array.Clear(targetHeights, 0, targetHeights.Length);
                    Array.Clear(barHeight, 0, barHeight.Length);
                    averageAmplitude = 0f;
                }
            }
        }

        private void OnThumbnailChanged(Media? m)
        {
            try
            {
                // Prefer ThumbnailData from media object; fall back to service cache for compatibility
                byte[]? bytes = m?.ThumbnailData;
                if (bytes == null || bytes.Length == 0)
                {
                    try
                    {
                        bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                    }
                    catch { }
                }

                lock (thumbLock)
                {
                    pendingThumbnailBytes = bytes != null && bytes.Length > 0 ? (byte[])bytes.Clone() : null;
                    cachedThumbnailBytes = pendingThumbnailBytes;
                    thumbnailDirty = true;
                    thumbnailFade = 0f;
                }
            }
            catch { }
        }

        private void OnThumbnailChangedEvent(object? sender, MediaChangedEventArgs e)
        {
            try
            {
                lock (thumbLock)
                {
                    // If there is no media and no bytes, clear cache to avoid showing stale images
                    if (e.Media == null && (e.ThumbnailBytes == null || e.ThumbnailBytes.Length == 0))
                    {
                        pendingThumbnailBytes = null;
                        cachedThumbnailBytes = null;
                        thumbnailDirty = true;
                        return;
                    }

                    // Prefer bytes from event; fall back to service cache
                    byte[]? bytes = e.ThumbnailBytes;
                    if (bytes == null || bytes.Length == 0)
                    {
                        try
                        {
                            bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                        }
                        catch { }
                    }

                    pendingThumbnailBytes = bytes != null && bytes.Length > 0 ? (byte[])bytes.Clone() : null;
                    cachedThumbnailBytes = pendingThumbnailBytes;
                    thumbnailDirty = true;
                    thumbnailFade = 0f;
                }
            }
            catch { }
        }

        /// <summary>
        /// Destroys previously gathered frequency data from Windows Audio Services API
        /// </summary>
        public override void OnDestroy()
        {
            base.OnDestroy();

            // Unsubscribe
            SetThumbnailSubscription(false);

            StopCapture(resetBars: false);

            // Dispose cached thumbnail image
            lock (thumbLock)
            {
                cachedThumbnailImage?.Dispose();
                cachedThumbnailImage = null;
                cachedThumbnailBytes = null;
                pendingThumbnailBytes = null;
                previousThumbnailImage?.Dispose();
                previousThumbnailImage = null;
            }
        }

        /// <summary>
        /// Override method to calculate visualiser values to display
        /// </summary>
        /// <param name="deltaTime">Value to display frames per second</param>
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Thumbnail fade handling
            if (UseThumbnailBackground && thumbnailFade < 1f)
            {
                thumbnailFade += deltaTime / Math.Max(0.0001f, ThumbnailFadeDuration);
                if (thumbnailFade >= 1f)
                {
                    thumbnailFade = 1f;
                    lock (thumbLock)
                    {
                        // previousThumbnailImage disposed after fade completes
                        previousThumbnailImage?.Dispose();
                        previousThumbnailImage = null;
                    }
                }
            }

            // Re-use targetHeights array to avoid per-frame allocations
            for (int t = 0; t < barCount; t++) targetHeights[t] = 0f;

            lock (fftLock)
            {
                // Define specific weights 
                // Bar 0: Sub (1.2x) -> Strongest
                // Bar 1: Bass (0.8x) -> Dipped to let bar 0 lead
                // Bar 5: Highs (2.5x) -> Extreme boost for hi-hat visibility
                float[] barWeights = { 1.0f, 0.9f, 1.15f, 1.5f, 2.0f, 2.5f };

                for (int i = 0; i < barCount; i++)
                {
                    float rms = MathF.Sqrt(barSumSquares[i] / Math.Max(barBinCounts[i], 1));

                    // Sensitive floor: using -70dB ensures quiet tail-end of high frequencies are caught
                    float db = 20f * MathF.Log10(Math.Max(rms, 1e-7f));
                    float normalized = Math.Clamp((db + 70f) / 60f, 0f, 1f);

                    // Apply the specific weights
                    normalized *= barWeights[i];

                    // Apply individual gain from array
                    normalized *= barGain[i];

                    // Punch correction (gamma)
                    // Lower power (1.4) is used for highs so they are more "reactive"
                    // and a higher power (2.2) for bass so it feels "sturdy"
                    float power = (i < 2) ? 2.2f : 1.4f;
                    normalized = MathF.Pow(normalized, power);

                    targetHeights[i] = Math.Clamp(normalized, 0f, 1f);
                }
            }

            // Compute average amplitude without LINQ to avoid iterator overhead
            float sumAvg = 0f;
            for (int i = 0; i < barCount; i++) sumAvg += targetHeights[i];
            averageAmplitude = sumAvg / Math.Max(1, barCount);

            if (averageAmplitude < 0.015f)
            {
                for (int i = 0; i < barCount; i++) targetHeights[i] = 0f;
            }

            // Smooth interpolation
            for (int i = 0; i < barCount; i++)
            {
                float current = barHeight[i];
                float target = targetHeights[i];
                float alpha = 1f - MathF.Exp(-(target > current ? attackRate : releaseRate) * deltaTime);
                float newValue = current + (target - current) * alpha;
                float maxStep = maxChangePerSecond * deltaTime;
                float delta = Math.Clamp(newValue - current, -maxStep, maxStep);
                barHeight[i] = current + delta;
            }
        }

        /// <summary>
        /// Handler to check if any data can be fetched from device
        /// </summary>
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int samplesAvailable = e.BytesRecorded / 4;
            int copyLen = Math.Min(samplesAvailable, fftLength);

            for (int i = 0; i < fftLength; i++)
            {
                float s = (i < copyLen) ? BitConverter.ToSingle(e.Buffer, i * 4) : 0f;
                fftBuffer[i] = new Complex(s * window[i], 0);
            }

            FFT(fftBuffer);

            float magnitudeScale = 2.0f / fftLength;

            lock (fftLock)
            {
                int magLen = fftMagnitudes.Length;
                for (int i = 0; i < magLen; i++)
                {
                    // Approximate magnitude for speed
                    float mag = ApproxMagnitude(fftBuffer[i]) * magnitudeScale;
                    fftMagnitudes[i] = mag < 1e-12f ? 0f : mag;
                }

                // Update running RMS per bar (use indexed loops to avoid foreach overhead)
                for (int i = 0; i < barCount; i++)
                {
                    float sum = 0f;
                    var bins = barBinIndices[i];
                    for (int j = 0; j < bins.Length; j++)
                    {
                        int bin = bins[j];
                        float v = fftMagnitudes[bin];
                        sum += v * v;
                    }
                    barSumSquares[i] = sum;
                }
            }
        }

        private float ApproxMagnitude(Complex c)
        {
            float absRe = MathF.Abs((float)c.Real);
            float absIm = MathF.Abs((float)c.Imaginary);
            return MathF.Max(absRe, absIm) + 0.4f * MathF.Min(absRe, absIm);
        }

        /// <summary>
        /// Performs Fast Fourier Transform on each sample (in-place). Uses precomputed bit-reversal indices.
        /// </summary>
        private void FFT(Complex[] buffer)
        {
            int n = buffer.Length;
            for (int i = 0; i < n; i++)
            {
                int j = bitRevIndices[i];
                if (j > i) (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }

            int m = (int)Math.Log2(n);
            for (int s = 1; s <= m; s++)
            {
                int mval = 1 << s;
                int half = mval >> 1;
                double theta = -2.0 * Math.PI / mval;
                Complex wm = new Complex(Math.Cos(theta), Math.Sin(theta));

                for (int k = 0; k < n; k += mval)
                {
                    Complex w = Complex.One;
                    for (int j = 0; j < half; j++)
                    {
                        Complex t = w * buffer[k + j + half];
                        Complex u = buffer[k + j];
                        buffer[k + j] = u + t;
                        buffer[k + j + half] = u - t;
                        w *= wm;
                    }
                }
            }
        }

        private int BitReverse(int n, int bits)
        {
            int reversed = 0;
            for (int i = 0; i < bits; i++)
            {
                reversed = (reversed << 1) | (n & 1);
                n >>= 1;
            }
            return reversed;
        }

        public override void Draw(SKCanvas canvas)
        {
            if (capture == null) return;

            SKImage? thumbnailImage = null;
            SKImage? prevThumb = null;

            if (UseThumbnailBackground)
            {
                // Throttle decode operations to avoid CPU spike when songs change quickly.
                // Only decode if there's a pending change and the fetch interval has elapsed.
                byte[]? bytesToDecode = null;
                bool shouldDecode = false;
                DateTime now = DateTime.UtcNow;

                lock (thumbLock)
                {
                    // If a new pending byte array exists and we haven't decoded it yet, or cached is null
                    if (thumbnailDirty && pendingThumbnailBytes != null)
                    {
                        // Allow immediate decode only if enough time passed since last decode to avoid spikes
                        if ((now - lastDecodeTime).TotalSeconds >= ThumbnailFetchInterval || cachedThumbnailImage == null)
                        {
                            bytesToDecode = (byte[])pendingThumbnailBytes.Clone();
                            // Mark as not dirty until we actually finish decoding
                            thumbnailDirty = false;
                            shouldDecode = true;
                        }
                    }
                    else if (cachedThumbnailImage == null && pendingThumbnailBytes != null && (now - lastDecodeTime).TotalSeconds >= ThumbnailFetchInterval)
                    {
                        bytesToDecode = (byte[])pendingThumbnailBytes.Clone();
                        thumbnailDirty = false;
                        shouldDecode = true;
                    }

                    // Provide references for drawing (won't be disposed here)
                    thumbnailImage = cachedThumbnailImage;
                    prevThumb = previousThumbnailImage;
                }

                if (shouldDecode && bytesToDecode != null && bytesToDecode.Length > 0)
                {
                    // Decode outside lock to avoid blocking event handlers. If decode fails, keep previous image.
                    SKImage? newImage = null;
                    try
                    {
                        using var bmp = SKBitmap.Decode(bytesToDecode);
                        if (bmp != null)
                        {
                            newImage = SKImage.FromBitmap(bmp);
                        }
                    }
                    catch
                    {
                        newImage = null;
                    }

                    lock (thumbLock)
                    {
                        lastDecodeTime = DateTime.UtcNow;

                        if (newImage != null)
                        {
                            // Move current cached to previous for crossfade and set new cached image
                            if (cachedThumbnailImage != null)
                            {
                                previousThumbnailImage?.Dispose();
                                previousThumbnailImage = cachedThumbnailImage;
                            }

                            cachedThumbnailImage = newImage;
                            cachedThumbnailBytes = (byte[])bytesToDecode.Clone();
                            thumbnailFade = 0f;

                            thumbnailImage = cachedThumbnailImage;
                            prevThumb = previousThumbnailImage;
                        }
                        else
                        {
                            // Decoding failed, nothing to do; keep existing images
                        }
                    }
                }
            }

            float width = Size.X;
            float height = Size.Y;
            float centerY = Position.Y + height / 2;

            float maxSpacing = barCount > 1 ? Math.Max(0f, (width - barCount * 0.5f) / (barCount - 1)) : 0f;
            float spacing2 = Math.Clamp(BarSpacing, 0f, maxSpacing);
            float totalSpacing2 = spacing2 * (barCount - 1);
            float barWidth2 = Math.Max(0.5f, (width - totalSpacing2) / barCount);
            float visualBoost = 1.5f;
            float dotHeight = barWidth2;

            for (int i = 0; i < barCount; i++)
            {
                // Calculate the dynamic height
                float rawHeight = barHeight[i] * visualBoost;
                float dynamicHeight = rawHeight * height * 0.8f;

                float bH = EnableDotWhenLow
                    ? Math.Max(dotHeight, dynamicHeight)
                    : dynamicHeight;

                float xBase = Position.X + i * (barWidth2 + spacing2);
                float barTopY = centerY - bH / 2;

                var rect = SKRect.Create(xBase, barTopY, barWidth2, bH);
                var roundRect = new SKRoundRect(rect, barWidth2 / 2, barWidth2 / 2);

                // Color logic: dot must stay at the "Secondary" color until it starts growing
                // We calculate a 'colorActivity' based on how much it has grown past the dot
                float growthAboveDot = Math.Max(0, (bH - dotHeight) / (height * 0.5f));
                float lerpAmount = EnableDotWhenLow ? growthAboveDot : barHeight[i];

                // Slight baseline alpha for the dot so it's always subtly there
                float activeLerp = Math.Clamp(lerpAmount, 0f, 1f);

                if (UseThumbnailBackground && thumbnailImage != null)
                {
                    try
                    {
                        DrawThumbnailBar(canvas, roundRect, thumbnailImage, prevThumb, width, height, thumbnailFade);

                        using var overlay = new SKPaint
                        {
                            Color = thumbnailColorAdjustment
                        };
                        canvas.DrawRoundRect(roundRect, overlay);
                    }
                    catch
                    {
                        // Thumbnail drawing is optional – ignore failures
                    }
                }
                else
                {
                    Col pCol = EnableColourTransition
                        ? Col.Lerp(Secondary, Primary, activeLerp)
                        : Primary;

                    SKColor baseColor = GetColor(pCol).Value();
                    byte alpha = (byte)Math.Max(120, (int)baseColor.Alpha); // Keep dots visible but dim

                    SKColor startColor = baseColor.WithAlpha(alpha);
                    SKColor endColor = new SKColor(
                        (byte)(baseColor.Red * 0.8),
                        (byte)(baseColor.Green * 0.8),
                        (byte)(baseColor.Blue * 0.8),
                        alpha
                    );

                    using var paintBar = new SKPaint
                    {
                        IsAntialias = Settings.AntiAliasing,
                        Shader = SKShader.CreateLinearGradient(
                            new SKPoint(rect.Left, rect.Bottom),
                            new SKPoint(rect.Left, rect.Top),
                            new[] { startColor, endColor },
                            new float[] { 0, 1 },
                            SKShaderTileMode.Clamp
                        ),
                    };

                    if (Settings.AllowBlur && BlurAmount > 0)
                        paintBar.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurAmount);

                    canvas.DrawRoundRect(roundRect, paintBar);
                }
            }

            // Do not dispose cached images here - they are owned by this object and will be disposed in OnDestroy or when replaced
        }

        private void DrawThumbnailBar(SKCanvas canvas, SKRoundRect roundRect, SKImage current, SKImage? previous, float totalWidth, float totalHeight, float fade)
        {
            if (canvas == null || current == null) return;

            canvas.Save();
            canvas.ClipRoundRect(roundRect, SKClipOperation.Intersect, true);

            void Draw(SKImage img, float alpha)
            {
                if (img == null) return;
                if (img.Width <= 0 || img.Height <= 0) return; // Prevent zero-dimension crash

                try
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = Settings.AntiAliasing,
                        FilterQuality = SKFilterQuality.High,
                        Color = SKColors.White.WithAlpha((byte)(alpha * 255)),
                        ImageFilter = SKImageFilter.CreateBlur(ThumbnailBlurAmount, ThumbnailBlurAmount)
                    };

                    float scale = Math.Max(totalWidth / img.Width, totalHeight / img.Height);
                    float iw = img.Width * scale;
                    float ih = img.Height * scale;

                    float ix = Position.X + (totalWidth - iw) / 2f;
                    float iy = Position.Y + (totalHeight - ih) / 2f;

                    canvas.DrawImage(img, SKRect.Create(ix, iy, iw, ih), paint);
                }
                catch
                {
                    // Silently ignore any Skia native errors (disposed image as example)
                }
            }

            if (previous != null && previous.Width > 0 && previous.Height > 0 && fade < 1f)
                Draw(previous, 1f - fade);

            Draw(current, fade);

            canvas.Restore();
        }

        public Col GetActionCol()
        {
            return Col.Lerp(Secondary, Primary, averageAmplitude * 2);
        }

        public Col GetInverseActionCol()
        {
            return Col.Lerp(Primary, Secondary, averageAmplitude * 2);
        }

        public void ResetVisuals()
        {
            for (int i = 0; i < barCount; i++)
            {
                barHeight[i] = 0f;
                barGain[i] = 1f;
                bandNoiseEstimate[i] = 1e-9f;
                bandPeakEstimate[i] = 1e-7f;
            }

            Primary = Theme.Primary;
            Secondary = Theme.Secondary.Override(a: 0.5f);

            lock (thumbLock)
            {
                cachedThumbnailImage?.Dispose();
                cachedThumbnailImage = null;
                cachedThumbnailBytes = null;
                pendingThumbnailBytes = null;
                previousThumbnailImage?.Dispose();
                previousThumbnailImage = null;
            }
        }

        public void SetBarGain(float[] gains)
        {
            if (gains == null) return;
            int len = Math.Min(gains.Length, barCount);
            for (int i = 0; i < len; i++) barGain[i] = gains[i];
        }

        public void SetBarGain(int index, float value)
        {
            if (index < 0 || index >= barCount) return;
            lock (fftLock)
            {
                barGain[index] = value;
            }
        }

        public float GetBarGain(int index)
        {
            if (index < 0 || index >= barCount) return 1f;
            lock (fftLock)
            {
                return barGain[index];
            }
        }
    }
}
