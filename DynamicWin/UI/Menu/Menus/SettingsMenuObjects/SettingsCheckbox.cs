using DynamicWin.UI.UIElements;
using DynamicWin.Utils;

namespace DynamicWin.UI.Menu.Menus.SettingsMenuObjects
{
    internal class SettingsCheckbox : DWCheckbox
    {
        public SettingsCheckbox(
            UIObject? parent,
            string text,
            Vec2 position,
            Vec2 size,
            Action? clickCallback,
            UIAlignment alignment = UIAlignment.TopLeft)
            : base(parent, text, position, size, clickCallback, alignment)
        {
        }
    }
}
