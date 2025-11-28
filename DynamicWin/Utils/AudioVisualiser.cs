using DynamicWin.Main;
using DynamicWin.UI;
using NAudio.Wave;
using SkiaSharp;
using System.Numerics;
using System.Linq;

/*
 * 
 *   Overview:
 *    - Implement a new audio visualiser that aims to look closely similar to iOS audio visualisers.
 *    
 *   Author:                 59xa
 *   GitHub:                 https://github.com/59xa
 *   Implementation Date:    18 May 2025
 *   Last Modified:          27 November 2025
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

        private float runningPeak = 0f;
        private float peakDecay = 1.5f;

        // Band balance multipliers to keep spectrum visually balanced (static, no historical normalisation)
        private float[] bandBalance = new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 0.9f, 1.1f };

        // Hardcoded frequency ranges per bar (Hz)
        private readonly float[][] freqRanges = new float[][]
        {
            new float[]{ 0f,   60f   },  // Bar 1 – deep bass
            new float[]{ 60f,   250f  },  // Bar 2 – bass body (kick / low tom)
            new float[]{ 250f,  500f  },  // Bar 3 – low mids (snare fundamental)
            new float[]{ 500f,  2000f },  // Bar 4 – mids (vocals/body)
            new float[]{ 2000f, 6000f },  // Bar 5 – upper mids (presence)
            new float[]{ 6000f, 20000f }   // Bar 6 – brilliance/air/hi-hats
        };

        private WasapiLoopbackCapture capture;
        private readonly object fftLock = new object();

        // Values to ensure smoothness and reactiveness on the visualiser
        // Use attack/release rates (higher = quicker). These are used as rate constants in the
        // exponential smoothing function: alpha = 1 - exp(-rate * deltaTime)
        // Attack low for smooth rise, release high for quick decay
        public float attackRate = 20f;   // How quickly bars rise (lower = smoother)
        public float releaseRate = 100f;  // How quickly bars fall (higher = quicker decay)
        // Cap on change per second to avoid frame-to-frame jitter while remaining reactive
        public float maxChangePerSecond = 12f;
        private float maxDecay = 0.90f;

        // Noise gating parameters (per-band slow noise estimate + gate multiplier)
        private float[] bandNoiseEstimate;
        // Use separate rise/fall rates for the noise-floor estimate to avoid the estimate getting stuck high
        public float noiseEstimateRiseRate = 0.6f; // How fast noise floor increases when signal rises
        public float noiseEstimateFallRate = 3.0f; // How fast noise floor falls when signal drops (faster decay)
        public float gateMultiplier = 1.15f;    // Threshold = noiseEstimate * gateMultiplier
        public float minGateThreshold = 1e-5f;  // Absolute minimum gate (lowered)

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
            for (int i = 0; i < barCount; i++)
            {
                barHeight[i] = 0f;
                barGain[i] = 1f;   // default: unity gain
                bandNoiseEstimate[i] = 1e-6f; // start with very small floor to avoid permanent gating
            }

            // Capture default device audio
            if (DynamicWinMain.defaultDevice != null)
            {
                capture = new WasapiLoopbackCapture(DynamicWinMain.defaultDevice);
                capture.DataAvailable += OnDataAvailable;
                capture.StartRecording();
            }
        }

        /// <summary>
        /// Destroys previously gathered frequency data from Windows Audio Services API
        /// </summary>
        public override void OnDestroy()
        {
            base.OnDestroy();
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
        }

        /// <summary>
        /// Override method to calculate visualiser values to display
        /// </summary>
        /// <param name="deltaTime">Value to display frames per second</param>
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Locks the object to ensure thread safety
            lock (fftLock)
            {
                // Initialise min-to-max frequencies to display
                float[] targetHeights = new float[barCount];
                float sampleRate = capture?.WaveFormat.SampleRate ?? 44100f; // Capture sample rate, else, default to 44100

                // For-loop to visualise each frequency per bar using hardcoded ranges
                for (int i = 0; i < barCount; i++)
                {
                    float lowFreq = 20f;
                    float highFreq = 5000f;

                    if (i < freqRanges.Length && freqRanges[i] != null && freqRanges[i].Length >= 2)
                    {
                        lowFreq = freqRanges[i][0];
                        highFreq = freqRanges[i][1];
                    }

                    int lowIndex = (int)(lowFreq / sampleRate * fftLength);
                    int highIndex = Math.Min((int)(highFreq / sampleRate * fftLength), fftMagnitudes.Length - 1);

                    // Values to calculate average magnitude within frequency range
                    float avg = 0f;
                    int count = 0;
                    for (int j = lowIndex; j <= highIndex; j++)
                    {
                        if (j >= 0 && j < fftMagnitudes.Length)
                        {
                            avg += fftMagnitudes[j];
                            count++;
                        }
                    }
                    avg = (count > 0) ? avg / count : 0f;

                    // Noise gate: maintain a per-band noise-floor estimate and apply gate
                    // Update noise-floor estimate in linear magnitude domain (more stable)
                    float riseAlpha = 1f - MathF.Exp(-noiseEstimateRiseRate * deltaTime);
                    float fallAlpha = 1f - MathF.Exp(-noiseEstimateFallRate * deltaTime);
                    if (avg > bandNoiseEstimate[i])
                    {
                        bandNoiseEstimate[i] = bandNoiseEstimate[i] + (avg - bandNoiseEstimate[i]) * riseAlpha;
                    }
                    else
                    {
                        bandNoiseEstimate[i] = bandNoiseEstimate[i] + (avg - bandNoiseEstimate[i]) * fallAlpha;
                    }

                    float gateThreshold = Math.Max(minGateThreshold, bandNoiseEstimate[i] * gateMultiplier);

                    float normalized = 0f;
                    if (avg <= gateThreshold)
                    {
                        // Below threshold: consider silent
                        normalized = 0f;
                    }
                    else
                    {
                        // Convert linear amplitude to dB (dBFS-like). Add eps to avoid log(0).
                        const float eps2 = 1e-9f;
                        float mag = Math.Max(avg, eps2);
                        float db = 20f * MathF.Log10(mag);

                        // Map db -> normalized 0..1 using fixed floor/ceiling
                        const float minDb = -80f;   // Silence floor
                        const float maxDb = -6f;    // Loud but not clipping ceiling
                        normalized = (db - minDb) / (maxDb - minDb);
                        normalized = Math.Clamp(normalized, 0f, 1f);

                        // Softly re-normalise the remaining dynamic range above the gate using linear domain
                        // This keeps the perceptual mapping but avoids hard cutoff artifacts
                        float above = (avg - gateThreshold) / (Math.Max(gateThreshold, 1e-6f));
                        above = MathF.Min(above, 1f);
                        normalized = Math.Max(normalized, above);
                    }

                    // Optional running peak normaliser to avoid sudden all-bars full at sustained loudness
                    // Update runningPeak (simple max with decay)
                    runningPeak = Math.Max(runningPeak, normalized);
                    float peakDecayThisFrame = 1f - MathF.Exp(-peakDecay * deltaTime); // Decay alpha
                    runningPeak = runningPeak * (1f - peakDecayThisFrame); // Decay towards 0

                    // Use peak to compress the dynamic: divide by (peak * factor + epsilon) to avoid saturation
                    float peakFactor = Math.Max(runningPeak, 0.0001f);
                    float compressed = normalized / (0.9f * peakFactor + 0.1f); // Blend so not too aggressive
                    compressed = Math.Clamp(compressed, 0f, 1f);

                    // Now apply per-band compensation/gain/balance
                    float balance = (i < bandBalance.Length) ? bandBalance[i] : 1f;
                    float gain = (barGain != null && i < barGain.Length) ? barGain[i] : 1f;

                    // Small frequency compensation (reverse sign from your old formula so highs are not overly boosted)
                    float compensation = 1.0f - i * 0.05f; // gentle tilt; tweak to taste

                    float finalValue = compressed * gain * balance * compensation;

                    // Tiny gate to prevent flicker
                    if (finalValue < 0.00001f) finalValue = 0f;

                    targetHeights[i] = Math.Clamp(finalValue, 0f, 1f);
                }

                // Compute average amplitude (simple average of targets)
                averageAmplitude = targetHeights.Average();

                // Ensure smoothness when adjusting bar height according to values
                for (int i = 0; i < barCount; i++)
                {
                    // Exponential attack/release smoothing
                    float current = barHeight[i];
                    float target = targetHeights[i];

                    // Determine alpha using attack/release rates
                    float rate = (target > current) ? attackRate : releaseRate;
                    float alpha = 1f - (float)Math.Exp(-rate * deltaTime);

                    // Apply smoothing
                    float newValue = current + (target - current) * alpha;

                    // Rate limit change per frame to avoid jitter while keeping responsiveness
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
        /// <param name="sender">Value of where the event is coming from</param>
        /// <param name="e">Value provided by the event</param>
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            // Convert byte array to float array
            var buffer = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

            var samples = new Complex[fftLength];

            // For-loop to fill array with audio data
            for (int i = 0; i < fftLength && i < buffer.Length; i++)
            {
                float window = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (fftLength - 1)));
                samples[i] = new Complex(buffer[i] * window, 0);
            }

            // Convert time-domain audio to frequency-domain
            FFT(samples);

            float magnitudeScale = 2.0f / fftLength;
            const float eps = 1e-9f;

            // Ensure thread-safety
            lock (fftLock)
            {
                // Only store first half (DC..Nyquist)
                int len = fftMagnitudes.Length;
                for (int i = 0; i < len; i++)
                {
                    // Magnitude of complex bin scaled to amplitude
                    float mag = (float)samples[i].Magnitude * magnitudeScale;

                    // Optional tiny smoothing to avoid exact zeros
                    if (mag < eps) mag = 0f;

                    fftMagnitudes[i] = mag;
                }
            }
        }

        /// <summary>
        /// Performs Fast Fourier Transform on each sample
        /// </summary>
        /// <param name="buffer">Buffer value</param>
        private void FFT(Complex[] buffer)
        {
            int n = buffer.Length;
            int m = (int)Math.Log2(n);

            // Bit-reversal permutation stage
            for (int i = 0; i < n; i++)
            {
                int j = BitReverse(i, m);
                if (j > i)
                {
                    // Swap elements to put in bit-reversed order
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
                }
            }

            // Cooley-Turkey algorithm computation
            for (int s = 1; s <= m; s++)
            {
                int mval = 1 << s;
                int mval2 = mval >> 1;

                // Twiddle factor implementation
                Complex wm = Complex.FromPolarCoordinates(1, -2 * Math.PI / mval);

                for (int k = 0; k < n; k += mval)
                {
                    Complex w = Complex.One;
                    for (int j = 0; j < mval2; j++)
                    {
                        // Handle twiddle multiplication
                        Complex t = w * buffer[k + j + mval2];
                        Complex u = buffer[k + j];

                        // Compute the new values
                        buffer[k + j] = u + t;
                        buffer[k + j + mval2] = u - t;
                        w *= wm; // Update before next iteration
                    }
                }
            }
        }

        /// <summary>
        /// Helper function to reverse bits of an integer
        /// </summary>
        /// <param name="n">The value to reverse</param>
        /// <param name="bits">The amount of bits needed to reverse the value</param>
        /// <returns></returns>
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
            if (capture == null) return; // Do not draw anything if there is no audio to capture

            // Values to fetch dimensions and position
            float width = Size.X;
            float height = Size.Y;
            float centerY = Position.Y + height / 2;

            // Calculate bar spacing and dimensions
            float spacing = BarSpacing;
            float totalSpacing = spacing * (barCount - 1);
            float barWidth = (width - totalSpacing) / barCount;
            float visualBoost = 1.5f;
            float dotHeight = barWidth;

            // Draw each frequency bar
            for (int i = 0; i < barCount; i++)
            {
                // Determine bar height
                float rawHeight = barHeight[i] * visualBoost;
                bool isDot = enableDotWhenLow && rawHeight < 0.05f;
                float bH = isDot ? dotHeight : rawHeight * height * 0.8f;

                // Calculate placement
                float x = Position.X + i * (barWidth + spacing);
                float barTopY = centerY - bH / 2;

                // Define shape per bar
                var rect = SKRect.Create(x, barTopY, barWidth, bH);
                var roundRect = new SKRoundRect(rect, barWidth / 2, barWidth / 2);

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
                using var paint = new SKPaint
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
                {
                    paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurAmount);
                }

                canvas.DrawRoundRect(roundRect, paint);
            }
        }

        /// <summary>
        /// Returns a colour between Secondary and Primary depending on audio amplitude
        /// </summary>
        /// <returns>Either Secondary or Primary colour schemes</returns>
        public Col GetActionCol()
        {
            return Col.Lerp(Secondary, Primary, averageAmplitude * 2);
        }

        /// <summary>
        /// Opposite of GetActionCol()
        /// </summary>
        /// <returns>Either Secondary or Primary colour schemes but inversed</returns>
        public Col GetInverseActionCol()
        {
            return Col.Lerp(Primary, Secondary, averageAmplitude * 2);
        }

        /// <summary>
        /// Linear interpolation method
        /// </summary>
        /// <param name="a">First value</param>
        /// <param name="b">Second value</param>
        /// <param name="t">Third value</param>
        /// <returns>A float value that smoothly blends between the given values</returns>
        private float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        public void ResetVisuals()
        {
            barHeight = new float[barCount];
            // Reset gain to defaults
            barGain = new float[barCount];
            bandNoiseEstimate = new float[barCount];
            for (int i = 0; i < barCount; i++)
            {
                barGain[i] = 1f;
                bandNoiseEstimate[i] = 1e-6f;
            }
            Primary = Theme.Primary;
            Secondary = Theme.Secondary.Override(a: 0.5f);
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