using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

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
            using HttpClient client = new();
            string json = await client.GetStringAsync("https://raw.githubusercontent.com/59xa/DynamicWin-Legacy/refs/heads/updater/version.json");

            var remote = JsonSerializer.Deserialize<AppVersion>(json);

            Version current = new Version(DynamicWinMain.Version); // Current application version
            Version latest = new Version(remote.version);

            return latest > current ? remote : null;
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

            Process.Start(new ProcessStartInfo
            {
                FileName = updater,
                Arguments = $"\"{zipPath}\" \"{AppContext.BaseDirectory}\"",
                UseShellExecute = true
            });

            Environment.Exit(0); // Exit application
        }
    }

    public class AppVersion
    {
        public string version { get; set; }
        public string downloadUri { get; set; }
    }
}
