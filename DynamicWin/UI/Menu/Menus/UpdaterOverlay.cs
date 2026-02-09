using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using static DynamicWin.UI.UIElements.IslandObject;

/*
 *
 *  Overview:
 *      - Small overlay to indicate process of fetching application updates if made available.
 *      - Either returns the user to HomeMenu or UpdaterMenu depending on the outcome.
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    27 November 2025
 *  Last Modified:          27 November 2025
 *
 */

namespace DynamicWin.UI.Menu.Menus
{
    internal class UpdaterOverlay : BaseMenu
    {
        DWText overlayText;

        public static float timerUntilClose = 0f;

        static UpdaterOverlay instance;

        float islandScale = 1.50f;

        public UpdaterOverlay()
        {
            instance = this;
            timerUntilClose = 0f;
        }

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            var objects = base.InitializeMenu(island);

            overlayText = new DWText(island, "Checking for updates...", new Vec2(0, 0), UIAlignment.Center)
            {
                TextSize = 14,
                Font = Res.SFProBold
            };

            objects.Add(overlayText);

            return objects;
        }

        public override void Update()
        {
            base.Update();

            islandScale = Mathf.Lerp(islandScale, 1f, 5f * RendererMain.Instance.DeltaTime);
        }

        public override Vec2 IslandSize()
        {
            return new Vec2(250, 50) * islandScale;
        }

        public override Col IslandBorderColor()
        {
            IslandMode mode = Settings.IslandMode; // Reads either Island or Notch as value
            if (mode == IslandMode.Island) return new Col(0.5f, 0.5f, 0.5f);
            else return new Col(0, 0, 0, 0); // Render transparent if island mode is Notch
        }
    }
}
