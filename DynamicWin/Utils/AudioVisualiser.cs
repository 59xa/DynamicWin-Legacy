using DynamicWin.Main;
using DynamicWin.UI;
using NAudio.Wave;
using SkiaSharp;
using System.Numerics;
using System;
using System.Buffers;

/*
*   Overview:
*    - Implement a new audio visualiser that aims to look closely similar to iOS audio visualisers.
*    
*   Author:                 Megan Park
*   GitHub:                 https://github.com/59xa
*   Implementation Date:    18 May 2025
*   Last Modified:          18 May 2025 18:11 KST (UTC+9)
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
        private float[] barMax;

        // Reuse arrays to avoid per-callback allocations
        private readonly Complex[] samples;
        private readonly float[] tempFloatBuffer;

        // Implement bar bias to replicate the look seen on iPhones
        private readonly float[] barBias = new float[] { 1f, 0.6f, 0.9f, 1f, 1f, 2f };

        private WasapiLoopbackCapture capture;
        private readonly object fftLock = new object();

        // Values to ensure smoothness and reactiveness on the visualiser
        public float barUpSmoothing = 20;
        public float barDownSmoothing = 10f;
        private float maxDecay = 0.90f;

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
            barMax = new float[barCount];
            for (int i = 0; i < barCount; i++) barMax[i] = 0f;

            // Reuse buffers to avoid allocations per audio callback
            samples = new Complex[fftLength];
            tempFloatBuffer = new float[fftLength];

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
                float minFreq = 20f;
                float maxFreq = 5000f;
                float sampleRate = capture?.WaveFormat.SampleRate ?? 44100f; // Capture sample rate, else, default to 44100

                // For-loop to visualise each frequency per bar
                for (int i = 0; i < barCount; i++)
                {
                    // Convert frequencies to FFT binary indices
                    float lowFreq = (float)(minFreq * Math.Pow(maxFreq / minFreq, (i + 0f) / barCount));
                    float highFreq = (float)(minFreq * Math.Pow(maxFreq / minFreq, (i + 1f) / barCount));

                    int lowIndex = (int)(lowFreq / sampleRate * fftLength);
                    int highIndex = Math.Min((int)(highFreq / sampleRate * fftLength), fftMagnitudes.Length - 1);

                    // Values to calculate average magnitude within frequency range
                    float avg = 0f;
                    int count = 0;
                    for (int j = lowIndex; j <= highIndex; j++)
                    {
                        avg += fftMagnitudes[j];
                        count++;
                    }

                    // Noise gating
                    avg = (count > 0) ? avg / count : 0f;
                    if (avg < 0.001f) avg = 0f;

                    // Boost higher frequencies
                    float compensation = 1.5f - i * 0.1f;
                    targetHeights[i] = avg * compensation;
                }

                // Filter out insignificant values
                float minThreshold = 0.005f;

                for (int i = 0; i < barCount; i++)
                {
                    if (targetHeights[i] < minThreshold)
                    {
                        targetHeights[i] = 0f;
                        continue;
                    }

                    // Increase if current value is high
                    if (targetHeights[i] > barMax[i] * 0.7f)
                    {
                        barMax[i] = targetHeights[i];
                    }
                    else // Decay otherwise if not
                    {
                        barMax[i] *= maxDecay;
                        barMax[i] = Math.Max(barMax[i], minThreshold);
                    }

                    // Normalise frequencies and apply bar bias
                    float normalized = targetHeights[i] / barMax[i];
                    targetHeights[i] = Math.Clamp(normalized, 0f, 1f);
                    targetHeights[i] *= barBias[i];
                    targetHeights[i] = Math.Clamp(targetHeights[i], 0f, 1f);
                }

                // Ensure amplitude is average regardless of volume
                averageAmplitude = targetHeights.Average();

                // Ensure smoothness when adjusting bar height according to values
                for (int i = 0; i < barCount; i++)
                {
                    // Prevent flickering
                    float smoothing = targetHeights[i] > barHeight[i]
                        ? barUpSmoothing * (1f + i * 0.1f)
                        : barDownSmoothing * (1f + i * 0.1f);

                    float t = 1 - (float)Math.Exp(-smoothing * deltaTime);
                    barHeight[i] = Lerp(barHeight[i], targetHeights[i], t);
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
            // Avoid reallocating buffers each time: copy into pre-allocated tempFloatBuffer
            int availableSamples = Math.Min(fftLength, e.BytesRecorded / 4);
            if (availableSamples <= 0) return;

            // Copy bytes -> tempFloatBuffer (assumes 32-bit float PCM)
            Buffer.BlockCopy(e.Buffer, 0, tempFloatBuffer, 0, availableSamples * 4);

            // prepare the Complex buffer
            for (int i = 0; i < fftLength; i++)
            {
                float value = i < availableSamples ? tempFloatBuffer[i] : 0f;
                float window = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (fftLength - 1)));
                samples[i] = new Complex(value * window, 0);
            }

            // Do FFT in-place
            FFT(samples);

            // Store magnitudes (thread-safe)
            lock (fftLock)
            {
                int len = Math.Min(fftMagnitudes.Length, samples.Length);
                for (int i = 0; i < len; i++)
                {
                    fftMagnitudes[i] = (float)samples[i].Magnitude;
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
                // Allow colour transitioning depending on boolean
                Col pCol = EnableColourTransition
                    ? Col.Lerp(Secondary, Primary, lerpAmount)
                    : Primary;

                SKColor baseColor = GetColor(pCol).Value();

                // Define gradient to use for the visualiser
                SKColor startColor = baseColor.WithAlpha((byte)(baseColor.Alpha * lerpAmount));
                SKColor endColor = new SKColor(
                    (byte)(baseColor.Red * 0.7),
                    (byte)(baseColor.Green * 0.7),
                    (byte)(baseColor.Blue * 0.7),
                    (byte)(baseColor.Alpha * lerpAmount)
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
    }
}
