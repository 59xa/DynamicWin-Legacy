using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using DynamicWin.Resources;
using DynamicWin.UI;
using DynamicWin.UI.Menu;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using DynamicWin.WPFBinders;
using SkiaSharp;

namespace DynamicWin.Main
{
    public class RendererMain : SKElement
    {
        private readonly IslandObject islandObject;
        public IslandObject MainIsland => islandObject;
        private List<UIObject> objects => MenuManager.Instance.ActiveMenu.UiObjects;

        // Shadow mask for island
        private BottomMask? islandShadow;
        private bool lastIslandShadowSetting = Settings.ToggleIslandShadow;
        private bool lastShadowState = false; // Tracks whether shadow was active last frame

        public static Vec2 ScreenDimensions
        {
            get
            {
                if (instance != null && instance.inputSnapshotValid)
                    return instance.cachedScreenDimensions;

                var active = System.Windows.Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
                if (active != null) return new Vec2((float)active.Width, (float)active.Height);
                return new Vec2((float)MainForm.Instance.Width, (float)MainForm.Instance.Height);
            }
        }

        public static Vec2 CursorPosition
        {
            get
            {
                if (instance != null && instance.inputSnapshotValid)
                    return instance.cachedCursorPosition;

                var active = System.Windows.Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
                if (active != null)
                {
                    var pos = Mouse.GetPosition(active);
                    return new Vec2((float)pos.X, (float)pos.Y);
                }
                var mainPos = Mouse.GetPosition(MainForm.Instance);
                return new Vec2((float)mainPos.X, (float)mainPos.Y);
            }
        }

        public static bool IsLeftMouseButtonDown
        {
            get
            {
                if (instance != null && instance.inputSnapshotValid)
                    return instance.cachedLeftMouseDown;

                return Mouse.LeftButton == MouseButtonState.Pressed;
            }
        }

        private static RendererMain? instance;
        public static RendererMain? Instance => instance;

        // Guard so the startup updater sequence runs only once per application lifetime
        private static bool startupUpdaterSequenceStarted = false;

        public Vec2 renderOffset = Vec2.zero;
        public Vec2 scaleOffset = Vec2.one;
        public float blurOverride = 0f;
        public float alphaOverride = 1f;

        public Action<float>? onUpdate;
        public Action<SKCanvas>? onDraw;

        private Stopwatch? updateStopwatch;
        private int initialScreenBrightness = 0;
        private DateTime lastBrightnessPoll = DateTime.MinValue;
        private readonly TimeSpan brightnessPollInterval = TimeSpan.FromMilliseconds(500);
        private float deltaTime = 0f;
        public float DeltaTime => deltaTime;

        private bool isInitialized = false;
        public int canvasWithoutClip;

        private bool inputSnapshotValid;
        private Vec2 cachedCursorPosition = Vec2.zero;
        private Vec2 cachedScreenDimensions = Vec2.zero;
        private bool cachedLeftMouseDown;

        public bool WantsRealtimeRendering { get; private set; } = true;
        public bool WantsContinuousRendering { get; private set; } = true;

        public RendererMain()
        {
            MenuManager m = new MenuManager();
            instance = this;
            islandObject = new IslandObject();
            m.Init();

            // Determine whether island shadow should exist on startup
            UpdateIslandShadowState();

            initialScreenBrightness = BrightnessAdjustMenu.GetBrightness();
            KeyHandler.onKeyDown += OnKeyRegistered;

            MainForm.Instance.DragEnter += MainForm.Instance.MainForm_DragEnter;
            MainForm.Instance.DragLeave += MainForm.Instance.MainForm_DragLeave;
            MainForm.Instance.Drop += MainForm.Instance.OnDrop;
            MainForm.Instance.MouseWheel += MainForm.Instance.OnScroll;

            // Register to MainForm's centrally throttled render callback instead of subscribing directly to CompositionTarget.Rendering.
            MainForm.Instance.onMainFormRender += Frame;

            // Start updater check sequence: wait 5s, show overlay, check for update, then open appropriate menu
            // Ensure this sequence starts only once per application lifetime
            // Only start automatic startup updater sequence if user opted in
            if (!startupUpdaterSequenceStarted && Settings.AllowAutomaticUpdates)
            {
                startupUpdaterSequenceStarted = true;

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000);

                        if (MenuManager.Instance.ActiveMenu is SettingsMenu)
                        {
                            // If user is in SettingsMenu, don't interrupt them with updater
                            return;
                        }

                        // Show overlay manually
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            MenuManager.OpenOverlayMenu(new UpdaterOverlay(), 0f); // 0f = no auto-close
                        });

