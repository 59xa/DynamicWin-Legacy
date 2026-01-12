using DynamicWin.Main;
using DynamicWin.UI;
using NAudio.Wave;
using SkiaSharp;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Diagnostics;

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

        private float[] bandBalance = new float[] { 1f, 0.75f, 1.15f, 1.10f, 1.25f, 1.35f };

        private readonly float[][] freqRanges = new float[][]
        {
            new float[]{ 20f,   120f },
            new float[]{ 120f,  400f },
            new float[]{ 400f,  1200f },
            new float[]{ 1200f, 5000f },
            new float[]{ 5000f, 12000f },
            new float[]{ 12000f, 20000f }
        };

        private WasapiLoopbackCapture capture;
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

        private volatile byte[]? cachedThumbnailBytes;
        private SKImage? cachedThumbnailImage; // Keep decoded image cached to avoid per-frame decode/encode
        private SKImage? previousThumbnailImage;
        private float thumbnailFade = 3f;
        public float ThumbnailFadeDuration { get; set; } = 0.35f;
        private readonly object thumbLock = new object();
        public bool UseThumbnailBackground { get; set; } = false;
        public float ThumbnailFetchInterval { get; set; } = 1.0f;
        public float ThumbnailBlurAmount { get; set; } = 5f;

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
        public float BarSpacing { get; set; } = 1f;

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

            if (DynamicWinMain.defaultDevice != null)
            {
                capture = new WasapiLoopbackCapture(DynamicWinMain.defaultDevice);
                capture.DataAvailable += OnDataAvailable;
                capture.StartRecording();
            }

            // Precompute FFT bin mapping
            InitBarBinMapping(capture?.WaveFormat.SampleRate ?? 44100f);

            // Subscribe to central thumbnail service if using thumbnail background
            MediaThumbnailService.Instance.Subscribe(OnThumbnailChanged);
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChangedEvent;

            // Prime thumbnail cache from central service instead of fetching directly
            try
            {
                var bmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (bmp != null)
                {
                    lock (thumbLock)
                    {
                        cachedThumbnailImage?.Dispose();
                        // Keep an owned copy of the service bitmap for later cloning in Draw
                        cachedThumbnailImage = SKImage.FromBitmap(bmp);
                        cachedThumbnailBytes = null;
                        thumbnailFade = 0f;
                    }
                }
                else
                {
                    // Try to get cached bytes from the central service
                    try
                    {
                        var bytes = MediaThumbnailService.Instance.GetCurrentThumbnailBytes();
                        if (bytes != null && bytes.Length > 0)
                        {
                            lock (thumbLock)
                            {
                                cachedThumbnailBytes = (byte[])bytes.Clone();
                                cachedThumbnailImage?.Dispose();
                                cachedThumbnailImage = null;
                                thumbnailFade = 0f;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitBarBinMapping(float sampleRate)
        {
            barBinIndices = new int[barCount][];
            barBinCounts = new int[barCount];
            for (int i = 0; i < barCount; i++)
            {
                int lowIndex = Math.Max(0, (int)(freqRanges[i][0] / sampleRate * fftLength));
                int highIndex = Math.Min((int)(freqRanges[i][1] / sampleRate * fftLength), fftMagnitudes.Length - 1);
                int len = Math.Max(0, highIndex - lowIndex + 1);
                var arr = new int[len];
                for (int j = 0; j < len; j++) arr[j] = lowIndex + j;
                barBinIndices[i] = arr;
                barBinCounts[i] = len;
            }
        }

        private void OnThumbnailChanged(Media? m)
        {
            try
            {
                lock (thumbLock)
                {
                    // Move current → previous
                    if (cachedThumbnailImage != null)
                    {
                        previousThumbnailImage?.Dispose();
                        previousThumbnailImage = cachedThumbnailImage;
                    }

                    cachedThumbnailBytes = m?.ThumbnailData;
                    cachedThumbnailImage = null; // Will be recreated lazily on next Draw
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
                        cachedThumbnailBytes = null;
                        cachedThumbnailImage?.Dispose();
                        cachedThumbnailImage = null;
                        return;
                    }

                    cachedThumbnailBytes = e.ThumbnailBytes;
                    // Dispose existing image - will be recreated on UI thread in Draw
                    cachedThumbnailImage?.Dispose();
                    cachedThumbnailImage = null;

                    // If service provides decoded bitmap, we can use it; otherwise we'll decode bytes on UI thread
                    var bmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                    if (bmp != null)
                    {
                        try
                        {
                            // Create SKImage from bitmap clone to own it safely
                            var img = SKImage.FromBitmap(bmp);
                            cachedThumbnailImage = img;
                        }
                        catch { cachedThumbnailImage = null; }
                    }
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
            try { MediaThumbnailService.Instance.Unsubscribe(OnThumbnailChanged); } catch { }
            try { MediaThumbnailService.Instance.ThumbnailChanged -= OnThumbnailChangedEvent; } catch { }

            try
            {
                if (capture != null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.StopRecording();
                    capture.Dispose();
                }
            }
            catch (ThreadInterruptedException) { }

            // Dispose cached thumbnail image
            lock (thumbLock)
            {
                cachedThumbnailImage?.Dispose();
                cachedThumbnailImage = null;
                cachedThumbnailBytes = null;
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
                        previousThumbnailImage?.Dispose();
                        previousThumbnailImage = null;
                    }
                }
            }

            // Re-use targetHeights array to avoid per-frame allocations
            for (int t = 0; t < barCount; t++) targetHeights[t] = 0f;

            lock (fftLock)
            {
                for (int i = 0; i < barCount; i++)
                {
                    float rms = MathF.Sqrt(barSumSquares[i] / Math.Max(barBinCounts[i], 1));
                    float riseAlpha = 1f - MathF.Exp(-noiseEstimateRiseRate * deltaTime);
                    float fallAlpha = 1f - MathF.Exp(-noiseEstimateFallRate * deltaTime);
                    float maxNoiseFraction = 0.35f;
                    float noiseCeiling = rms * maxNoiseFraction;

                    if (rms > bandNoiseEstimate[i])
                        bandNoiseEstimate[i] += (MathF.Min(rms, noiseCeiling) - bandNoiseEstimate[i]) * riseAlpha;
                    else
                        bandNoiseEstimate[i] += (rms - bandNoiseEstimate[i]) * fallAlpha;

                    float localGateMultiplier = i switch { 0 => 1.15f, 1 => 1.3f, _ => gateMultiplier };
                    float gateThreshold = Math.Max(minGateThreshold, bandNoiseEstimate[i] * localGateMultiplier);
                    float val = Math.Max(0f, rms - gateThreshold);

                    float peakRiseA = 1f - MathF.Exp(-peakRiseRate * deltaTime);
                    float peakFallA = 1f - MathF.Exp(-peakFallRate * deltaTime);
                    if (val > bandPeakEstimate[i]) bandPeakEstimate[i] += (val - bandPeakEstimate[i]) * peakRiseA;
                    else bandPeakEstimate[i] *= MathF.Exp(-peakFallRate * deltaTime);

                    float valLin = val * barGain[i] * bandBalance[i] * outputBoost;
                    if (valLin < 1e-5f) { targetHeights[i] = 0f; continue; }

                    float db = 20f * MathF.Log10(Math.Max(valLin, 1e-12f));
                    float normalizedDb = Math.Clamp((db + 64f) / 58f, 0f, 1f);

                    float dynamicScale = Math.Clamp(val / Math.Max(bandPeakEstimate[i], 1e-12f), 0f, 1f);
                    float normalized = MathF.Max(normalizedDb, dynamicScale) * bandBalance[i];
                    normalized = MathF.Pow(normalized, 0.75f);
                    normalized *= 1.0f - i * 0.05f;
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
                // Get or create decoded image once per frame (cached)
                lock (thumbLock)
                {
                    if (cachedThumbnailImage != null)
                    {
                        thumbnailImage = cachedThumbnailImage;
                    }
                    else if (cachedThumbnailBytes != null && cachedThumbnailBytes.Length > 0)
                    {
                        try
                        {
                            using var bmp = SKBitmap.Decode(cachedThumbnailBytes);
                            if (bmp != null)
                            {
                                cachedThumbnailImage = SKImage.FromBitmap(bmp);
                                thumbnailImage = cachedThumbnailImage;
                                // Keep cachedThumbnailBytes intact in case service needs it; we created image once
                            }
                        }
                        catch
                        {
                            thumbnailImage = null;
                        }
                    }

                    prevThumb = previousThumbnailImage;
                }
            }

            float width = Size.X;
            float height = Size.Y;
            float centerY = Position.Y + height / 2;

            float spacing2 = BarSpacing;
            float totalSpacing2 = spacing2 * (barCount - 1);
            float barWidth2 = (width - totalSpacing2) / barCount;
            float visualBoost = 1.5f;
            float dotHeight = barWidth2;

            for (int i = 0; i < barCount; i++)
            {
                float rawHeight = barHeight[i] * visualBoost;
                bool isDot = enableDotWhenLow && rawHeight < 0.05f;
                float bH = isDot ? dotHeight : rawHeight * height * 0.8f;

                float x = Position.X + i * (barWidth2 + spacing2);
                float barTopY = centerY - bH / 2;

                var rect = SKRect.Create(x, barTopY, barWidth2, bH);
                var roundRect = new SKRoundRect(rect, barWidth2 / 2, barWidth2 / 2);

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
                    float lerpAmount = isDot ? 0.2f : barHeight[i];
                    Col pCol = EnableColourTransition
                        ? Col.Lerp(Secondary, Primary, lerpAmount)
                        : Primary;

                    SKColor baseColor = GetColor(pCol).Value();

                    // Ensure alpha never goes below a visible threshold
                    byte alpha = (byte)Math.Max(100, (int)baseColor.Alpha);

                    SKColor startColor = baseColor.WithAlpha(alpha);
                    SKColor endColor = new SKColor(
                        (byte)(baseColor.Red * 0.7),
                        (byte)(baseColor.Green * 0.7),
                        (byte)(baseColor.Blue * 0.7),
                        alpha
                    );

                    // Create gradient placement
                    using var paintBar = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = SKShader.CreateLinearGradient(
                            new SKPoint(rect.Left, rect.Bottom),
                            new SKPoint(rect.Left, rect.Top),
                            new[] { startColor, endColor },
                            new float[] { 0, 1 },
                            SKShaderTileMode.Clamp
                        ),
                    };

                    // If blur is active, blur the visualiser
                    if (Settings.AllowBlur)
                        paintBar.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurAmount);

                    canvas.DrawRoundRect(roundRect, paintBar);
                }
            }

            // Do not dispose cached images here - they are owned by this object and will be disposed in OnDestroy or when replaced
        }

        private void DrawThumbnailBar(SKCanvas canvas, SKRoundRect roundRect, SKImage current, SKImage? previous, float totalWidth, float totalHeight, float fade
        )
        {
            // At this point img is guaranteed fresh & owned by this object (do not dispose)
            canvas.Save();
            canvas.ClipRoundRect(roundRect, SKClipOperation.Intersect, true);

            void Draw(SKImage img, float alpha)
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    FilterQuality = SKFilterQuality.High,
                    Color = SKColors.White.WithAlpha((byte)(alpha * 255)),
                    ImageFilter = SKImageFilter.CreateBlur(ThumbnailBlurAmount, ThumbnailBlurAmount)
                };

                float scale = Math.Max(
                    totalWidth / img.Width,
                    totalHeight / img.Height
                );

                float iw = img.Width * scale;
                float ih = img.Height * scale;

                float ix = Position.X + (totalWidth - iw) / 2f;
                float iy = Position.Y + (totalHeight - ih) / 2f;

                canvas.DrawImage(img, SKRect.Create(ix, iy, iw, ih), paint);
            }

            if (previous != null && fade < 1f)
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