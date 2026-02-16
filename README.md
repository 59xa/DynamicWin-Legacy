# DynamicWin Legacy

<p align="center">
  <img src="https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white">
  <a href="https://creativecommons.org/licenses/by-sa/4.0/"><img src="https://img.shields.io/static/v1?label=License&message=CC+BY-SA+4.0&color=%23c49b04&style=for-the-badge"></a>
  <a href="https://discord.gg/UHFuqB9NqR"><img src="https://dcbadge.limes.pink/api/server/https://discord.gg/UHFuqB9NqR)](https://discord.gg/UHFuqB9NqR"></a>
  <a href="https://github.com/59xa/DynamicWin-Legacy/actions/workflows/build.yml"><img alt="GitHub Actions Workflow Status" src="https://img.shields.io/github/actions/workflow/status/59xa/DynamicWin-Legacy/.github%2Fworkflows%2Fbuild.yml?style=for-the-badge"></a>
</p>

<p align="center">
  <img src="ReadmeFiles/IslandGif-1_Volume.gif" style="border-radius:15px" alt="animated" width="1000" height="auto" />
</p>

<p xmlns:cc="http://creativecommons.org/ns#" xmlns:dct="http://purl.org/dc/terms/"><a property="dct:title" rel="cc:attributionURL" href="https://github.com/59xa/DynamicWin-Legacy">DynamicWin Legacy</a> by <a rel="cc:attributionURL dct:creator" property="cc:attributionName" href="https://github.com/FlorianButz">Florian Butz</a> is maintained by <a rel="cc:attributionURL dct:maintainer" property="cc:attributionName" href="https://github.com/59xa">59xa</a> and is licenced under <a href="https://creativecommons.org/licenses/by-sa/4.0/?ref=chooser-v1" target="_blank" rel="license noopener noreferrer" style="display:inline-block;">CC BY-SA 4.0<img style="height:22px!important;margin-left:3px;vertical-align:text-bottom;" src="https://mirrors.creativecommons.org/presskit/icons/cc.svg?ref=chooser-v1" alt=""><img style="height:22px!important;margin-left:3px;vertical-align:text-bottom;" src="https://mirrors.creativecommons.org/presskit/icons/by.svg?ref=chooser-v1" alt=""><img style="height:22px!important;margin-left:3px;vertical-align:text-bottom;" src="https://mirrors.creativecommons.org/presskit/icons/sa.svg?ref=chooser-v1" alt=""></a></p>

