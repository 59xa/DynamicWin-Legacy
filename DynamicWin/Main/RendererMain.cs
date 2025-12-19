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
        private IslandObject islandObject;
        public IslandObject MainIsland => islandObject;
        private List<UIObject> objects => MenuManager.Instance.ActiveMenu.UiObjects;

        public static Vec2 ScreenDimensions => new Vec2(MainForm.Instance.Width, MainForm.Instance.Height);
        public static Vec2 CursorPosition => new Vec2(Mouse.GetPosition(MainForm.Instance).X, Mouse.GetPosition(MainForm.Instance).Y);

        private static RendererMain instance;
        public static RendererMain Instance => instance;

        // Guard so the startup updater sequence runs only once per application lifetime
        private static bool startupUpdaterSequenceStarted = false;

        public Vec2 renderOffset = Vec2.zero;
        public Vec2 scaleOffset = Vec2.one;
        public float blurOverride = 0f;
        public float alphaOverride = 1f;

        public Action<float> onUpdate;
        public Action<SKCanvas> onDraw;

        private Stopwatch? updateStopwatch;
        private int initialScreenBrightness = 0;
        private float deltaTime = 0f;
        public float DeltaTime => deltaTime;

        private bool isInitialized = false;
        public int canvasWithoutClip;

        public RendererMain()
        {
            MenuManager m = new MenuManager();
            instance = this;
            islandObject = new IslandObject();
            m.Init();

            initialScreenBrightness = BrightnessAdjustMenu.GetBrightness();
            KeyHandler.onKeyDown += OnKeyRegistered;

            MainForm.Instance.DragEnter += MainForm.Instance.MainForm_DragEnter;
            MainForm.Instance.DragLeave += MainForm.Instance.MainForm_DragLeave;
            MainForm.Instance.Drop += MainForm.Instance.OnDrop;
            MainForm.Instance.MouseWheel += MainForm.Instance.OnScroll;

            // Get refresh rate via centralized helper
            int refreshRate = DisplayHelper.GetRefreshRate();
            Debug.WriteLine($"Monitor Refresh Rate: {refreshRate} Hz");

            // Register to MainForm's centrally throttled render callback instead of subscribing directly to CompositionTarget.Rendering.
            MainForm.Instance.onMainFormRender += Frame;

            // Start updater check sequence: wait 5s, show overlay, check for update, then open appropriate menu
            // Ensure this sequence starts only once per application lifetime
            if (!startupUpdaterSequenceStarted)
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
                    MenuManager.OpenOverlayMenu(new VolumeAdjustMenu(), 100f);
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

            if (BrightnessAdjustMenu.GetBrightness() != initialScreenBrightness && PopupOptions.saveData.brightnessPopup)
            {
                initialScreenBrightness = BrightnessAdjustMenu.GetBrightness();
                if (MenuManager.Instance.ActiveMenu is HomeMenu)
                {
                    MenuManager.OpenOverlayMenu(new BrightnessAdjustMenu(), 100f);
                }
                else if (BrightnessAdjustMenu.timerUntilClose != null)
                {
                    BrightnessAdjustMenu.PressBK();
                    BrightnessAdjustMenu.timerUntilClose = 0f;
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

            if (MainIsland.hidden) return;

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

        private void Mask(SKCanvas canvas)
        {
            var islandMask = GetMask();
            canvas.ClipRoundRect(islandMask);
        }

        public SKRoundRect GetMask()
        {
            var islandMask = islandObject.GetRect();
            islandMask.Deflate(new SKSize(1, 1));
            return islandMask;
        }
    }
}