using DynamicWin.Main;
using DynamicWin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.UI.UIElements.Custom
{
    public class DWSlider(UIObject? parent, Vec2 position, Vec2 size, UIAlignment alignment = UIAlignment.TopCenter) : DWProgressBarEx(parent, position, size, alignment)
    {
        public Action<float> clickCallback;
        float valueBefore = 0f;

        public override void OnMouseDown()
        {
            base.OnMouseDown();

            valueBefore = Value;
        }

        public override void OnGlobalMouseUp()
        {
            base.OnGlobalMouseUp();

            if(valueBefore != Value)
                clickCallback?.Invoke(Value);
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (IsMouseDown)
                Value = Mathf.Clamp(Mathf.Remap(RendererMain.CursorPosition.X - Position.X, 0, Size.X, 0, 1),
                    0.05f, 1);
        }
    }
}
