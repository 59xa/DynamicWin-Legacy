using DynamicWin.Main;
using DynamicWin.UI.Menu.Menus;
using DynamicWin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;
using System.Threading;
using System.Windows;

namespace DynamicWin.UI.Menu
{
    public class MenuManager
    {
        private BaseMenu activeMenu;
        public BaseMenu ActiveMenu { get => activeMenu; }

        private static MenuManager instance;
        public static MenuManager Instance { get => instance; }

        public Action<BaseMenu, BaseMenu> onMenuChange;
        public Action<BaseMenu> onMenuChangeEnd;

        private BaseMenu overlayMenu = null;
        private BaseMenu overlayNextMenu = null;

        private CancellationTokenSource overlayCts = null;

        public MenuManager()
        {
            instance = this;
        }

        public void Init()
        {
            Resources.Res.CreateStaticMenus();
            activeMenu = Resources.Res.HomeMenu;
        }

        // Locking support
        private BaseMenu lockedMenu = null;

        public void LockMenu(BaseMenu menu)
        {
            lockedMenu = menu;
        }

        public void UnlockMenu()
        {
            lockedMenu = null;

            // If there are queued menus, try to open the next one
            if (menuLoadQueue.Count > 0)
            {
                var next = menuLoadQueue[0];
                menuLoadQueue.RemoveAt(0);
                Open(next);
            }
        }

        public static void OpenMenu(BaseMenu newActiveMenu)
        {
            Instance.Open(newActiveMenu);
        }

        private void Open(BaseMenu newActiveMenu)
        {
            // If an animation is currently running, queue the request so it won't be lost
            if (menuAnimatorOut != null && menuAnimatorOut.IsRunning)
            {
                menuLoadQueue.Add(newActiveMenu);
                return;
            }

            // If locked and the requested menu is not the locked menu, queue it instead of opening immediately
            if (lockedMenu != null && newActiveMenu != lockedMenu)
            {
                menuLoadQueue.Add(newActiveMenu);
                return;
            }

            // If trying to open a static menu that may have been disposed, re-create static menus
            if ((newActiveMenu == Resources.Res.HomeMenu || newActiveMenu == Resources.Res.SettingsMenu)
                && (Resources.Res.HomeMenu == null || Resources.Res.HomeMenu.UiObjects == null || Resources.Res.HomeMenu.UiObjects.Count == 0))
            {
                try { Resources.Res.CreateStaticMenus(); }
                catch { }

                // Make sure it points to the newly created static menu instance
                if (newActiveMenu == Resources.Res.HomeMenu) newActiveMenu = Resources.Res.HomeMenu;
                if (newActiveMenu == Resources.Res.SettingsMenu) newActiveMenu = Resources.Res.SettingsMenu;
            }

            SetActiveMenu(newActiveMenu);
        }

        // Added optional parameter to open a specific menu after the overlay timeout
        public static void OpenOverlayMenu(BaseMenu overlayMenu, float duration = 5f, BaseMenu menuToOpenAfter = null)
        {
            if (Instance == null) return;

            // If overlay already exists, replace it
            Instance.SetOverlay(overlayMenu, duration, menuToOpenAfter);
        }

