using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System;
using DynamicWin.Main;
using System.Runtime.InteropServices;

/*
 *
 *  Overview:
 *      - Updater logic to handle automatic updates rather than user manually updating
 *      - from the repository holding the codebase (DynamicWin-Legacy/release).
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    27 November 2025
 *  Last Modified:          15 February 2026
 *
 */

namespace DynamicWin.Utils
{
    internal class Updater
    {
        /// <summary>
        /// Retrieves and deserializes an application version definition from a remote JSON file hosted on GitHub.
        /// </summary>
        /// <remarks>This performs an HTTP GET request to the specified file in the updater branch
        /// inside the repository. The JSON is deserialised in a case-insensitive manner. Network failures
        /// or invalid JSON will result in a <see langword="null"/> return value.</remarks>
        /// <param name="file">The name of the JSON file to fetch from the remote repository. Cannot be null or empty.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an <see cref="AppVersion"/>
        /// object if the file is successfully retrieved and deserialised; otherwise, <see langword="null"/>.</returns>
        private async Task<AppVersion?> FetchRemote(string file, Architecture cpuArchitecture)
        {
            using HttpClient client = new();

            string suffix = cpuArchitecture switch
            {
                Architecture.X64 => "-x64",
                Architecture.Arm64 => "-arm64",
                _ => string.Empty // In case none matches architecture
            };

            string json = await client.GetStringAsync(
                $"https://raw.githubusercontent.com/59xa/DynamicWin-Legacy/refs/heads/updater/{file}{suffix}.json");

#if DEBUG
            Debug.WriteLine(json);
#endif

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<AppVersion>(json, options);
        }

        /// <summary>
        /// Checks for a newer version of DynamicWin-Legacy by retrieving version information from a remote source.
        /// </summary>
        /// <remarks>This method retrieves version information from a remote JSON file hosted online.
        /// Network connectivity is required for the operation to succeed. If the remote source is unavailable or the
        /// response cannot be parsed, an exception will be thrown.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains an <see cref="AppVersion"/>
        /// object representing the latest available version if an update is found; otherwise, <see langword="null"/> if
        /// the current version is up to date.</returns>
        public async Task<AppVersion?> CheckForUpdate()
        {
            try
            {
#if DEBUG
                Debug.WriteLine($"[UPDATER]: current version = {DynamicWinMain.Version}");
                Debug.WriteLine($"[UPDATER]: selected stream = {(Settings.ReleaseStream == 1 ? "canary" : "release")}");
#endif

                var release = await FetchRemote("version-release", DynamicWinMain.ProcessArchitecture);

#if DEBUG
                if (release != null)
                    Debug.WriteLine($"[UPDATER]: remote release = {release.version}");
                else
                    Debug.WriteLine("[UPDATER]: failed to fetch release");
#endif

                // FORCE REVERT: canary -> release
                if (Settings.ReleaseStream == 0 &&
                    DynamicWinMain.ReleaseStream == Channel.Canary &&
                    release != null)
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: stream switch detected (canary -> release), forcing revert");
#endif
                    return release;
                }

                // Normal forward update: release
                if (release != null)
                {
                    int cmp = CompareVersionStrings(release.version, DynamicWinMain.Version);

#if DEBUG
                    Debug.WriteLine($"[UPDATER]: release compare -> {cmp}");
#endif

                    if (cmp > 0)
                    {
#if DEBUG
                        Debug.WriteLine("[UPDATER]: release update available -> prioritised");
#endif
                        return release;
                    }
                }

                // If user is NOT on canary, stop here
                if (Settings.ReleaseStream != 1)
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: not on canary stream, stopping");
#endif
                    return null;
                }

                var canary = await FetchRemote("version-canary", DynamicWinMain.ProcessArchitecture);

#if DEBUG
                if (canary != null)
                    Debug.WriteLine($"[UPDATER]: remote canary = {canary.version}");
                else
                    Debug.WriteLine("[UPDATER]: failed to fetch canary");
#endif