> [!NOTE]
> This repository holds the legacy code and releases for DynamicWin developed by [FlorianButz](https://github.com/FlorianButz), and is maintained by [59xa](https://github.com/59xa). Please do not report issues and missing features in this repository regarding version 2.0 as this repository only accepts version 1.0 issues. For version 2.0 releases, click [here](https://github.com/FlorianButz/DynamicWin).

> [!WARNING]
> This is a legacy application that is being maintained by one developer. Do not expect most features to be fixed whatsoever. However, this does not mean groundbreaking issues and feature requests will be turned down immediately. Open an issue ticket for a new feature or an existing issue, they will be added/fixed eventually. 

### What is it?
A [Dynamic Island](https://support.apple.com/de-de/guide/iphone/iph28f50d10d/ios) inspired Windows App that brings in a bunch of features like widgets or a file tray that works like a clipboard.
Similar to dynamic notches that you can find on macOS like [NotchNook](https://lo.cafe/notchnook), this application brings the concept on Windows devices to life.

### Implementation and build
This application is developed using C# for the logic, Windows Presentation Foundation (WPF) for windowing, and [SkiaSharp](https://github.com/mono/SkiaSharp) to display the graphical interface.
To build this project, ensure that you have the latest version of **`.NET 9.0`** installed on your environment.

To get started:
```bash
git pull https://github.com/59xa/DynamicWin-Legacy.git
```

### Future plans/continued support:
- While [version 2.0](https://github.com/FlorianButz/DynamicWin) of this software has been made public, the legacy codebase will continue to exist and maintained by me until FlorianButz decides to pull the legacy support.
- This repository is no longer connected to the original repository's fork network. Please report your issues regarding V2 [here](https://github.com/FlorianButz/DynamicWin).
- V1 (this repository) will co-exist with V2, and will not serve as a replacement but an alternative for users to use.
- Your support truly means a lot to keep maintaining DynamicWin Legacy. Keep an eye out whenever a new release comes out.
- Feel free to contribute to this project as you wish. Open any issues on the issues page if you encounter any bugs.
<br>

**Quick disclaimer**: The codebase is currently structured terribly and almost un-maintainable. Codebase refactoring is currently in the works starting with **`v1.4.0b`**.

# Features
> [!NOTE]
> Only checkboxed features are currently available. Unimplemented features will be introduced as time passes.

DynamicWin-Legacy has a variety of features, currently including: <br>

## Shortcuts
- [x] `Ctrl + Win` Will hide the island (or show it again).
- [ ] ~~`Shift + Win` Will open a quick search menu.~~ <sub>(Please consider using an alternative such as [Powertoys Run](https://learn.microsoft.com/en-us/windows/powertoys/run))</sub>

## Big Widgets
- [x] Media Playback Widget
- [x] Timer Widget
- [x] Weather Widget
- [x] Shortcuts Widget <sub>(Can be configured to open a file, e.g. Shortcut, .EXE or any other filetype.)</sub>
- [ ] Calendar Widget

## Small Widgets
- [x] Time Display
- [x] Music Visualizer
- [x] Device Usage Detector <sub>(Indicates if camera / microphone is in use)</sub>
- [x] Power State Display <sub>(Shows battery in form of icons. If no battery is found it shows a connector icon instead)</sub>
- [x] Timer <sub>(Displaying current running timer)</sub>
- [x] CPU/GPU Usage Display

## File Distribution & Management <br>

<p align="center">
  <img style="border-radius:15px" src="ReadmeFiles/IslandGif-2_Tray.gif" alt="animated" width="1000" height="auto" />
</p>

- [x] File Tray <sub><br>
Files can be dragged over the island to add them to the file tray. The tray can be accessed when hovering over the island and clicking on the 'Tray' button. The files are stored until they are dragged out again. They can also be removed by selecting the file and right clicking. A context menu will popup and you can click on - **"Remove Selected Files"** or **"Remove Selected Files"** to copy the files.</sub> <br>
- [ ] SnapDrop API implementation<br><sub>
While this feature is low-priority, please expect the introduction of this feature in the near future.
</sub>


> [!WARNING]
> If you are using the file tray to import files in to an app (e.g. After Effects) make sure to not remove the files from the tray. Apps that only copy a link to the file will be lost after you remove the file from the tray.

## Spotify Integration

<p><img align="left" height="150" style="border-radius:15px; margin:0 25 0 0" src="ReadmeFiles/IslandGif-3_Spotify.gif">The Media Playback Widget automatically detects when an instance of the Spotify app is running (Desktop version only). It will display the current playing song name and the artist. Login to the Spotify service on the app is <b>not</b> required.</p>
<br><br><br>

## Mod Support
**We support mod extensions. You can add your own small widgets and big widgets by creating a custom extension.** <br>
Loading an extension from someone else is very simple. You just need to drag the **Mod.dll** file in to the *Extensions* folder that is located in the `%appdata%/DynamicWin` directory. 

> [!WARNING]
> **Please never load a mod that is not tested to be safe.**

Mods may contain malicious code that can mess up your system, so always check a mod's source code or let a trustworthy person check it for you.

## Custom Themes

<p align="center">
  <img style="border-radius:15px; margin:15px 0 15px 0" src="ReadmeFiles/Themes.png" alt="animated" width="100%" height="auto" />
</p>

> [!NOTE]
> Custom themes are not the main priority for this repository, but will remain supported for use. Visit Florian's Discord server to get access to more themes like the ones shown from above.

You can use the built-in dark / light theme. You can also create custom themes that fit your liking by going to the `%appdata%/DynamicWin/Theme.json` file. After editing the colors you need to select the `Custom` theme option in the settings. If you already did that, you will need to go back to the settings and click on it again. Otherwise you would have to restart the app.
<br>
This is an example of a color:
`"IslandColor": "#000000"`
<br>
The hex code is structured this way: `#rrggbb`. If you want to change the alpha of the color, it is **always** at the start of the code. `#aarrggbb`.

# Known Issues
The performance might not be the best. Slowly expect codebase optimisations starting with **`v1.4.0b`**. <br><br>

The app might suddenly disappear and upon trying to reopen it a message box will tell you that only one instance of the app can run at the same time. To fix this, open task manager and find the process `DynamicWin`. Kill it and start the app again. <br><br>

Too fast interactions might confuse the animation system and will result in an empty menu. To fix this, usually moving the mouse away from the island and then over it again will fix it.

# Modding DynamicWin (making Extensions)
- While extension support and compatibility is not a focus for the maintainer, users are still able to make their own extensions as needed.
- Read [MODDING.md](MODDING.md) for more information on how to get started.
