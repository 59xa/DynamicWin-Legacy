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
    static async Task<int> Main(string[] args)
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
                return 1;
            }

            // Sanitize input arguments: remove surrounding quotes and trim whitespace
            string zipPath = args[0]?.Trim().Trim('"') ?? string.Empty;
            string installPath = args[1]?.Trim().Trim('"') ?? string.Empty;

            // Normalize path separators and remove any trailing directory separator char
            installPath = installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Resolve full paths to ensure consistent comparison later
            try { zipPath = Path.GetFullPath(zipPath); } catch { }
            try { installPath = Path.GetFullPath(installPath); } catch { }

            string mainExe = Path.Combine(installPath, "DynamicWin.exe");
            string updaterExe = Path.Combine(installPath, "Updater.exe");

            Log($"Zip: {zipPath}");
            Log($"Install Path: {installPath}");
            Log($"Main EXE: {mainExe}");
            Log($"Updater EXE: {updaterExe}");

            // Wait for main app to fully exit. Prefer waiting on process instances instead of a fixed delay.
            Log("Waiting for DynamicWin to exit...");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000) // wait up to 15s
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(mainExe));
                if (procs == null || procs.Length == 0) break;
                await Task.Delay(500);
            }

            // Extract update safely
            Log("Extracting update...");
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string destinationPath = Path.Combine(installPath, entry.FullName);

                        // Skip the updater itself
                        if (string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(updaterExe), StringComparison.OrdinalIgnoreCase))
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
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Log($"ACCESS DENIED during extraction: {uaEx.Message}");

                // Try relaunching this updater elevated to retry extraction
                try
                {
                    var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
                    var psi = new ProcessStartInfo
                    {
                        FileName = currentExe,
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = $"\"{zipPath}\" \"{installPath}\""
                    };

                    Log("Attempting to relaunch updater elevated...");
                    var elevated = Process.Start(psi);
                    if (elevated != null)
                    {
                        Log("Relaunched elevated updater, exiting current instance.");
                        return 0;
                    }
                    else
                    {
                        Log("Failed to relaunch elevated updater (Process.Start returned null).");
                        return 2;
                    }
                }
                catch (System.ComponentModel.Win32Exception win32Ex)
                {
                    // User likely cancelled UAC prompt
                    Log("Elevation cancelled or failed: " + win32Ex.Message);
                    return 3;
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
            return 0;
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

            return 4;
        }
    }
}
