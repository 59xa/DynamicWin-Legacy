using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System;

/*
 *
 *  Overview:
 *      - Updater logic to handle automatic updates rather than user manually updating
 *      - from the repository holding the codebase (DynamicWin-Legacy/stable).
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    27 November 2025
 *  Last Modified:          27 November 2025
 *
 */

namespace DynamicWin.Utils
{
    internal class Updater
    {
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
                using HttpClient client = new();
                string json = await client.GetStringAsync("https://raw.githubusercontent.com/59xa/DynamicWin-Legacy/refs/heads/updater/version.json");

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var remote = JsonSerializer.Deserialize<AppVersion>(json, options);

                // Fallback: if properties are null due to unexpected schema/casing, try to read them manually
                if (remote == null)
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: remote payload was null after deserialization");
#endif
                    return null;
                }

                if (string.IsNullOrWhiteSpace(remote.version) || string.IsNullOrWhiteSpace(remote.downloadUri))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (string.IsNullOrWhiteSpace(remote.version))
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "version", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "ver", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                        remote.version = prop.Value.GetString();
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(remote.downloadUri))
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "downloadUri", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "download", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "url", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                        remote.downloadUri = prop.Value.GetString();
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore, we'll validate below
                    }
                }

                // Validate remote payload now
                if (string.IsNullOrWhiteSpace(remote.version))
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: remote.version is null or empty after fallback extraction");
#endif
                    return null;
                }

                if (string.IsNullOrWhiteSpace(remote.downloadUri))
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: remote.downloadUri is null or empty after fallback extraction");
#endif
                    return null;
                }

                int cmp;
                try
                {
                    // Compare remote (a) to current (b)
                    cmp = CompareVersionStrings(remote.version, DynamicWinMain.Version);
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[UPDATER]: version comparison failed: " + ex.Message);
#endif
                    // Fallback: attempt numeric parse only; if fails, give up
                    try
                    {
                        Version current = ParseVersion(DynamicWinMain.Version);
                        Version latest = ParseVersion(remote.version);
                        cmp = latest.CompareTo(current);
                    }
                    catch
                    {
                        return null;
                    }
                }

#if DEBUG
                Debug.WriteLine($"[UPDATER]: Comparing remote '{remote.version}' to current '{DynamicWinMain.Version}' -> cmp={cmp}");
#endif

                return cmp > 0 ? remote : null;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[UPDATER]: exception while checking for update: " + ex.Message);
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

            // Use ArgumentList to avoid ALL quoting issues
            var psi = new ProcessStartInfo
            {
                FileName = updater,
                UseShellExecute = false // IMPORTANT — required for ArgumentList
            };

            // Add arguments as raw strings
            psi.ArgumentList.Add(zipPath);
            psi.ArgumentList.Add(AppContext.BaseDirectory);

            // Start updater
            Process.Start(psi);

            // Kill app
            Environment.Exit(0);
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
        private record VersionInfo(Version Numeric, string? Pre);

        private static VersionInfo ParseVersionWithPre(string raw)
        {
            raw = (raw ?? string.Empty).Trim().ToLower();
            if (raw.StartsWith("v")) raw = raw.Substring(1);

            int i = 0;
            while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.')) i++;

            string numeric = raw.Substring(0, i);
            string pre = i < raw.Length ? raw.Substring(i) : string.Empty;

            return new VersionInfo(new Version(numeric), pre);
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
            if (numCmp != 0) return numCmp;

            bool aIsRelease = string.IsNullOrEmpty(va.Pre);
            bool bIsRelease = string.IsNullOrEmpty(vb.Pre);

            if (aIsRelease && bIsRelease) return 0;
            if (aIsRelease) return 1; // release is newer than prerelease
            if (bIsRelease) return -1;

            // both prerelease -> compare lexicographically
            return string.Compare(va.Pre, vb.Pre, StringComparison.OrdinalIgnoreCase);
        }

    }

    public class AppVersion
    {
        public string version { get; set; }
        public string downloadUri { get; set; }
    }
}
