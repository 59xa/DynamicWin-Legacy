using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus.SettingsMenuObjects;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.Utils;
using MathNet.Numerics.Optimization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DynamicWin.UI.UIElements.IslandObject;

/*
 *
 *  Overview:
 *      - Opens a menu indicating to user that the application will perform a restart
 *      - to install a new update from the target repository.
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    27 November 2025
 *  Last Modified:          28 November 2025
 *
 */

namespace DynamicWin.UI.Menu.Menus
{
    public class UpdaterMenu : BaseMenu
    {
        // The update object received from RendererMain
        internal AppVersion update { get; }
        private string latestVersion;
        private string releaseStream;

        // UI elements
        DWText subUpdaterText;
        DWText versionText;
        string subUpdaterTextFormatToString;
        TimeSpan countdown = TimeSpan.FromSeconds(5);

        private bool countdownStarted = false;
        private CancellationTokenSource? cts;

        // Progress bar tracking
        private DWProgressBar? countdownBar;
        private float countdownProgress = 1f; // 1 = full, 0 = empty

        // Whether the provided update info is valid
        private bool validUpdate = false;

        /// <summary>
        /// Constructor: now receives both AppVersion and remoteVersion
        /// </summary>
        /// <param name="update">The update info fetched from remote</param>
        /// <param name="remoteVersion">The string version of the remote release</param>
        public UpdaterMenu(AppVersion update)
        {
            this.update = update;

            // Validate update object
            validUpdate = (update != null) && !string.IsNullOrWhiteSpace(update.version) && !string.IsNullOrWhiteSpace(update.downloadUri);

            latestVersion = DisplayVersion();
            releaseStream = DisplayReleaseStream();

#if DEBUG
            Debug.WriteLine($"[UPDATER] Constructor: validUpdate = {validUpdate}, update.version = {update?.version}");
            Debug.WriteLine($"[UPDATER] latestVersion = {latestVersion}");
#endif
        }

        private string DisplayVersion()
        {
            if (update != null && !string.IsNullOrWhiteSpace(update.version))
                return update.version;
            else
                return "unknown";
        }

        private string DisplayReleaseStream()
        {
            if (update != null && !string.IsNullOrWhiteSpace(update.releaseStream))
                return update.releaseStream;
            else
                return "unknown";
        }

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            var objects = base.InitializeMenu(island);

            RendererMain.Instance.MainIsland.hidden = false;

            // Title text
            var updaterText = new DWText(island, "An update is available.", new Vec2(0, -10), UIAlignment.Center)
            {
                Font = Res.SatoshiBold,
                TextSize = 18,
                Color = Theme.TextMain
            };

            // Countdown progress bar
            countdownBar = new DWProgressBar(island, new Vec2(0, 10), new Vec2(200, 5f), UIAlignment.Center);

#if DEBUG
            Debug.WriteLine($"[UPDATER] Version display: {latestVersion}");
#endif

            versionText = new DWText(island, $"Fetching latest version...", new Vec2(0, -40), UIAlignment.Center)
            {
                TextSize = 11
            };
            objects.Add(versionText);

            // Countdown info text
            subUpdaterText = new DWText(island, validUpdate ? $"DynamicWin will close in {countdown.Seconds}..." : "Update information unavailable.", new Vec2(0, -25), UIAlignment.BottomCenter)
            {
                TextSize = 12
            };

            // Add UI objects
            objects.Add(updaterText);
            objects.Add(countdownBar);
            //objects.Add(versionText); // intentionally commented out in layout
            objects.Add(subUpdaterText);

            return objects;
        }

        public override void Update()
        {
            base.Update();

            MainForm.Instance?.UpdateTrayButtons();

            versionText.Text = $"New version: {latestVersion} ({releaseStream})";

            // Start countdown once (only for valid update)
            if (!countdownStarted && validUpdate)
            {
                countdownStarted = true;
                cts = new CancellationTokenSource();
                _ = StartCountdownAsync(countdown, cts.Token);

#if DEBUG
                Debug.WriteLine("[UPDATER] Countdown started.");
#endif
            }

            // Update the visible text each frame
            if (!string.IsNullOrEmpty(subUpdaterTextFormatToString))
            {
                subUpdaterText.Text = subUpdaterTextFormatToString;
            }

            // Update progress bar value
            if (countdownBar != null)
            {
                countdownBar.value = countdownProgress;
            }
        }

        /// <summary>
        /// Performs an asynchronous countdown for the specified duration, updating progress and status text until
        /// completion or cancellation.
        /// </summary>
        private async Task StartCountdownAsync(TimeSpan duration, CancellationToken token)
        {
            if (!validUpdate)
                return; // nothing to do

            DateTime endTime = DateTime.Now.Add(duration);

            try
            {
                while (DateTime.Now < endTime && !token.IsCancellationRequested)
                {
                    TimeSpan remaining = endTime - DateTime.Now;

                    int secondsLeft = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

                    subUpdaterTextFormatToString = $"DynamicWin will close in {secondsLeft}...";

                    // Compute progress as fraction [0..1]
                    float progress = (float)(remaining.TotalSeconds / Math.Max(1.0, duration.TotalSeconds));
                    countdownProgress = Math.Clamp(progress, 0f, 1f);

                    // Update ~10 times per second
                    try { await Task.Delay(100, token); } catch (TaskCanceledException) { break; }
                }

                if (!token.IsCancellationRequested)
                {
                    subUpdaterTextFormatToString = "DynamicWin will close in 0...";
                    countdownProgress = 0f;

                    RendererMain.Instance.MainIsland.hidden = true;

                    Updater updater = new Updater();

                    try
                    {
                        string zipPath = await updater.DownloadUpdate(update);
                        updater.LaunchUpdater(zipPath);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled - do nothing
                    }
                    catch (Exception ex)
                    {
                        // Show error to user instead of throwing
                        subUpdaterTextFormatToString = "Update failed: " + ex.Message;

#if DEBUG
                        Debug.WriteLine("UpdaterMenu: download/launch failed: " + ex);
#endif
                        // Unlock island so user can continue using app
                        RendererMain.Instance.MainIsland.hidden = false;

                        // After a brief delay, return to Home Menu if overlay is active
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(new Action(() =>
                            {
                                MenuManager.OpenMenu(Res.HomeMenu);
                            }));
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("UpdaterMenu: countdown error: " + ex);
#endif
                // Ensure we don't propagate exceptions to UI thread
                try
                {
                    subUpdaterTextFormatToString = "Update interrupted.";
                    RendererMain.Instance.MainIsland.hidden = false;
                }
                catch { }
            }
        }

        public override Vec2 IslandSize()
        {
            Vec2 size = new Vec2(300, 150);
            return size;
        }

        public override void OnDeload()
        {
            base.OnDeload();
            // Cancel countdown when menu is being unloaded
            if (cts != null && !cts.IsCancellationRequested)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
                cts = null;
            }
        }

        public override void OnDispose()
        {
            base.OnDispose();
            if (cts != null && !cts.IsCancellationRequested)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
                cts = null;
            }
        }

        public override Col IslandBorderColor()
        {
            IslandMode mode = Settings.IslandMode; // Reads either Island or Notch as value
            if (mode == IslandMode.Island) return new Col(0.5f, 0.5f, 0.5f);
            else return new Col(0, 0, 0, 0); // Render transparent if island mode is Notch
        }
    }
}
