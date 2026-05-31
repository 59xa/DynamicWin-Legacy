using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;

namespace DynamicWin.WPFBinders
{
    [DefaultEvent("PaintSurface")]
    [DefaultProperty("Name")]
    public class SKElement : FrameworkElement
    {
        private const double BitmapDpi = 96.0;

        private readonly bool designMode;

        private WriteableBitmap bitmap;
        private bool ignorePixelScaling;

        // Create GRContext for GPU rendering if available
        public GRContext? GrContext { get; private set; }

        public SKElement()
        {
            designMode = DesignerProperties.GetIsInDesignMode(this);

            GrContext = null;
        }

        public SKSize CanvasSize { get; private set; }

        public bool IgnorePixelScaling
        {
            get => ignorePixelScaling;
            set
            {
                ignorePixelScaling = value;
                InvalidateVisual();
            }
        }

        [Category("Appearance")]
        public event EventHandler<SKPaintSurfaceEventArgs> PaintSurface;

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (designMode)
                return;

            if (Visibility != Visibility.Visible || PresentationSource.FromVisual(this) == null)
                return;

            var size = CreateSize(out var unscaledSize, out var scaleX, out var scaleY);
            var userVisibleSize = IgnorePixelScaling ? unscaledSize : size;

            CanvasSize = userVisibleSize;

            if (size.Width <= 0 || size.Height <= 0)
                return;

            var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);

            // Attempt to create a GPU-backed surface if GPU context exists. Fall back to CPU otherwise
            if (GrContext != null)
            {
                try
                {
                    // Create raster surface backed by GPU context
                    using (var surface = SKSurface.Create(GrContext, true, info))
                    {
                        if (IgnorePixelScaling)
                        {
                            var canvas = surface.Canvas;
                            canvas.Scale(scaleX, scaleY);
                            canvas.Save();
                        }

                        OnPaintSurface(new SKPaintSurfaceEventArgs(surface, info.WithSize(userVisibleSize), info));

                        // Flush GPU commands
                        surface.Canvas.Flush();
                    }

                    // Draw nothing to the drawingContext here because GPU-backed drawing is handled on the GPU
                    // Something still needs to be presented in WPF; to keep compatibility
                    // with the previous implementation, draw the last bitmap if present. Skip drawing if no bitmap exists
                    if (bitmap != null)
                    {
                        drawingContext.DrawImage(bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
                    }

                    return;
                }
                catch (Exception ex)
                {
                    // If any GPU path fails, log and fall back to CPU path below
                    Debug.WriteLine($"SKElement: GPU rendering failed, falling back to CPU. Exception: {ex}");
                    // Do not dispose the context here - allow the host to decide. Null it so fallback will be used
                    GrContext = null;
                }
            }

            // CPU fallback (existing behavior)
            // reset the bitmap if the size has changed
            if (bitmap == null || info.Width != bitmap.PixelWidth || info.Height != bitmap.PixelHeight)
            {
                bitmap = new WriteableBitmap(info.Width, size.Height, BitmapDpi * scaleX, BitmapDpi * scaleY, PixelFormats.Pbgra32, null);
            }

            // draw on the bitmap
            bitmap.Lock();
            using (var surface = SKSurface.Create(info, bitmap.BackBuffer, bitmap.BackBufferStride))
            {
                if (IgnorePixelScaling)
                {
                    var canvas = surface.Canvas;
                    canvas.Scale(scaleX, scaleY);
                    canvas.Save();
                }

                OnPaintSurface(new SKPaintSurfaceEventArgs(surface, info.WithSize(userVisibleSize), info));
            }

            // draw the bitmap to the screen
            bitmap.AddDirtyRect(new Int32Rect(0, 0, info.Width, size.Height));
            bitmap.Unlock();
            drawingContext.DrawImage(bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        protected virtual void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            // invoke the event
            PaintSurface?.Invoke(this, e);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);

            InvalidateVisual();
        }

        private SKSizeI CreateSize(out SKSizeI unscaledSize, out float scaleX, out float scaleY)
        {
            unscaledSize = SKSizeI.Empty;
            scaleX = 1.0f;
            scaleY = 1.0f;

            var w = ActualWidth;
            var h = ActualHeight;

            if (!IsPositive(w) || !IsPositive(h))
                return SKSizeI.Empty;

            unscaledSize = new SKSizeI((int)w, (int)h);

            var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            scaleX = (float)m.M11;
            scaleY = (float)m.M22;
            return new SKSizeI((int)(w * scaleX), (int)(h * scaleY));

            bool IsPositive(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
            }
        }

        /// <summary>
        /// Explicitly set a GRContext (for example a D3D-backed context created by host code). This allows external code to supply a GPU context
        /// (Direct3D, Vulkan, Metal, etc) instead of relying on the built-in GL initialisation
        /// </summary>
        /// <param name="context">The GRContext to use. Pass null to clear and force CPU fallback</param>
        /// <param name="disposeExisting">If true, dispose any existing context before replacing it</param>
        public void SetGrContext(GRContext? context, bool disposeExisting = false)
        {
            try
            {
                if (disposeExisting && GrContext != null)
                {
                    GrContext.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SKElement: Exception disposing existing GRContext: {ex}");
            }

            GrContext = context;
            Debug.WriteLine($"SKElement: GRContext explicitly set. GPU available: {GrContext != null}");
        }

        /// <summary>
        /// Helper to create a GPU-backed surface using the currently configured GRContext. Returns null if no GRContext is available
        /// </summary>
        public SKSurface? CreateGpuSurface(SKImageInfo info, bool isRenderTarget = true)
        {
            if (GrContext == null) return null;

            try
            {
                return SKSurface.Create(GrContext, isRenderTarget, info);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SKElement: Failed to create GPU surface: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Indicates whether GPU-backed rendering is available for this element
        /// </summary>
        public bool IsGpuAvailable => GrContext != null;
    }
}