                if (canary != null)
                {
                    int cmp = CompareVersionStrings(canary.version, DynamicWinMain.Version);

#if DEBUG
                    Debug.WriteLine($"[UPDATER]: canary compare -> {cmp}");
#endif

                    if (cmp > 0)
                    {
#if DEBUG
                        Debug.WriteLine("[UPDATER]: canary update available");
#endif
                        return canary;
                    }
                }

#if DEBUG
                Debug.WriteLine("[UPDATER]: no update available");
#endif
                return null;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[UPDATER]: exception while checking for update: " + ex);
#endif
                return null;
            }
        }

        /// <summary>
        /// Downloads the update package specified by the given application version and saves it to a temporary file.
        /// </summary>
        /// <remarks>The caller is responsible for deleting the temporary file after use. This method
        /// overwrites any existing file named "update.zip" in the temporary directory. Network errors or invalid URIs
        /// will result in exceptions being thrown by underlying system calls.</remarks>
        /// <param name="update">An object representing the application version to download. The <c>downloadUri</c> property must specify a
        /// valid URI for the update package.</param>
        /// <returns>A string containing the full path to the downloaded update package file. The file is saved in the system's
        /// temporary directory.</returns>
        public async Task<string> DownloadUpdate(AppVersion update)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "update.zip");
            using HttpClient client = new();
            var bytes = await client.GetByteArrayAsync(update.downloadUri);
            await File.WriteAllBytesAsync(tempPath, bytes);
            return tempPath;
        }

        /// <summary>
        /// Starts the external updater process with the specified update package and terminates the current
        /// application.
        /// </summary>
        /// <remarks>This method immediately exits the current application after launching the updater.
        /// Any unsaved data will be lost. The updater executable must be located in the application's base
        /// directory.</remarks>
        /// <param name="zipPath">The full path to the update package (ZIP file) to be passed to the updater. Cannot be null or empty.</param>
        public void LaunchUpdater(string zipPath)
        {
            string updater = Path.Combine(AppContext.BaseDirectory, "Updater.exe");

            string baseDir = AppContext.BaseDirectory ?? string.Empty;
            baseDir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool needsElevation = IsInProgramFiles(baseDir);

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                UseShellExecute = true
            };

            if (needsElevation)
            {
                psi.Verb = "runas"; // trigger UAC only when required
            }

            psi.Arguments = $"\"{zipPath}\" \"{baseDir}\"";

#if DEBUG
            Debug.WriteLine($"[UPDATER]: install dir = {baseDir}");
            Debug.WriteLine($"[UPDATER]: elevation required = {needsElevation}");
