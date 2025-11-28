using System.Diagnostics;
using System.IO.Compression;

/*
 *
 *  Overview:
 *      - Updater helper to handle file extracting and overriding old binaries
 *      = Updates DynamicWin Legacy to the latest version if one is made available
 *      
 *  Author:                 59xa
 *  Github:                 https://github.com/59xa
 *  Implementation Date:    28 November 2025
 *  Last Modified:          28 November 2025
 *
 */

class Program
{
    static async Task Main(string[] args)
    {
        string documentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DynamicWin"
        );
        Directory.CreateDirectory(documentsDir);

        // Create a unique log file for each run
        string logPath = Path.Combine(documentsDir, $"DynamicWinUpdater_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            catch { }
        }

        Log("=== Updater started ===");

        try
        {
            if (args.Length < 2)
            {
                Log("ERROR: Missing arguments (zipPath, installPath)");
                return;
            }

            string zipPath = args[0];
            string installPath = args[1];
            string mainExe = Path.Combine(installPath, "DynamicWin.exe");
            string updaterExe = Path.Combine(installPath, "Updater.exe");

            Log($"Zip: {zipPath}");
            Log($"Install Path: {installPath}");
            Log($"Main EXE: {mainExe}");
            Log($"Updater EXE: {updaterExe}");

            // Wait for main app to fully exit
            Log("Waiting for DynamicWin to exit...");
            await Task.Delay(2000);

            // Extract update safely
            Log("Extracting update...");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    string destinationPath = Path.Combine(installPath, entry.FullName);

                    // Skip the updater itself
                    if (Path.GetFullPath(destinationPath).Equals(updaterExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"Skipping updater file: {destinationPath}");
                        continue;
                    }

                    // Ensure directory exists
                    string? dir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    try
                    {
                        entry.ExtractToFile(destinationPath, overwrite: true);
                        Log($"Extracted: {destinationPath}");
                    }
                    catch (IOException ex)
                    {
                        Log($"WARNING: Could not overwrite {destinationPath}: {ex.Message}");
                    }
                }
            }
            Log("Extraction complete.");

            // Start updated app
            Log("Starting updated application...");
            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                UseShellExecute = true,
                WorkingDirectory = installPath
            });

            Log("=== Update completed successfully ===");
        }
        catch (Exception ex)
        {
            Log("ERROR during update:");
            Log(ex.ToString());

            // Attempt to open log folder even if error occurs
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = documentsDir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch { }
        }
    }
}
