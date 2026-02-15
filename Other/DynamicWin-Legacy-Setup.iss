#ifndef DWAppVersion
  #define DWAppVersion "v1.6.0r"
#endif

#ifndef Platform
  #define Platform "x64"
#endif

[Setup]
AppName=DynamicWin
AppId={{B0A1270C-5C23-42CD-A017-30F4B87C141E}}
AppVersion={#DWAppVersion}
AppPublisher=59xa
AppPublisherURL=https://github.com/59xa/DynamicWin-Legacy
AppSupportURL=https://github.com/59xa/DynamicWin-Legacy
AppUpdatesURL=https://github.com/59xa/DynamicWin-Legacy
DefaultDirName={autopf}\DynamicWin
OutputBaseFilename=DynamicWinSetup-{#Platform}
UninstallDisplayIcon=..\publish\Release-{#Platform}\DynamicWin.exe
Compression=lzma2
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
PrivilegesRequiredOverridesAllowed=dialog
DisableDirPage=auto
DisableProgramGroupPage=yes
ChangesAssociations=no
ShowLanguageDialog=yes
LicenseFile="..\LICENSE"
WizardStyle=modern
WizardImageFile=compiler:WizClassicImage.bmp
WizardSmallImageFile="InstallerIcon\setup.bmp"
SetupIconFile="InstallerIcon\setup.ico"

[Files]
Source: "..\publish\Release-{#Platform}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\Release-{#Platform}\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists('..\publish\Release-{#Platform}\runtimes')


[Icons]
Name: "{autoprograms}\DynamicWin"; Filename: "{app}\DynamicWin.exe"; 
Name: "{autodesktop}\DynamicWin"; Filename: "{app}\DynamicWin.exe"; Tasks: desktopicon; 

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; 

[CustomMessages]
english.NameAndVersion=%1 version %2
english.AdditionalIcons=Additional shortcuts:
english.CreateDesktopIcon=Create a &desktop shortcut
english.CreateQuickLaunchIcon=Create a &Quick Launch shortcut
english.ProgramOnTheWeb=%1 on the Web
english.UninstallProgram=Uninstall %1
english.LaunchProgram=Launch %1
english.AssocFileExtension=&Associate %1 with the %2 file extension
english.AssocingFileExtension=Associating %1 with the %2 file extension...
english.AutoStartProgramGroupDescription=Startup:
english.AutoStartProgram=Automatically start %1
english.AddonHostProgramNotFound=%1 could not be located in the folder you selected.%n%nDo you want to continue anyway?

[Languages]
; These files are stubs
; To achieve better results after recompilation, use the real language files
Name: "english"; MessagesFile: "compiler:Default.isl"; 
