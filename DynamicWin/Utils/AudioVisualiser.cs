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
 *   Last Modified:          04 January 2026
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

        // Per-bar gain (sensitivity)
        private float[] barGain;

        // Band balance multipliers to keep spectrum visually balanced (static, no historical normalisation)
        private float[] bandBalance = new float[] { 1f, 1.0f, 1.15f, 1.10f, 1.25f, 1.35f };

        // Hardcoded frequency ranges per bar (Hz)
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

        // Re-use buffers and precomputed indices for performance
        private Complex[] fftBuffer;
        private float[] window;
        private int[] bitRevIndices;

        // Values to ensure smoothness and reactiveness on the visualiser
        public float attackRate = 60f;   // Quicker rise for livelier feel
        public float releaseRate = 20f;  // Moderate decay
        public float maxChangePerSecond = 18f;

        // Noise gating parameters (per-band slow noise estimate + gate multiplier)
        private float[] bandNoiseEstimate;
        // Slower rise, faster fall so the noise estimate doesn't grow and kill bands
        public float noiseEstimateRiseRate = 0.4f; // Slower rise
        public float noiseEstimateFallRate = 6.0f; // Faster fall
        public float gateMultiplier = 1.35f;    // Slightly above noise floor
        public float minGateThreshold = 1e-8f;  // Very small minimum gate

        // Per-band adaptive peak normaliser (kept for dynamic scale fallback)
        private float[] bandPeakEstimate;
        public float peakRiseRate = 12f;  // Moderate rise to capture peaks
        public float peakFallRate = 6.0f; // Faster fall so peaks don't hold too long

        // Output boost to make visuals pop
        public float outputBoost = 1.0f;

        // Thumbnail background caching
        private volatile byte[]? cachedThumbnailBytes;
        private SKImage? cachedThumbnailImage;
        // Thumbnail crossfade
        private SKImage? previousThumbnailImage;
        private float thumbnailFade = 3f; // 1 = fully current
        public float ThumbnailFadeDuration { get; set; } = 0.35f; // Seconds
        private DateTime lastThumbnailRequest = DateTime.MinValue;
        private readonly object thumbLock = new object();
        public bool UseThumbnailBackground { get; set; } = false;
        // How often to request a new thumbnail (seconds)
        public float ThumbnailFetchInterval { get; set; } = 1.0f;
        // Amount of blur to apply to thumbnail background
        public float ThumbnailBlurAmount { get; set; } = 5f;

        public Col Primary;
        public Col Secondary;

        // Initialise getters and setters
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

            // Use primary and secondary colour palettes
            this.Primary = Primary ?? Theme.Primary;
            this.Secondary = Secondary ?? Theme.Secondary.Override(a: 0.5f);

            // Define visualiser look
            fftMagnitudes = new float[fftLength / 2];
            barHeight = new float[barCount];

            // Initialize per-bar gain
            barGain = new float[barCount];
            bandNoiseEstimate = new float[barCount];
            bandPeakEstimate = new float[barCount];

            // Pre-allocate reusable buffers
            fftBuffer = new Complex[fftLength];
            window = new float[fftLength];
            bitRevIndices = new int[fftLength];

            // Pre-compute Hann window and bit-reversal indices
            for (int i = 0; i < fftLength; i++)
            {
                window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (fftLength - 1)));
                bitRevIndices[i] = BitReverse(i, (int)Math.Log2(fftLength));
            }

            for (int i = 0; i < barCount; i++)
            {
                barHeight[i] = 0f;
                barGain[i] = 1f;   // default: unity gain
                bandNoiseEstimate[i] = 1e-9f; // start with very small floor to avoid permanent gating

                const float absoluteNoiseFloor = 2e-5f;
                bandNoiseEstimate[i] = Math.Max(bandNoiseEstimate[i], absoluteNoiseFloor);

                bandPeakEstimate[i] = 1e-7f;  // small initial peak to avoid division by zero
            }

            // Capture default device audio
            if (DynamicWinMain.defaultDevice != null)
            {
                capture = new WasapiLoopbackCapture(DynamicWinMain.defaultDevice);
                capture.DataAvailable += OnDataAvailable;
                capture.StartRecording();
            }

            // Subscribe to central thumbnail service if using thumbnail background
            MediaThumbnailService.Instance.Subscribe(OnThumbnailChanged);
            MediaThumbnailService.Instance.ThumbnailChanged += OnThumbnailChangedEvent;
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
                    cachedThumbnailImage = null; // Will be recreated lazily
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
            }
        }

        /// <summary>
        /// Override method to calculate visualiser values to display
        /// </summary>
        /// <param name="deltaTime">Value to display frames per second</param>
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (UseThumbnailBackground && thumbnailFade < 1f)
            {
                thumbnailFade += deltaTime / Math.Max(0.0001f, ThumbnailFadeDuration);
                if (thumbnailFade >= 1f)
                {
                    thumbnailFade = 1f;

                    // Fade finished -> old image no longer needed
                    lock (thumbLock)
                    {
                        previousThumbnailImage?.Dispose();
                        previousThumbnailImage = null;
                    }
                }
            }

            // If our thumbnail background option is enabled, request thumbnail periodically (async)
            if (UseThumbnailBackground)
            {
                var now = DateTime.UtcNow;
                if ((now - lastThumbnailRequest).TotalSeconds >= ThumbnailFetchInterval)
                {
                    lastThumbnailRequest = now;
                    // Fire-and-forget async fetch; MediaInfo caches results internally
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var media = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);
                            if (media?.ThumbnailData != null && media.ThumbnailData.Length > 0)
                            {
                                lock (thumbLock)
                                {
                                    // Simple change detection by reference/length
                                    if (cachedThumbnailBytes == null || cachedThumbnailBytes.Length != media.ThumbnailData.Length || !cachedThumbnailBytes.SequenceEqual(media.ThumbnailData))
                                    {
                                        cachedThumbnailBytes = media.ThumbnailData;
                                        // Dispose old image on UI thread later when drawing
                                        cachedThumbnailImage?.Dispose();
                                        cachedThumbnailImage = null;
                                    }
                                }
                            }
                            else
                            {
                                // If there's really no media, clear cached bytes/image
                                if (media == null)
                                {
                                    lock (thumbLock)
                                    {
                                        cachedThumbnailBytes = null;
                                        cachedThumbnailImage?.Dispose();
                                        cachedThumbnailImage = null;
                                    }
                                }
                            }
                        }
                        catch { /* ignore fetch errors */ }
                    });
                }
            }

            // Locks the object to ensure thread safety for FFT-derived arrays
            lock (fftLock)
            {
                float[] targetHeights = new float[barCount];
                float sampleRate = capture?.WaveFormat.SampleRate ?? 44100f;

                for (int i = 0; i < barCount; i++)
                {
                    float lowFreq = 20f;
                    float highFreq = 5000f;

                    if (i < freqRanges.Length && freqRanges[i] != null && freqRanges[i].Length >= 2)
                    {
                        lowFreq = freqRanges[i][0];
                        highFreq = freqRanges[i][1];
                    }

                    int lowIndex = Math.Max(0, (int)(lowFreq / sampleRate * fftLength));
                    int highIndex = Math.Min((int)(highFreq / sampleRate * fftLength), fftMagnitudes.Length - 1);

                    // Compute RMS (power) over the band for better perceptual response
                    float sumSquares = 0f;
                    int count = 0;
                    for (int j = lowIndex; j <= highIndex; j++)
                    {
                        if (j >= 0 && j < fftMagnitudes.Length)
                        {
                            float m = fftMagnitudes[j];
                            sumSquares += m * m;
                            count++;
                        }
                    }

                    float rms = (count > 0) ? MathF.Sqrt(sumSquares / count) : 0f;

                    // Update noise-floor estimate (linear domain)
                    float riseAlpha = 1f - MathF.Exp(-noiseEstimateRiseRate * deltaTime);
                    float fallAlpha = 1f - MathF.Exp(-noiseEstimateFallRate * deltaTime);
                    float maxNoiseFraction = 0.35f; // never treat more than 35% of signal as noise
                    float noiseCeiling = rms * maxNoiseFraction;

                    if (rms > bandNoiseEstimate[i])
                    {
                        float target = MathF.Min(rms, noiseCeiling);
                        bandNoiseEstimate[i] += (target - bandNoiseEstimate[i]) * riseAlpha;
                    }
                    else
                    {
                        bandNoiseEstimate[i] += (rms - bandNoiseEstimate[i]) * fallAlpha;
                    }


                    float localGateMultiplier = gateMultiplier;

                    // Ease the gate for low frequencies
                    if (i == 0)          // sub
                        localGateMultiplier = 1.15f;
                    else if (i == 1)     // low bass
                        localGateMultiplier = 1.3f;

                    float gateThreshold = Math.Max(
                        minGateThreshold,
                        bandNoiseEstimate[i] * localGateMultiplier
                    );

                    // Subtract noise-floor
                    float val = Math.Max(0f, rms - gateThreshold);

                    // Update per-band peak estimate (fallback scale)
                    float peakRiseA = 1f - MathF.Exp(-peakRiseRate * deltaTime);
                    float peakFallA = 1f - MathF.Exp(-peakFallRate * deltaTime);
                    if (val > bandPeakEstimate[i])
                        bandPeakEstimate[i] += (val - bandPeakEstimate[i]) * peakRiseA;
                    else
                    {
                        // Faster multiplicative decay to avoid long-held large peaks
                        bandPeakEstimate[i] *= MathF.Exp(-peakFallRate * deltaTime);
                        // ensure it never goes below a tiny floor
                        if (bandPeakEstimate[i] < 1e-9f) bandPeakEstimate[i] = 1e-9f;
                    }

                    // Perceptual mapping: dB mapping plus dynamic-scale fallback to preserve band differences
                    const float eps2 = 1e-12f;
                    float gain = (barGain != null && i < barGain.Length) ? barGain[i] : 1f;
                    float balance = (i < bandBalance.Length) ? bandBalance[i] : 1f;
                    float valLin = val * gain * balance * outputBoost;

                    if (valLin < 1e-5f)
                    {
                        targetHeights[i] = 0f;
                        continue;
                    }

                    float db = 20f * MathF.Log10(Math.Max(valLin, eps2));

                    const float minDb = -64f;
                    const float maxDb = -6f;
                    float normalizedDb = (db - minDb) / (maxDb - minDb);
                    normalizedDb = Math.Clamp(normalizedDb, 0f, 1f);

                    // Dynamic fallback: val relative to recent peak
                    float dynamicScale = val / Math.Max(bandPeakEstimate[i], eps2);
                    dynamicScale = Math.Clamp(dynamicScale, 0f, 1f);

                    // Combine: prefer dB mapping but allow dynamicScale to boost bands that are underrepresented
                    float normalized = MathF.Max(normalizedDb, dynamicScale);

                    // Gentle gamma to make small signals more visible
                    normalized = MathF.Pow(normalized, 0.75f);

                    // Slight compensation for lower bars so highs don't dominate visually
                    float compensation = 1.0f - i * 0.05f;
                    float finalValue = normalized * compensation;

                    if (finalValue < 0.00001f) finalValue = 0f;

                    targetHeights[i] = Math.Clamp(finalValue, 0f, 1f);
                }

                // Compute average amplitude (simple average of targets)
                averageAmplitude = targetHeights.Average();

                if (averageAmplitude < 0.015f)
                {
                    for (int i = 0; i < barCount; i++)
                        targetHeights[i] = 0f;
                }

                // Ensure smoothness when adjusting bar height according to values
                for (int i = 0; i < barCount; i++)
                {
                    float current = barHeight[i];
                    float target = targetHeights[i];

                    float rate = (target > current) ? attackRate : releaseRate;
                    float alpha = 1f - (float)Math.Exp(-rate * deltaTime);

                    float newValue = current + (target - current) * alpha;

                    float maxStep = maxChangePerSecond * deltaTime;
                    float delta = newValue - current;
                    if (delta > maxStep) delta = maxStep;
                    if (delta < -maxStep) delta = -maxStep;

                    barHeight[i] = current + delta;
                }
            }
        }

        /// <summary>
        /// Handler to check if any data can be fetched from device
        /// </summary>
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int samplesAvailable = e.BytesRecorded / 4;
            int copyLen = Math.Min(samplesAvailable, fftLength);

            // Fill fftBuffer with windowed samples, reuse preallocated Complex buffer
            for (int i = 0; i < fftLength; i++)
            {
                float s = 0f;
                if (i < copyLen)
                    s = BitConverter.ToSingle(e.Buffer, i * 4);

                fftBuffer[i] = new Complex(s * window[i], 0);
            }

            // In-place FFT
            FFT(fftBuffer);

            float magnitudeScale = 2.0f / fftLength;
            const float eps = 1e-12f;

            lock (fftLock)
            {
                int len = fftMagnitudes.Length;
                for (int i = 0; i < len; i++)
                {
                    float mag = (float)fftBuffer[i].Magnitude * magnitudeScale;
                    if (mag < eps) mag = 0f;
                    fftMagnitudes[i] = mag;
                }
            }
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
                if (j > i)
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
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

            if (UseThumbnailBackground)
            {
                thumbnailImage = TryGetFreshThumbnailImage();
            }

            float width = Size.X;
            float height = Size.Y;
            float centerY = Position.Y + height / 2;

            // Prepare image to use for thumbnail background. If we create a temporary SKImage it must be disposed;
            // do NOT dispose cachedThumbnailImage because it's owned by this object.
            SKImage? imgToUse = null;
            bool createdTempImage = false;

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
                        SKImage? prev;
                        lock (thumbLock)
                        {
                            prev = previousThumbnailImage;
                        }

                        DrawThumbnailBar(canvas, roundRect, thumbnailImage, prev, width, height, thumbnailFade);

                        using var overlay = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, 40)
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

            // Dispose temporary image created from service BMP only
            try
            {
                thumbnailImage?.Dispose();
            }
            catch { }
        }

        private SKImage? TryGetFreshThumbnailImage()
        {
            // Try service bitmap that's already decoded
            try
            {
                var bmp = MediaThumbnailService.Instance.GetCurrentThumbnailBitmap();
                if (bmp != null)
                {
                    return SKImage.FromBitmap(bmp); // Local ownership
                }
            }
            catch { }

            // Fallback to cached bytes
            try
            {
                lock (thumbLock)
                {
                    if (cachedThumbnailBytes == null || cachedThumbnailBytes.Length == 0)
                        return null;

                    using var ms = new SKMemoryStream(cachedThumbnailBytes);
                    using var codec = SKCodec.Create(ms);

                    if (codec != null)
                    {
                        using var bitmap = SKBitmap.Decode(codec);
                        return bitmap != null ? SKImage.FromBitmap(bitmap) : null;
                    }

                    using var bmp = SKBitmap.Decode(cachedThumbnailBytes);
                    return bmp != null ? SKImage.FromBitmap(bmp) : null;
                }
            }
            catch { }

            return null;
        }

        private void DrawThumbnailBar(SKCanvas canvas, SKRoundRect roundRect, SKImage current, SKImage? previous, float totalWidth, float totalHeight, float fade
        )
        {
            // At this point img is guaranteed fresh & owned by caller
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
            barHeight = new float[barCount];
            // Reset gain to defaults
            barGain = new float[barCount];
            bandNoiseEstimate = new float[barCount];
            bandPeakEstimate = new float[barCount];
            for (int i = 0; i < barCount; i++)
            {
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