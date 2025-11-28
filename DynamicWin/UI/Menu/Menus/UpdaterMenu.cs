using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.Menu.Menus.SettingsMenuObjects;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.Utils;
using MathNet.Numerics.Optimization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/*
 *
 *  Overview:
 *      - Opens a menu indicating to user that the application will perform a restart
 *      - to install a new update from the target repository.
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    27 November 2025
 *  Last Modified:          27 November 2025
 *
 */

namespace DynamicWin.UI.Menu.Menus
{
    public class UpdaterMenu : BaseMenu
    {
        private IslandObject islandObject;

        DWText subUpdaterText;
        string subUpdaterTextFormatToString;
        TimeSpan countdown = TimeSpan.FromSeconds(5);

        private bool countdownStarted = false;
        private CancellationTokenSource? cts;

        // Added fields to track and display progress
        private DWProgressBar? countdownBar;
        private float countdownProgress = 1f; // 1 = full, 0 = empty

        public UpdaterMenu()
        { }

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            var objects = base.InitializeMenu(island);

            var updaterText = new DWText(island, "Update available!", new Vec2(0, -10), UIAlignment.Center)
            {
                Font = Res.SatoshiBold,
                TextSize = 18,
                Color = Theme.TextMain
            };

            // Store as a field so it can be updated from Update()
            countdownBar = new DWProgressBar(island, new Vec2(0, 10), new Vec2(150, 5f), UIAlignment.Center);

            subUpdaterText = new DWText(island, $"DynamicWin will close in {countdown.Seconds}...", new Vec2(0, -25), UIAlignment.BottomCenter)
            {
                TextSize = 12
            };

            objects.Add(updaterText);
            objects.Add(countdownBar);
            objects.Add(subUpdaterText);

            return objects;
        }

        public override void Update()
        {
            base.Update();

            // Start countdown once
            if (!countdownStarted)
            {
                countdownStarted = true;
                cts = new CancellationTokenSource();
                _ = StartCountdownAsync(countdown, cts.Token);
            }

            // Update the visible text each frame (thread-safe string swap)
            if (!string.IsNullOrEmpty(subUpdaterTextFormatToString))
            {
                subUpdaterText.Text = subUpdaterTextFormatToString;
            }

            // Update progress bar value from the last computed progress (main/UI thread)
            if (countdownBar != null)
            {
                countdownBar.value = countdownProgress;
            }
        }

        /// <summary>
        /// Performs an asynchronous countdown for the specified duration, updating progress and status text until
        /// completion or cancellation.
        /// </summary>
        /// <remarks>If the countdown completes without cancellation, the application will close
        /// automatically after a brief delay. Progress and status text are updated periodically throughout the
        /// countdown.</remarks>
        /// <param name="duration">The total length of time to count down before completing the operation.</param>
        /// <param name="token">A cancellation token that can be used to request cancellation of the countdown before it completes.</param>
        /// <returns>A task that represents the asynchronous countdown operation.</returns>
        private async Task StartCountdownAsync(TimeSpan duration, CancellationToken token)
        {
            DateTime endTime = DateTime.Now.Add(duration);

            while (DateTime.Now < endTime && !token.IsCancellationRequested)
            {
                TimeSpan remaining = endTime - DateTime.Now;

                int secondsLeft = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

                subUpdaterTextFormatToString = $"DynamicWin will close in {secondsLeft}...";

                // compute progress as fraction [0..1]
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

                await Task.Delay(1000, token);
                Environment.Exit(0);
                //Updater.LaunchUpdater();
            }
        }

        public override Vec2 IslandSize()
        {
            Vec2 size = new Vec2(250, 150);

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
            return new Col(0.5f, 0.5f, 0.5f);
        }
    }
}
