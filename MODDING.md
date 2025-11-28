# Creating/modifying DynamicWin-Legacy with custom extensions

To create an extension you need an IDE like [Visual Studio 2026](https://visualstudio.microsoft.com/vs/community/).
- Create a new C# project of the type `Class Library`. Ensure that the target framework is **`.NET 9.0`**.
- It is required to add `DynamicWin.dll` and SkiaSharp DLLs as assembly dependencies to your project. [More information regarding this through here.](https://learn.microsoft.com/en-gb/visualstudio/ide/how-to-add-or-remove-references-by-using-the-reference-manager?view=vs-2022)
- Create a new C# class file if you haven't done so. Rename the class to something like `TestExtension`.
- All extensions must have a class that implements the `IDynamicWinExtension` interface.<br>

Here is an example of how the extension class should look like:

```cs
public class TestExtension : IDynamicWinExtension
{
    public string AuthorName => "59xa"; // The display name of the author (you) of the extension

    public string ExtensionName => "Test Extension"; // The dislpay name of the extension

    public string ExtensionID => "59xa.test"; // The ID of the extension

    public List<IRegisterableWidget> GetExtensionWidgets() // Returns all Widgets that are available in this extension
    {
        return new List<IRegisterableWidget>() { }; // Creates a list with all IRegisterableWidgets in this extension
    }

    public void LoadExtension() // Gets executed after DynamicWin has finished loading in all extensions and has created the window
    {
        System.Diagnostics.Debug.WriteLine(ExtensionName + " was loaded sucessfully."); // Debug text
    }
}
```

This is how you can initialise your first widget. Create a new class and call it how you want. `TestWidget` will be the name of this widget for the time being. The class has to implement from `WidgetBase` if it is a big widget. To make a small widget, extend the class from `SmallWidgetBase`.

```cs
public class TestWidget : WidgetBase
{
    DWText text; // Reference for the text object

    public TestWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment) // Overriding the constructor is essential.
    {
        text = new DWText(this, "This is a Text", Vec2.zero, UIAlignment.Center); // Creates a new text object
        AddLocalObject(text); // Adds an object to the UIObject (Widget)
    }

    public override void DrawWidget(SKCanvas canvas) // Gets called when the widget is drawn. Do not override 'Draw()' since it contains other important things.
    {
        var paint = GetPaint(); // Creates a new paint of the UIObject. Please only use this and don't create your own paint.
        paint.Color = Theme.Primary.Value(); // Sets the color of the paint to the current Primary color

        var rect = GetRect(); // Gets the rect / bounds of the widget

        canvas.DrawRoundRect(rect, paint); // Draws the rect to the screen. Only use 'DrawRoundRect()' when trying to draw the bounds of the UIObject.
    }
}
```

DynamicWin needs to know what widgets are in your extension. To register the widget, create a new class that implements from the `IRegisterableWidget` class. `RegisterTestWidget` will be used as the widget class name example.

```cs
public class RegisterTestWidget : IRegisterableWidget
{
    public bool IsSmallWidget => false; // Determins if the widget is supposed to be small or big (small widgets are displyed when the island is not hovered)

    public string WidgetName => "Test Widget"; // The display name of the widget

    public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) // Overriding the constructor is essential.
    {
        return new TestWidget(parent, position, alignment); // Needs to return the Widget that you are trying to register
    }
}
```

The extension is almost ready for use. Go back to your main extension class and add the `RegisterTestWidget` (or whatever you called it) class to the `GetExtensionWidgets()` function's return list.

```cs
public List<IRegisterableWidget> GetExtensionWidgets() // Returns all Widgets that are available in this extension
{
    return new List<IRegisterableWidget>() { new RegisterTestWidget() }; // Creates a list with all IRegisterableWidgets in this extension
}
```

Build the project, and open your project's output directory. Often times, it is located under `\bin\Debug\net9.0\` or `\bin\Release\net9.0\` and move **ONLY** the DLL file that has the name of your project in to the `%appdata%/DynamicWin/Extensions` folder. In this case, my output DLL is called `TestExtension.dll`. <br><br>
Run DynamicWin and test your extension. This was of course a very bare bones example.

> [!TIP]
> It's best to look at the widgets that are already in DynamicWin and learn from them.

<br>
Here is a set of current UIObjects that can be used to make the widget creation process easier.

## UIObject
  - Constructor
    - `UIObject? parent` is the parent UIObject, most times the widget itself.
    - `Vec2 position` is the position of the UIObject.
    - `Vec2 size` is the size of the UIObject.
    - `UIAlignment alignment` is an optional paramenter which determins where inside the parent the object's zero point is.
  - Important Methods
    - `SetActive(bool)` sets the active state of the UIObject with an animation.
    - `SilentSetActive(bool)` sets the active state of the UIObject without an animation.
    - `SKRoundRect GetRect()` returns the bounds of the UIObject (`SKRoundRect`);
    - `ContextMenu? CreateContextMenu()` is overriden if the UIObject is supposed to have a context (right click) menu using the WPF `ContextMenu` class.
    - `AddLocalObject(UIObject)` adds another UIObject inside the UIObject that called it. Parent is automatically set to the caller UIObject.
    - `DestroyLocalObject(UIObject obj)` removes the local UIObject.
    - `GetColor(Col)` takes in a color and returns the same color but with correct transparency. Please use it whenever you use custom colors or colors from the Theme class.
  - Important Fields / Properties
    - `IsHovering` returns true if the mouse is over the UIObject.
    - `IsMouseDown` returns true if the mouse is down over the UIObject. 
    - `Color` returns the color of the object.
    - `Position` returns the position of the object.
    - `RawPosition` returns the actual position of the object without the screen transformation (use this when setting the `Position` field).
    - `Anchor` is the anchor of the object. By default set to `(0.5f, 0.5f)` which means zero is in the middle of the UIObject.
    - `Size` returns the size of the object.
    - `IsEnabled` returns the active state of the UIObject.

## DWText
  - Constructor
    - `string text` is the text.
  - Important Methods
    - `SetText(string)` sets the text with an animation.
    - `SilentSetText(string)` sets the text without an animation.
  - Important Fields / Properties
    - `Font` is the Typeface of the text.
    - `TextBounds` are the bounds of the text.
    - `TextSize` is the size of the text.

## WTextImageButton
(Do not use. This is the base class of `DWTextButton`, `DWImageButton` and `DWTextImageButton`)
  - Constructor
    - `Action clickCallback` is an action that gets called on click.
  - Important Fields / Properties
    - `normalColor` is the color of the button when not hovered.
    - `hoverColor` is the color of the button when hovered.
    - `clickColor` is the color of the button when clicked.
    - `colorSmoothingSpeed` is the speed at which the color updates.
    - `normalScaleMulti` is the size of the button when not hovered.
    - `hoverScaleMulti` is the size of the button when hovered.
    - `normclickScaleMultialScaleMulti` is the size of the button when clicked.


## DWTextButton
  - Constructor
    - `string buttonText` is the text on the button.
  - Important Fields / Properties
    - `normalTextSize` is the size of the text when not hovered.

## DWImageButton
  - Constructor
    - `SKBitmap image` is the image on the button.
  - Important Fields / Properties
    - `imageScale` the scale multiplier of the image. Default is `0.85f`. `1f` would be the scale of the button.
    - `Image` the image of the button.
    
## DWTextImageButton
  - Constructor
    - `SKBitmap image` is the text on the button.
    - `string buttonText` is the text on the button.
    - `Action clickCallback` is an action that gets called on click.
  - Important Fields / Properties
    - `imageScale` the scale multiplier of the image. Default is `0.85f`. `1f` would be the scale of the button.
    - `normalTextSize` is the size of the text when not hovered.
    - `Image` the image of the button.

The `Res` class can be used to load in SKTypeface or SKBitmap (for DWText or DWImage).
For colors the `Col` class is used. When applying the color to the paint `Col.Value()` has to be called to convert to `SKColor`. <br>
Try to only use the colors available in the `Theme` class to provide color customizability to the user.
