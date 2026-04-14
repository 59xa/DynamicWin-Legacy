using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DynamicWin.Utils
{
    public class StartupShortcutManager
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] string pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] string pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] string pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] string pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        }

        private static string GetStartupFolderPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        private static string GetShortcutPath(string shortcutName)
        {
            return Path.Combine(GetStartupFolderPath(), $"{shortcutName}.lnk");
        }

        public static void CreateShortcut()
        {
            string appPath = Process.GetCurrentProcess().ProcessName;
            string shortcutPath = GetShortcutPath(appPath);

            if (File.Exists(shortcutPath))
            {
                Console.WriteLine("Shortcut already exists.");
                return;
            }

            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine("Could not get executable path.");
                return;
            }

            try
            {
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(exePath);
                link.SetWorkingDirectory(Path.GetDirectoryName(exePath));
                link.SetDescription("Launches the app on system startup.");

                var file = (IPersistFile)link;
                file.Save(shortcutPath, false);

                Console.WriteLine("Shortcut created successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create shortcut: {ex.Message}");
            }
        }

        public static bool RemoveShortcut()
        {
            string appPath = Process.GetCurrentProcess().ProcessName;
            string shortcutPath = GetShortcutPath(appPath);

            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                Console.WriteLine("Shortcut removed successfully.");
                return true;
            }

            Console.WriteLine("Shortcut does not exist.");
            return false;
        }
    }
}