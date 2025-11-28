using System.Diagnostics;
using System.IO.Compression;

class Program
{
    static async Task Main(string[] args)
    {
        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string logDir = Path.Combine(documentsPath, "DynamicWin");
        Directory.CreateDirectory(logDir); // Ensure folder exists
        string logPath = Path.Combine(logDir, "DynamicWinUpdater.log");

        void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            catch
            {
                /* Fail silently if logging fails */
            }
        }

        Log("=== Updater started ===");

        try
        {
            if (args.Length < 2)
            {
                Log("ERROR: Missing arguments.");
                return;
            }

            string zipPath = args[0];
            string installPath = args[1];
            string mainExe = Path.Combine(installPath, "DynamicWin.exe");

            Log($"Zip: {zipPath}");
            Log($"Install Path: {installPath}");
            Log($"Main EXE: {mainExe}");

            // Wait for main app to close fully
            Log("Waiting for DynamicWin to exit...");
            await Task.Delay(2000);

            // Extract update
            Log("Extracting update...");
            ZipFile.ExtractToDirectory(zipPath, installPath, overwriteFiles: true);
            Log("Extraction complete.");

            // Start updated app
            Log("Starting updated application...");
            Process.Start(mainExe);

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
                    FileName = logDir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch { }
        }
    }
}