                        var updater = new Updater();
                        AppVersion? update = null;

                        try
                        {
                            update = await updater.CheckForUpdate();
                        }
                        catch { update = null; }

                        // Close overlay & open correct menu
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            // Force overlay unlock
                            MenuManager.CloseOverlay();

                            // Force queued menus to process immediately
                            if (MenuManager.Instance != null)
                            {
                                MenuManager.Instance.UnlockMenu();
                            }

                            if (update == null)
                            {
                                MenuManager.OpenMenu(Res.HomeMenu);
                            }
                            else
                            {
#if DEBUG
                                Debug.WriteLine($"[UPDATER] Update received from remote: {update.version}");
#endif
                                MenuManager.OpenMenu(new UpdaterMenu(update));
                            }
                        });
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine("[UPDATER] Updater sequence error: " + ex.Message);
#endif
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            MenuManager.CloseOverlay();
                            MenuManager.Instance?.UnlockMenu();
                            MenuManager.OpenMenu(Res.HomeMenu);
                        });
                    }
                });
            }

            isInitialized = true;
        }

        public void Destroy()
        {
            // Unregister from central render callback
            if (MainForm.Instance != null)
                MainForm.Instance.onMainFormRender -= Frame;

            // if (fallbackTimer != null) fallbackTimer.Stop();

            KeyHandler.onKeyDown -= OnKeyRegistered;
            MainForm.Instance.DragEnter -= MainForm.Instance.MainForm_DragEnter;
            MainForm.Instance.DragLeave -= MainForm.Instance.MainForm_DragLeave;

            MainForm.Instance.MouseWheel -= MainForm.Instance.OnScroll;

            // Dispose island shadow if present
            try
            {
                islandShadow?.DestroyCall();
                islandShadow = null;
            }
            catch { }

            instance = null;
        }

        // Called from MainForm's throttled rendering loop
        public void Frame()
        {
            Update();
            // InvalidateVisual must be called on UI thread; this Frame runs on UI thread because MainForm invokes onMainFormRender from CompositionTarget.Rendering.
            InvalidateVisual();
        }

        private void OnKeyRegistered(Keys key, KeyModifier modifier)
        {
            if (key == Keys.LWin && modifier.isCtrlDown)
            {
                islandObject.hidden = !islandObject.hidden;
            }

            if ((key == Keys.VolumeDown || key == Keys.VolumeMute || key == Keys.VolumeUp) && PopupOptions.saveData.volumePopup)
            {
                if (MenuManager.Instance.ActiveMenu is HomeMenu)
                {
                    MenuManager.OpenOverlayMenu(new VolumeAdjustMenu(), 2.75f);
                }
                else if (VolumeAdjustMenu.timerUntilClose != null)
                {
                    VolumeAdjustMenu.timerUntilClose = 0f;
                }
            }

            if (key == Keys.MediaNextTrack || key == Keys.MediaPreviousTrack)
            {
                if (MenuManager.Instance.ActiveMenu is HomeMenu)
                {
                    if (key == Keys.MediaNextTrack) Res.HomeMenu.NextSong();
                    else Res.HomeMenu.PrevSong();
                }
            }
        }

        private void Update()
        {
            CaptureInputSnapshot();

            if (updateStopwatch != null)
            {
                updateStopwatch.Stop();
                deltaTime = updateStopwatch.ElapsedMilliseconds / 1000f;
            }
            else
            {
                deltaTime = 1f / 1000f;
            }

            updateStopwatch = Stopwatch.StartNew();

            onUpdate?.Invoke(DeltaTime);

            if (PopupOptions.saveData.brightnessPopup && (DateTime.UtcNow - lastBrightnessPoll) >= brightnessPollInterval)
            {
                lastBrightnessPoll = DateTime.UtcNow;

                int currentBrightness = BrightnessAdjustMenu.GetBrightness();
                if (currentBrightness != initialScreenBrightness)
                {
                    initialScreenBrightness = currentBrightness;
                    if (MenuManager.Instance.ActiveMenu is HomeMenu)
                    {
                        MenuManager.OpenOverlayMenu(new BrightnessAdjustMenu());
                    }
                    else if (BrightnessAdjustMenu.timerUntilClose != null)
                    {
                        BrightnessAdjustMenu.PressBK();
                        BrightnessAdjustMenu.timerUntilClose = 0f;
                    }
                }
            }

            MenuManager.Instance.Update(DeltaTime);

            // Defensive: if the active menu becomes null or has no UI objects, restore the HomeMenu
            try
            {
                var active = MenuManager.Instance.ActiveMenu;

                if (active == null ||
                    active.UiObjects == null ||
                    active.UiObjects.Count == 0)
                {
                    // Allow UpdaterMenu to initialize without being overridden
                    if (active is UpdaterMenu)
                        return;

                    MenuManager.OpenMenu(Res.HomeMenu);
                }
            }
            catch { }

            if (MenuManager.Instance.ActiveMenu != null)
            {
                MenuManager.Instance.ActiveMenu.Update();

                if (MenuManager.Instance.ActiveMenu is DropFileMenu && !MainForm.Instance.isDragging)
                    MenuManager.OpenMenu(Res.HomeMenu);
            }

            islandObject.UpdateCall(DeltaTime);

            bool shouldRenderShadow = Settings.ToggleIslandShadow;

            // Disable shadow for HomeMenu if setting is off and island not hovered
            if (MenuManager.Instance.ActiveMenu is HomeMenu && !Settings.ToggleHomeMenuShadow && !MainIsland.IsHovering)
            {
                shouldRenderShadow = false;
            }

            if (shouldRenderShadow != lastShadowState)
            {
                lastShadowState = shouldRenderShadow;

                if (shouldRenderShadow)
                    CreateIslandShadow();
                else
                    DestroyIslandShadow();
            }

            // Update island shadow to follow island
            islandShadow?.UpdateCall(DeltaTime);

            if (MainIsland.hidden)
            {
                UpdateRenderDemand();
                return;
            }

            // Take a stable snapshot of the menu object list to avoid InvalidOperationException
            var uiObjectsSnapshot = objects?.ToArray();
            if (uiObjectsSnapshot != null)
            {
                for (int i = 0; i < uiObjectsSnapshot.Length; i++)
                {
                    var uiObject = uiObjectsSnapshot[i];
                    if (uiObject == null) continue;
                    uiObject.UpdateCall(DeltaTime);
                }
            }

            UpdateRenderDemand();
        }

        private void CaptureInputSnapshot()
        {
            try
            {
                var active = System.Windows.Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? MainForm.Instance;

                var pos = Mouse.GetPosition(active);
                cachedCursorPosition = new Vec2((float)pos.X, (float)pos.Y);

                double width = active.ActualWidth > 0 ? active.ActualWidth : active.Width;
                double height = active.ActualHeight > 0 ? active.ActualHeight : active.Height;
                cachedScreenDimensions = new Vec2((float)width, (float)height);
                cachedLeftMouseDown = Mouse.LeftButton == MouseButtonState.Pressed;
                inputSnapshotValid = true;
            }
            catch
            {
                inputSnapshotValid = false;
                cachedLeftMouseDown = Mouse.LeftButton == MouseButtonState.Pressed;
            }
        }

        private void UpdateRenderDemand()
        {
            bool realtime = false;
            bool continuous = false;

            try
            {
                realtime |= MenuManager.Instance?.IsAnimating == true;
                realtime |= islandObject.SubtreeWantsRealtimeUpdate();
                continuous |= islandObject.SubtreeWantsContinuousUpdate();

                if (islandShadow != null)
                {
                    realtime |= islandShadow.SubtreeWantsRealtimeUpdate();
                    continuous |= islandShadow.SubtreeWantsContinuousUpdate();
                }

                var activeObjects = objects;
                if (activeObjects != null)
                {
                    for (int i = 0; i < activeObjects.Count; i++)
                    {
                        var obj = activeObjects[i];
                        if (obj == null) continue;

                        if (!realtime && obj.SubtreeWantsRealtimeUpdate())
                            realtime = true;

                        if (!continuous && obj.SubtreeWantsContinuousUpdate())
                            continuous = true;

                        if (realtime && continuous)
                            break;
                    }
                }
            }
            catch
            {
                realtime = true;
                continuous = true;
            }

            WantsRealtimeRendering = realtime;
            WantsContinuousRendering = continuous || realtime;
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            base.OnPaintSurface(e);

            if (!isInitialized) return;

            SKSurface surface = e.Surface;
            SKCanvas canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);

            double dpiFactor = System.Windows.PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;
            canvas.Scale((float)dpiFactor, (float)dpiFactor);

            canvasWithoutClip = canvas.Save();

            // Draw island shadow first (so it sits behind island)
            if (Settings.ToggleIslandShadow && islandShadow != null)
            {
                try { islandShadow.DrawCall(canvas); } catch { }
            }

            if (islandObject.maskInToIsland) Mask(canvas);
            islandObject.DrawCall(canvas);

            if (MainIsland.hidden) return;

            bool hasContextMenu = false;

            // Snapshot the active menu's UiObjects to avoid collection modifications while painting
            var uiObjectsSnapshot = objects?.ToArray();
            if (uiObjectsSnapshot != null)
            {
                foreach (var uiObject in uiObjectsSnapshot)
                {
                    if (uiObject == null) continue;

                    canvas.RestoreToCount(canvasWithoutClip);
                    canvasWithoutClip = canvas.Save();

                    // Snapshot the local objects too before enum
                    var localSnapshot = uiObject.LocalObjects?.ToArray();

                    if (uiObject.IsHovering && uiObject.GetContextMenu() != null)
                    {
                        hasContextMenu = true;
                        ContextMenu = uiObject.GetContextMenu();
                    }

                    if (localSnapshot != null)
                    {
                        foreach (var obj in localSnapshot)
                        {
                            if (obj == null) continue;
                            if (obj.IsHovering && obj.GetContextMenu() != null)
                            {
                                hasContextMenu = true;
                                ContextMenu = obj.GetContextMenu();
                            }
                        }
                    }

                    if (uiObject.maskInToIsland)
                    {
                        Mask(canvas);
                    }

                    canvas.Scale(scaleOffset.X, scaleOffset.Y, islandObject.Position.X + islandObject.Size.X / 2, islandObject.Position.Y + islandObject.Size.Y / 2);
                    canvas.Translate(renderOffset.X, renderOffset.Y);

                    uiObject.DrawCall(canvas);
                }
            }

            onDraw?.Invoke(canvas);

            if (!hasContextMenu) ContextMenu = null;

            canvas.Flush();
        }

        private void UpdateIslandShadowState()
        {
            bool shouldRenderShadow = Settings.ToggleIslandShadow;

            // Disable shadow for HomeMenu if setting is off and island not hovered
            if (MenuManager.Instance?.ActiveMenu is HomeMenu && !Settings.ToggleHomeMenuShadow && !MainIsland.IsHovering)
            {
                shouldRenderShadow = false;
            }

            lastShadowState = shouldRenderShadow;

            if (shouldRenderShadow)
                CreateIslandShadow();
            else
                DestroyIslandShadow();
        }

        private void CreateIslandShadow()
        {
            try
            {
                islandShadow = new BottomMask(null, islandObject);
                islandShadow.shadowStrength = 20f;
                islandShadow.padding = 2f;
                islandShadow.alpha = 0.6f;
                islandShadow.roundRadius = islandObject.roundRadius;
            }
            catch
            {
                islandShadow = null;
            }
        }

        private void DestroyIslandShadow()
        {
            try
            {
                islandShadow?.DestroyCall();
            }
            catch { }
            islandShadow = null;
        }

        private void Mask(SKCanvas canvas)
        {
            using var path = MainIsland.GetIslandPath();
            canvas.ClipPath(path, SKClipOperation.Intersect, Settings.AntiAliasing);
        }
    }
}