        // Instance method to handle overlay logic
        private void SetOverlay(BaseMenu overlayMenu, float duration, BaseMenu menuToOpenAfter)
        {
            // Cancel any existing overlay
            if (overlayCts != null)
            {
                try { overlayCts.Cancel(); } catch { }
                overlayCts.Dispose();
                overlayCts = null;
            }

            overlayCts = new CancellationTokenSource();

            // Lock menu immediately
            LockMenu(overlayMenu);

            // Open overlay instantly
            QueueOpenMenu(overlayMenu);

            if (duration <= 0f) return; // manual close only

            // Schedule unlock and next menu after duration asynchronously (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    int totalMs = (int)(duration * 1000);
                    int waited = 0;
                    const int step = 100;

                    while (waited < totalMs && !(overlayCts?.IsCancellationRequested ?? true))
                    {
                        int delay = Math.Min(step, totalMs - waited);
                        await Task.Delay(delay).ConfigureAwait(false);
                        waited += delay;
                    }

                    if (!(overlayCts?.IsCancellationRequested ?? true))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            // Only unlock if overlay is still active
                            if (ActiveMenu == overlayMenu)
                            {
                                UnlockMenu();

                                if (menuToOpenAfter != null)
                                    QueueOpenMenu(menuToOpenAfter);
                            }
                        });
                    }
                }
                finally
                {
                    try { overlayCts?.Dispose(); } catch { }
                    overlayCts = null;
                }
            });
        }

        static Thread overlayThread;
        // cooperative cancellation flag to avoid Thread.Interrupt
        static volatile bool overlayCancelled = false;

        public static void CloseOverlay()
        {
            if (Instance == null) return;

            if (Instance.overlayCts != null)
            {
                try { Instance.overlayCts.Cancel(); } catch { }
                Instance.overlayCts.Dispose();
                Instance.overlayCts = null;
            }

            // Unlock immediately so queued menus open
            Instance.UnlockMenu();
        }

        // Updated to accept menuToOpenAfter. If provided, that menu will be opened after the overlay timeout
        // Modified so the overlay thread only restores/opens menus if the overlay is still the active menu,
        // preventing it from overriding menus opened while the overlay was visible.
        public void OpenOverlay(BaseMenu newActiveMenu, float time, BaseMenu menuToOpenAfter)
        {
            // If an overlay is already running, ignore
            if (overlayThread != null && overlayThread.IsAlive) return;

            overlayCancelled = false;
            overlayMenu = newActiveMenu;
            overlayNextMenu = menuToOpenAfter;

            overlayThread = new Thread(() =>
            {
                // Lock the overlay menu
                Application.Current?.Dispatcher.Invoke(() => LockMenu(overlayMenu));

                // Open overlay menu on UI thread
                Application.Current?.Dispatcher.Invoke(() => QueueOpenMenu(overlayMenu));

                int timeMillis = (int)(time * 1000);
                int waited = 0;
                const int step = 100;

                while (waited < timeMillis)
                {
                    if (overlayCancelled) break;

                    int sleep = Math.Min(step, timeMillis - waited);
                    try { Thread.Sleep(sleep); } catch { }

                    waited += sleep;
                }

                // Overlay finished or cancelled
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Only unlock if overlay is still active
                    if (ActiveMenu == overlayMenu)
                    {
                        // Open the intended next menu
                        if (overlayNextMenu != null)
                            QueueOpenMenu(overlayNextMenu);
                    }

                    UnlockMenu();

                    // Clear references
                    overlayCancelled = false;
                    overlayThread = null;
                    overlayMenu = null;
                    overlayNextMenu = null;
                }));
            });

            overlayThread.IsBackground = true;
            overlayThread.Start();
        }

        static List<BaseMenu> menuLoadQueue = new List<BaseMenu>();

        Animator menuAnimatorOut;

        public void Update(float deltaTime)
        {
            if (menuAnimatorOut != null)
                menuAnimatorOut.Update(deltaTime);
        }

        private void SetActiveMenu(BaseMenu newActiveMenu)
        {
            if (menuAnimatorOut != null && menuAnimatorOut.IsRunning) return;
            onMenuChange?.Invoke(activeMenu, newActiveMenu);

            menuAnimatorOut = new Animator(300, 1);

            RendererMain.Instance.blurOverride = 35f;

            if (activeMenu != null)
            {
                try
                {
                    // give menu a chance to stop audio/threads/etc
                    activeMenu.OnDeload();
                }
                catch { }

                try
                {
                    // Avoid disposing static menus created in Res to prevent restoring disposed instances later
                    bool isStaticResMenu = (activeMenu == Resources.Res.HomeMenu || activeMenu == Resources.Res.SettingsMenu);
                    if (!isStaticResMenu)
                    {
                        // fully dispose the previous menu to destroy UIObjects and unsubscribe events
                        activeMenu.Dispose();
                    }
                }
                catch { }
            }

            activeMenu = newActiveMenu;

            menuAnimatorOut.onAnimationUpdate += (t) =>
            {
                float easedTime = Easings.EaseOutCubic(t);
                float easedTime2 = Easings.EaseOutQuint(t);
                float blurSize = Mathf.Lerp(35f, 0f, easedTime);
                float alpha = Mathf.Lerp(0f, 1f, easedTime2);

                var canvasSize = Vec2.lerp(Vec2.one * 0.7f, Vec2.one, easedTime2);

                RendererMain.Instance.blurOverride = blurSize;
                RendererMain.Instance.alphaOverride = alpha;
                RendererMain.Instance.scaleOffset = canvasSize;
            };

            menuAnimatorOut.onAnimationEnd += () =>
            {
                LoadMenuEnd();
            };

            menuAnimatorOut.Start();
        }

        void LoadMenuEnd()
        {
            onMenuChangeEnd?.Invoke(activeMenu);

            if (menuLoadQueue.Count != 0)
            {
                var queueObj = menuLoadQueue[0];

                if (queueObj == activeMenu)
                {
                    menuLoadQueue.Remove(queueObj);
                    return;
                }
                else OpenMenu(queueObj);

                menuLoadQueue.Remove(queueObj);
            }

            RendererMain.Instance.blurOverride = 0f;
            RendererMain.Instance.alphaOverride = 1f;
            RendererMain.Instance.scaleOffset = Vec2.one;

            menuAnimatorOut = null;
        }

        public void QueueOpenMenu(BaseMenu menu)
        {
            if (menuAnimatorOut == null) OpenMenu(menu);
            else
            {
                menuLoadQueue.Add(menu);
            }
        }
    }
}
