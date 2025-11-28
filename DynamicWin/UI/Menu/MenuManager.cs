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
        public static void OpenOverlayMenu(BaseMenu newActiveMenu, float time = 5f, BaseMenu menuToOpenAfter = null)
        {
            Instance.OpenOverlay(newActiveMenu, time, menuToOpenAfter);
        }

        static Thread overlayThread;

        public static void CloseOverlay()
        {
            if (overlayThread != null)
            {
                try
                {
                    if (overlayThread.IsAlive) overlayThread.Interrupt();
                }
                catch { }
                finally
                {
                    overlayThread = null;
                }
            }
        }

        // Updated to accept menuToOpenAfter. If provided, that menu will be opened after the overlay timeout
        // Modified so the overlay thread only restores/opens menus if the overlay is still the active menu,
        // preventing it from overriding menus opened while the overlay was visible.
        private void OpenOverlay(BaseMenu newActiveMenu, float time, BaseMenu menuToOpenAfter)
        {
            // If an overlay thread is already running, don't start another one
            if (overlayThread != null && overlayThread.IsAlive) return;

            overlayThread = new Thread(() =>
            {
                BaseMenu lastMenu = activeMenu;
                BaseMenu overlayMenu = newActiveMenu;

                // Lock the menu so other code doesn't override it
                try
                {
                    Application.Current?.Dispatcher.Invoke(new Action(() =>
                    {
                        LockMenu(overlayMenu);
                    }));
                }
                catch { }

                // Synchronously open the overlay on the UI thread to avoid races
                try
                {
                    Application.Current?.Dispatcher.Invoke(new Action(() =>
                    {
                        QueueOpenMenu(newActiveMenu);
                    }));
                }
                catch { }

                int timeMillis = (int)(time * 1000);

                try
                {
                    Thread.Sleep(timeMillis);
                }
                catch (ThreadInterruptedException e)
                {
                    // On interrupt, only restore the previous menu if the overlay is still active
                    try
                    {
                        Application.Current?.Dispatcher.Invoke(new Action(() =>
                        {
                            if (activeMenu == overlayMenu && lastMenu != null)
                            {
                                QueueOpenMenu(lastMenu);
                            }

                            // Unlock regardless
                            UnlockMenu();
                        }));
                    }
                    catch { }
                    finally
                    {
                        // clear thread reference
                        overlayThread = null;
                    }

                    return;
                }

                // When time elapsed, only open the specified menu if the overlay is still active.
                try
                {
                    Application.Current?.Dispatcher.Invoke(new Action(() =>
                    {
                        if (activeMenu == overlayMenu)
                        {
                            if (menuToOpenAfter != null)
                            {
                                QueueOpenMenu(menuToOpenAfter);
                            }
                            else if (lastMenu != null)
                            {
                                QueueOpenMenu(lastMenu);
                            }
                        }

                        // Unlock after finishing
                        UnlockMenu();
                    }));
                }
                catch { }
                finally
                {
                    overlayThread = null;
                }

            });

            // mark as background so it won't block shutdown
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
