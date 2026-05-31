using DynamicWin.Main;
using DynamicWin.Utils;
using SkiaSharp;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DynamicWin.UI
{
    public class UIObject
    {
        private UIObject? parent;
        public UIObject? Parent { get { return parent; } set { parent = value; } }

        private Vec2 position = Vec2.zero;
        private Vec2 localPosition = Vec2.zero;
        private Vec2 anchor = new Vec2(0.5f, 0.5f);
        private Vec2 size = Vec2.one;
        private Col color = Col.White;

        public Vec2 RawPosition { get => position; }
        public Vec2 Position { get => GetPosition() + localPosition; set => position = value; }
        public Vec2 LocalPosition { get => localPosition; set => localPosition = value; }
        public Vec2 Anchor { get => anchor; set => anchor = value; }

        private const float DirtyFloatTolerance = 0.001f;

        public Vec2 Size
        {
            get
            {
                // Ensure size is never null
                if (size == null)
                {
                    // Try to use Vec2.one, fallback to new Vec2 if null
                    size = Vec2.one ?? new Vec2(1f, 1f);
                }

                return size;
            }
            set
            {
                float nextX;
                float nextY;

                // 59xa: what even
                if (value == null)
                {
                    nextX = 1f;
                    nextY = 1f;
                }
                else
                {
                    nextX = Math.Max(1f, value.X);
                    nextY = Math.Max(1f, value.Y);
                }

                if (size != null && NearlyEqual(size.X, nextX) && NearlyEqual(size.Y, nextY))
                    return;

                size = new Vec2(nextX, nextY);
                MarkGpuDirty();
            }
        }

        public Col Color
        {
            get => new Col(color.r, color.g, color.b, color.a * Alpha);
            set
            {
                if (value == null) value = Col.Transparent;
                if (ColorsClose(color, value)) return;

                color = value;
                MarkGpuDirty();
            }
        }

        private bool isHovering = false;
        private bool isMouseDown = false;
        private bool isGlobalMouseDown = false;
        protected bool drawLocalObjects = true;

        public bool IsHovering { get => isHovering; private set => isHovering = value; }
        public bool IsMouseDown { get => isMouseDown; private set => isMouseDown = value; }

        public UIAlignment alignment = UIAlignment.TopCenter;

        protected float localBlurAmount = 0f;
        public float blurAmount = 0f;
        public float roundRadius = 0f;
        public bool maskInToIsland = true;

        private List<UIObject> localObjects = new List<UIObject>();
        public List<UIObject> LocalObjects { get => localObjects; }

        private bool isEnabled = true;
        public bool IsEnabled { get => isEnabled; set => SetActive(value); }

        public float blurSizeOnDisable = 50;

        private float pAlpha = 1f;
        private float oAlpha = 1f;

        public float Alpha
        {
            get => (float)Math.Min(pAlpha, Math.Min(oAlpha, RendererMain.Instance.alphaOverride));
            set
            {
                if (NearlyEqual(oAlpha, value)) return;

                oAlpha = value;
                MarkGpuDirty();
            }
        }

        protected void AddLocalObject(UIObject obj)
        {
            obj.parent = this;
            localObjects.Add(obj);
            MarkGpuDirty();
        }

        protected void DestroyLocalObject(UIObject obj)
        {
            obj.DestroyCall();
            localObjects.Remove(obj);
            MarkGpuDirty();
        }

        public UIObject(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter)
        {
            this.parent = parent;
            this.position = position;
            this.size = size;
            this.alignment = alignment;

            this.contextMenu = CreateContextMenu();

            RendererMain.Instance.ContextMenuOpening += CtxOpen;
            RendererMain.Instance.ContextMenuClosing += CtxClose;

            // GPU cache defaults to auto when renderer is available and GPU is available
            UseGpuCaching = (RendererMain.Instance != null && RendererMain.Instance.IsGpuAvailable);
            gpuCacheDirty = true;
        }

        void CtxOpen(object sender, RoutedEventArgs e)
        {
            if(RendererMain.Instance.ContextMenu != null)
                canInteract = false;
        }

        void CtxClose(object sender, RoutedEventArgs e)
        {
            canInteract = true;
        }

        public Vec2 GetScreenPosFromRawPosition(Vec2 position, Vec2 Size = null, UIAlignment alignment = UIAlignment.None, UIObject parent = null)
        {
            if (parent == null) parent = this.parent;
            if (Size == null) Size = this.Size;
            if (alignment == UIAlignment.None) alignment = this.alignment;

            if (parent == null)
            {
                Vec2 screenDim = RendererMain.ScreenDimensions;
                if (Size == null) Size = Vec2.one;
                switch (alignment)
                {
                    case UIAlignment.TopLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopCenter:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.Center:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomCenter:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                }
            }
            else
            {
                Vec2 parentDim = parent.Size;
                Vec2 parentPos = parent.Position;

                switch (alignment)
                {
                    case UIAlignment.TopLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopCenter:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.Center:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomCenter:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                }
            }

            return Vec2.zero;
        }

        protected virtual Vec2 GetPosition()
        {
            if(parent == null)
            {
                Vec2 screenDim = RendererMain.ScreenDimensions;
                switch (alignment)
                {
                    case UIAlignment.TopLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopCenter:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.Center:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomLeft:
                        return new Vec2(position.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomCenter:
                        return new Vec2(position.X + screenDim.X / 2 - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomRight:
                        return new Vec2(position.X + screenDim.X - (Size.X * Anchor.X),
                            position.Y + screenDim.Y - (Size.Y * Anchor.Y));
                }
            }
            else
            {
                Vec2 parentDim = parent.Size;
                Vec2 parentPos = parent.Position;

                switch (alignment)
                {
                    case UIAlignment.TopLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopCenter:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.TopRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.Center:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.MiddleRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y / 2 - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomLeft:
                        return new Vec2(parentPos.X + position.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomCenter:
                        return new Vec2(parentPos.X + position.X + parentDim.X / 2 - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                    case UIAlignment.BottomRight:
                        return new Vec2(parentPos.X + position.X + parentDim.X - (Size.X * Anchor.X),
                            parentPos.Y + position.Y + parentDim.Y - (Size.Y * Anchor.Y));
                }
            }

            return Vec2.zero;
        }

        public float GetBlur()
        {
            if (!Settings.AllowBlur) return 0f;
            return Math.Max(blurAmount, Math.Max(localBlurAmount, Math.Max((parent == null) ? 0f : parent.GetBlur(), RendererMain.Instance.blurOverride)));
        }

        bool canInteract = true;
        
        public void UpdateCall(float deltaTime)
        {
            if (!isEnabled) return;

            if (parent != null)
                pAlpha = parent.Alpha;

            if (canInteract)
            {
                var cursor = RendererMain.CursorPosition;
                bool leftMouseDown = RendererMain.IsLeftMouseButtonDown;
                var rect = SKRect.Create(cursor.X, cursor.Y, 1, 1);
                isHovering = GetInteractionRect().Contains(rect);

                if (!isGlobalMouseDown && leftMouseDown)
                {
                    isGlobalMouseDown = true;
                    OnGlobalMouseDown();
                }
                else if (isGlobalMouseDown && !leftMouseDown)
                {
                    isGlobalMouseDown = false;
                    OnGlobalMouseUp();
                }

                if (IsHovering && !IsMouseDown && leftMouseDown)
                {
                    IsMouseDown = true;
                    OnMouseDown();
                }
                else if (IsHovering && IsMouseDown && !leftMouseDown)
                {
                    IsMouseDown = false;
                    OnMouseUp();
                }
                else if (IsMouseDown && !leftMouseDown)
                {
                    IsMouseDown = false;
                }
            }

            Update(deltaTime);

            if (drawLocalObjects)
            {
                for (int i = 0; i < localObjects.Count; i++)
                {
                    var obj = localObjects[i];
                    if (obj == null) continue;
                    obj.blurAmount = GetBlur();
                    obj.UpdateCall(deltaTime);
                }
            }
        }

        public virtual void Update(float deltaTime) { }

        public virtual bool WantsRealtimeUpdate => false;
        public virtual bool WantsContinuousUpdate => false;

        public bool SubtreeWantsRealtimeUpdate()
        {
            if (!isEnabled) return false;
            if (WantsRealtimeUpdate) return true;
            if (!drawLocalObjects) return false;

            for (int i = 0; i < localObjects.Count; i++)
            {
                var obj = localObjects[i];
                if (obj != null && obj.SubtreeWantsRealtimeUpdate())
                    return true;
            }

            return false;
        }

        public bool SubtreeWantsContinuousUpdate()
        {
            if (!isEnabled) return false;
            if (WantsContinuousUpdate || WantsRealtimeUpdate) return true;
            if (!drawLocalObjects) return false;

            for (int i = 0; i < localObjects.Count; i++)
            {
                var obj = localObjects[i];
                if (obj != null && obj.SubtreeWantsContinuousUpdate())
                    return true;
            }

            return false;
        }

        // GPU caching fields
        private SKImage? gpuCache = null;
        private bool gpuCacheDirty = true;
        public bool UseGpuCaching { get; set; }

        // Flag to indicate Draw() already rendered full subtree (so DrawCall won't draw children again)
        private bool lastDrawRenderedSubtree = false;

        protected void MarkGpuDirty()
        {
            if (gpuCacheDirty && gpuCache == null)
                return;

            gpuCacheDirty = true;
            try
            {
                gpuCache?.Dispose();
            }
            catch { }
            gpuCache = null;
        }

        protected static bool NearlyEqual(float a, float b, float epsilon = DirtyFloatTolerance)
        {
            return Math.Abs(a - b) <= epsilon;
        }

        protected static bool ColorsClose(Col a, Col b, float epsilon = DirtyFloatTolerance)
        {
            if (a == null || b == null) return a == b;

            return NearlyEqual(a.r, b.r, epsilon)
                && NearlyEqual(a.g, b.g, epsilon)
                && NearlyEqual(a.b, b.b, epsilon)
                && NearlyEqual(a.a, b.a, epsilon);
        }

        public void DrawCall(SKCanvas canvas)
        {
            if (!isEnabled) return;

            // Reset subtree flag
            lastDrawRenderedSubtree = false;

            // Draw this object (may draw subtree if cached)
            Draw(canvas);

            // If Draw rendered subtree already, skip drawing children again
            if (drawLocalObjects && !lastDrawRenderedSubtree)
            {
                for (int i = 0; i < localObjects.Count; i++)
                {
                    var obj = localObjects[i];
                    if (obj == null) continue;

                    // Children positions are relative to this object's origin; ensure we translate appropriately
                    obj.DrawCall(canvas);
                }
            }

            // Clear flag after use
            lastDrawRenderedSubtree = false;
        }

        /// <summary>
        /// Draw only this object's visual (no children) at local origin (0,0)
        /// Used by both CPU and GPU paths
        /// </summary>
        protected virtual void DrawSelfContents(SKCanvas canvas)
        {
            var rect = SKRect.Create(0, 0, Size.X, Size.Y);
            var roundRect = new SKRoundRect(rect, roundRadius);

            using var paint = GetPaint();

            canvas.DrawRoundRect(roundRect, paint);
        }

        /// <summary>
        /// Draw this object and its subtree into the provided canvas, assuming origin is at this object's top-left
        /// Used when rendering into a GPU surface for caching
        /// </summary>
        protected virtual void DrawIntoSurface(SKCanvas canvas)
        {
            // Draw self at origin
            DrawSelfContents(canvas);

            if (!drawLocalObjects) return;

            for (int i = 0; i < localObjects.Count; i++)
            {
                var obj = localObjects[i];
                if (obj == null) continue;

                canvas.Save();
                canvas.Translate(obj.LocalPosition.X, obj.LocalPosition.Y);
                obj.DrawIntoSurface(canvas);
                canvas.Restore();
            }
        }

        public virtual void Draw(SKCanvas canvas)
        {
            // Render/copy the cached GPU image of the subtree instead of CPU drawing if GPU caching or GRContext exists
            if (UseGpuCaching && RendererMain.Instance != null && RendererMain.Instance.IsGpuAvailable)
            {
                try
                {
                    if (gpuCache == null || gpuCacheDirty)
                    {
                        // Create GPU surface sized to object
                        var info = new SKImageInfo((int)Math.Max(1, Size.X), (int)Math.Max(1, Size.Y), SKImageInfo.PlatformColorType, SKAlphaType.Premul);

                        // Use a shared cache to avoid re-creating GPU images for identical content when possible
                        string cacheKey = $"UIObject_{GetType().FullName}_{GetHashCode()}_{(int)Size.X}x{(int)Size.Y}";

                        gpuCache = GPUTextureCache.GetOrCreate(cacheKey, () =>
                        {
                            using (var surface = RendererMain.Instance.CreateGpuSurface(info, true))
                            {
                                if (surface == null) return null;

                                var localCanvas = surface.Canvas;
                                localCanvas.Clear(SKColors.Transparent);

                                // Draw object and children at local origin
                                DrawIntoSurface(localCanvas);
                                localCanvas.Flush();

                                var img = surface.Snapshot();
                                return img;
                            }
                        });

                        // Mark dirty and fall back to CPU if cache is null
                        if (gpuCache == null) gpuCacheDirty = true;
                        else gpuCacheDirty = false;
                    }

                    if (gpuCache != null)
                    {
                        // Draw the cached image at the object's position
                        canvas.Save();
                        canvas.Translate(Position.X, Position.Y);
                        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
                        canvas.DrawImage(gpuCache, 0, 0, paint);
                        canvas.Restore();

                        // Indicate that the subtree was fully rendered by GPU cache
                        lastDrawRenderedSubtree = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Any GPU error falls back to CPU drawing
                    System.Diagnostics.Debug.WriteLine($"UIObject: GPU draw failed, falling back to CPU. Exception: {ex}");
                    gpuCacheDirty = true;
                }
            }

            // Default CPU path: translate to Position and draw only this object's content. Children will be drawn by DrawCall loop
            canvas.Save();
            canvas.Translate(Position.X, Position.Y);
            DrawSelfContents(canvas);
            canvas.Restore();
        }

        public virtual SKPaint GetPaint()
        {
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = this.Color.Value(),
                IsAntialias = Settings.AntiAliasing,
                IsDither = true,
                SubpixelText = false,
                FilterQuality = SKFilterQuality.Medium,
                HintingLevel = SKPaintHinting.Normal,
                IsLinearText = true
            };

            if(GetBlur() != 0f)
            {
                var blur = SKImageFilter.CreateBlur(GetBlur(), GetBlur());
                paint.ImageFilter = blur;
            }

            return paint;
        }

        public Col GetColor(Col col)
        {
            return new Col(col.r, col.g, col.b, col.a * Alpha);
        }

        public void DestroyCall() 
        {
            // Unsubscribe handlers registered in ctor to avoid leaking RendererMain/Dispatcher references
            try
            {
                if (RendererMain.Instance != null)
                {
                    RendererMain.Instance.ContextMenuOpening -= CtxOpen;
                    RendererMain.Instance.ContextMenuClosing -= CtxClose;
                }
            }
            catch { }

            // Destroy and clear all local children (recursively)
            for (int i = localObjects.Count - 1; i >= 0; i--)
            {
                try
                {
                    var obj = localObjects[i];
                    obj?.DestroyCall();
                }
                catch { }
            }

            localObjects.Clear();

            // Dispose GPU cache and remove from shared cache
            try { gpuCache?.Dispose(); } catch { }
            if (gpuCache != null)
            {
                try { GPUTextureCache.Remove($"UIObject_{GetType().FullName}_{GetHashCode()}_{(int)Size.X}x{(int)Size.Y}"); } catch { }
            }
            gpuCache = null;

            // Allow derived types to run cleanup logic
            OnDestroy();
        }

        public virtual void OnDestroy() { }

        public virtual void OnMouseDown() { }
        public virtual void OnGlobalMouseDown() { }

        public virtual void OnMouseUp() { }
        public virtual void OnGlobalMouseUp() { }

        public void SilentSetActive(bool isEnabled)
        {
            if (this.isEnabled == isEnabled && lastSetActiveCall == isEnabled) return;
            lastSetActiveCall = isEnabled;
            OnActiveChanged(isEnabled);
            this.isEnabled = isEnabled;
        }

        Animator toggleAnim;
        bool lastSetActiveCall = true;

        public void SetActive(bool isEnabled)
        {
            if(this.isEnabled == isEnabled && lastSetActiveCall == isEnabled) return;
            if (toggleAnim != null && toggleAnim.IsRunning) toggleAnim.Stop();

            lastSetActiveCall = isEnabled;

            // Notify derived classes of requested active change so they can start/stop background work immediately
            OnActiveChanged(isEnabled);

            if (isEnabled)
            {
                localBlurAmount = blurSizeOnDisable;
                Alpha = 0f;
                this.isEnabled = isEnabled;
            }

            toggleAnim = new Animator(250, 1);
            toggleAnim.onAnimationUpdate += (t) =>
            {
                if(t >= 0.5f) this.isEnabled = isEnabled;

                if (isEnabled)
                {
                    var tEased = Easings.EaseOutCubic(t);

                    localBlurAmount = Mathf.Lerp(blurSizeOnDisable, 0, tEased);
                    Alpha = Mathf.Lerp(0, 1, tEased);
                }
                else
                {
                    var tEased = Easings.EaseOutCubic(t);

                    localBlurAmount = Mathf.Lerp(0, blurSizeOnDisable, tEased);
                    Alpha = Mathf.Lerp(1, 0, tEased);
                }
            };
            toggleAnim.onAnimationEnd += () =>
            {
                this.isEnabled = isEnabled;
                localBlurAmount = 0f;
                Alpha = 1f;
                DestroyLocalObject(toggleAnim);
            };

            AddLocalObject(toggleAnim);
            toggleAnim.Start();

            MarkGpuDirty();

            // Also mark parent/ancestor GPU caches dirty so cached images do not include stale child visuals
            try
            {
                var p = this.parent;
                while (p != null)
                {
                    p.MarkGpuDirty();
                    p = p.parent;
                }
            }
            catch { }
            
        }

        /// <summary>
        /// Called when SetActive(...) is requested and the new active-state differs from prior calls
        /// Override in derived types to react to immediate active-state changes (start/stop background work, etc)
        /// This function is called as soon as SetActive is invoked (before the animator completes)
        /// </summary>
        /// <param name="isEnabled">requested active state</param>
        protected virtual void OnActiveChanged(bool isEnabled) { }

        public virtual SKRect GetRawRect()
        {
            float w = Math.Max(1f, Size.X);
            float h = Math.Max(1f, Size.Y);

            return SKRect.Create(Position.X, Position.Y, w, h);
        }

        public virtual SKRoundRect GetRect()
        {
            var rect = GetRawRect();
            return new SKRoundRect(rect, roundRadius);
        }

        public int expandInteractionRect = 5;

        public virtual SKRoundRect GetInteractionRect()
        {
            SKRect rect = SKRect.Create(
                Position.X - expandInteractionRect,
                Position.Y - expandInteractionRect,
                Size.X + 2 * expandInteractionRect,
                Size.Y + 2 * expandInteractionRect
            );

            return new SKRoundRect(rect, roundRadius);
        }

        ContextMenu? contextMenu = null;

        public virtual ContextMenu? CreateContextMenu() { return null; }
        public virtual ContextMenu? GetContextMenu() { return contextMenu; }

        // Build an approximate superellipse (squircle) path by sampling points
        public static SKPath BuildSuperellipsePath(
            SKRect r,
            float radius,
            float t
        )
        {
            var path = new SKPath();
            path.FillType = SKPathFillType.Winding;

            float x0 = r.Left;
            float x1 = r.Right;
            float y0 = r.Top;
            float y1 = r.Bottom;

            // Clamp radius safely
            float maxRadius = Math.Min(r.Width, r.Height) * 0.5f;
            radius = Math.Min(radius, maxRadius - 0.01f);

            // Match dynamic island curvature shaping
            const float kappa = 0.55228475f;
            float easedT = t * t * (3f - 2f * t);
            float squash = Mathf.Lerp(1f, 1.16f, easedT);
            float ctrl = radius * kappa * squash;

            // Start top-left
            path.MoveTo(x0, y0 + radius);

            // Top-left corner
            path.CubicTo(
                x0, y0 + radius - ctrl,
                x0 + radius - ctrl, y0,
                x0 + radius, y0
            );

            // Top edge
            path.LineTo(x1 - radius, y0);

            // Top-right corner
            path.CubicTo(
                x1 - radius + ctrl, y0,
                x1, y0 + radius - ctrl,
                x1, y0 + radius
            );

            // Right edge
            path.LineTo(x1, y1 - radius);

            // Bottom-right corner
            path.CubicTo(
                x1, y1 - radius + ctrl,
                x1 - radius + ctrl, y1,
                x1 - radius, y1
            );

            // Bottom edge
            path.LineTo(x0 + radius, y1);

            // Bottom-left corner
            path.CubicTo(
                x0 + radius - ctrl, y1,
                x0, y1 - radius + ctrl,
                x0, y1 - radius
            );

            path.Close();
            return path;
        }
    }

    public enum UIAlignment
    {
        TopLeft,
        TopCenter,
        TopRight,
        MiddleLeft,
        Center,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight,
        None
    }
}
