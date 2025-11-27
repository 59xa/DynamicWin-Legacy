using DynamicWin.Main;
using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.UI.Menu
{
    public class BaseMenu : IDisposable
    {
        private List<UIObject> uiObjects = new List<UIObject>();

        public List<UIObject> UiObjects { get { return uiObjects; } }

        public BaseMenu()
        {
            uiObjects = InitializeMenu(RendererMain.Instance.MainIsland);
        }

        public virtual Vec2 IslandSize() { return new Vec2(200, 45); }
        public virtual Vec2 IslandSizeBig() { return IslandSize(); }

        public virtual Col IslandBorderColor() { return Col.Transparent; }

        public virtual List<UIObject> InitializeMenu(IslandObject island) { return new List<UIObject>(); }

        public virtual void Update() { }

        public virtual void OnDeload() { }

        /// <summary>
        /// Called when menu is being permanently unloaded. Override to clean up menu-specific resources.
        /// </summary>
        public virtual void OnDispose() { }

        public void Dispose()
        {
            // run per-menu disposal hook first
            try { OnDispose(); } catch { }

            // Destroy each uiObject (unsubscribes events recursively)
            for (int i = uiObjects.Count - 1; i >= 0; i--)
            {
                try
                {
                    uiObjects[i].DestroyCall();
                }
                catch { }
            }

            uiObjects.Clear();
        }
    }
}