#endif

            try
            {
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    Environment.Exit(0);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[UPDATER]: updater launch cancelled or failed: " + ex.Message);
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[UPDATER]: exception while launching updater: " + ex);
#endif
            }
        }

        /// <summary>
        /// Parses a version string that may include a leading 'v' or trailing prerelease labels and returns a
        /// corresponding Version object.
        /// </summary>
        /// <remarks>This method ignores any leading 'v' character and any trailing non-numeric labels
        /// such as prerelease identifiers (e.g., 'alpha', 'rc1'). Only the numeric portion (major, minor, build,
        /// revision) is parsed. Throws an exception if the numeric portion is not a valid version format.</remarks>
        /// <param name="raw">The version string to parse. May include a leading 'v' and trailing prerelease or build metadata. Cannot be
        /// null.</param>
        /// <returns>A Version object representing the numeric portion of the specified version string.</returns>
        public static Version ParseVersion(string raw)
        {
            // Remove leading "v" if present
            raw = (raw ?? string.Empty).Trim().ToLower();
            if (raw.StartsWith("v"))
                raw = raw.Substring(1);

            // Remove trailing letters like "a", "b", "rc1", etc
            int i = 0;
            while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.'))
                i++;

            string numeric = raw.Substring(0, i);

            return new Version(numeric);
        }

        // New helpers to support prerelease comparison
        private enum PreType
        {
            Alpha = 0,
            Beta = 1,
            RC = 2,
            Unknown = -1
        }

        private record VersionInfo(
            Version Numeric,
            PreType Type,
            int PreNumber
        );

        /// <summary>
        /// Parses a version string that may include a pre-release identifier into a VersionInfo object.
        /// </summary>
        /// <remarks>The method supports version strings with or without a leading 'v' (e.g., 'v1.2.3').
        /// Recognised pre-release identifiers are 'a' (alpha), 'b' (beta), and 'rc' (release candidate), optionally
        /// followed by a number (e.g., '1.2.3rc2'). If the pre-release part is missing or unrecognised, the pre-release
        /// type is set to Unknown and the number is set to 0.</remarks>
        /// <param name="raw">The raw version string to parse. May include an optional leading 'v' and a pre-release suffix such as 'a',
        /// 'b', or 'rc' followed by a number.</param>
        /// <returns>A VersionInfo object representing the parsed version and pre-release information. If the pre-release part is
        /// not recognised, the pre-release type is set to Unknown.</returns>
        private static VersionInfo ParseVersionWithPre(string raw)
        {
            raw = (raw ?? string.Empty).Trim().ToLower();
            if (raw.StartsWith("v"))
                raw = raw.Substring(1);

            int i = 0;
            while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.'))
                i++;

            string numericPart = raw.Substring(0, i);
            string prePart = i < raw.Length ? raw.Substring(i) : string.Empty;

            var numeric = new Version(numericPart);

            if (string.IsNullOrEmpty(prePart))
                return new VersionInfo(numeric, PreType.Unknown, 0);

            PreType type;
            int numStart;

            if (prePart.StartsWith("a"))
            {
                type = PreType.Alpha;
                numStart = 1;
            }
            else if (prePart.StartsWith("b"))
            {
                type = PreType.Beta;
                numStart = 1;
            }
            else if (prePart.StartsWith("rc"))
            {
                type = PreType.RC;
                numStart = 2;
            }
            else
            {
                type = PreType.Unknown;
                numStart = prePart.Length;
            }

            int number = 0;
            if (numStart < prePart.Length)
                int.TryParse(prePart.Substring(numStart), out number);

            return new VersionInfo(numeric, type, number);
        }


        /// <summary>
        /// Compare version strings that may include prerelease suffixes.
        /// Returns &gt;0 if a &gt; b (a newer), 0 if equal, &lt;0 if a &lt; b.
        /// Rules:
        ///  - Compare numeric Version first.
        ///  - If numeric equal, absence of prerelease (release) is considered newer than any prerelease.
        ///  - If both have prerelease, compare the prerelease strings lexicographically.
        /// </summary>
        private static int CompareVersionStrings(string a, string b)
        {
            var va = ParseVersionWithPre(a);
            var vb = ParseVersionWithPre(b);

            int numCmp = va.Numeric.CompareTo(vb.Numeric);
            if (numCmp != 0)
                return numCmp;

            bool aIsRelease = va.Type == PreType.Unknown;
            bool bIsRelease = vb.Type == PreType.Unknown;

            if (aIsRelease && bIsRelease) return 0;
            if (aIsRelease) return 1;   // release beats prerelease
            if (bIsRelease) return -1;

            int typeCmp = va.Type.CompareTo(vb.Type);
            if (typeCmp != 0)
                return typeCmp;

            return va.PreNumber.CompareTo(vb.PreNumber);
        }

        /// <summary>
        /// Determines whether specified path is located within Program Files or Program Files (x86) directory.
        /// </summary>
        /// <remarks>Performs a case-insensitive comparison and normalises the input path
        /// before evaluation. Does not verify whether the path actually exists on disk.</remarks>
        /// <param name="path">The file system path to evaluate. Can be either a relative or absolute path.</param>
        /// <returns>true if the specified path is within Program Files or Program Files (x86) directory; otherwise, false.</returns>
        private static bool IsInProgramFiles(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

            string pf = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                .TrimEnd(Path.DirectorySeparatorChar)
                .ToUpperInvariant();

            string pf86 = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
                .TrimEnd(Path.DirectorySeparatorChar)
                .ToUpperInvariant();

            return fullPath.StartsWith(pf) || fullPath.StartsWith(pf86);
        }

    }

    public class AppVersion
    {
        public string version { get; set; }
        public string downloadUri { get; set; }
        public string releaseStream { get; set; }
    }

    public enum Channel
    {
        Release,
        Canary
    }

    public static class ReleaseChannelExtensions
    {
        public static string ToFriendlyString(this Channel channel)
        {
            return channel switch
            {
                Channel.Release => "release",
                Channel.Canary => "canary",
                _ => "unknown"
            };
        }

        public static string GetIconPath(this Channel stream)
        {
            return stream switch
            {
                Channel.Release => "app.ico",
                Channel.Canary => "canary.ico",
                _ => "app.ico"
            };
        }
    }
}
