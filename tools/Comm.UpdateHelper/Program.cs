using System.Diagnostics;
using System.IO.Compression;

namespace Comm.UpdateHelper;

internal static class Program
{
    public static int Main(string[] args)
    {
        var opts = Parse(args);
        if (opts is null)
        {
            Console.Error.WriteLine("Usage: Comm.UpdateHelper --worker --package <zip> --target-dir <dir> --wait-pid <pid> [--launch <exe>]");
            return 2;
        }

        try
        {
            return Run(opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex}");
            TryRestore(opts);
            return 1;
        }
    }

    private static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--worker":
                    o.Worker = true;
                    break;
                case "--package":
                    o.Package = args[++i];
                    break;
                case "--target-dir":
                    o.TargetDir = args[++i];
                    break;
                case "--wait-pid":
                    o.WaitPid = int.Parse(args[++i]);
                    break;
                case "--launch":
                    o.Launch = args[++i];
                    break;
                default:
                    return null;
            }
        }

        if (!o.Worker || string.IsNullOrWhiteSpace(o.Package) || string.IsNullOrWhiteSpace(o.TargetDir) || o.WaitPid <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(o.Launch))
        {
            o.Launch = Path.Combine(o.TargetDir, "CommTestTool.exe");
        }

        return o;
    }

    private static int Run(Options o)
    {
        Console.WriteLine($"Waiting for process {o.WaitPid} to exit...");
        try
        {
            var proc = Process.GetProcessById(o.WaitPid);
            if (!proc.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                throw new TimeoutException("Timed out waiting for the main process to exit.");
            }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }

        // Give file handles a moment to release.
        Thread.Sleep(1000);

        var stagingDir = Path.Combine(Path.GetTempPath(), "CommTestTool-update-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        Console.WriteLine("Extracting update package...");
        ZipFile.ExtractToDirectory(o.Package, stagingDir, overwriteFiles: true);

        var stagedExe = Path.Combine(stagingDir, "CommTestTool.exe");
        if (!File.Exists(stagedExe))
        {
            throw new FileNotFoundException("Update package does not contain CommTestTool.exe.", stagedExe);
        }

        var targetDir = o.TargetDir;
        var backupDir = targetDir + ".bak." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");

        if (Directory.Exists(targetDir))
        {
            Console.WriteLine("Backing up current installation...");
            Directory.Move(targetDir, backupDir);
        }

        try
        {
            Console.WriteLine("Applying update...");
            Directory.Move(stagingDir, targetDir);
        }
        catch
        {
            if (Directory.Exists(backupDir) && !Directory.Exists(targetDir))
            {
                Directory.Move(backupDir, targetDir);
            }
            throw;
        }

        Console.WriteLine("Launching updated application...");
        Process.Start(new ProcessStartInfo
        {
            FileName = o.Launch,
            UseShellExecute = true,
            WorkingDirectory = targetDir
        });

        // Keep backup for one successful run; cleanup staging.
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { /* ignore */ }

        Console.WriteLine("Update completed.");
        return 0;
    }

    private static void TryRestore(Options o)
    {
        try
        {
            var backup = Directory.GetDirectories(Path.GetDirectoryName(o.TargetDir)!, Path.GetFileName(o.TargetDir) + ".bak.*")
                .OrderByDescending(d => d)
                .FirstOrDefault();
            if (backup is not null && !Directory.Exists(o.TargetDir))
            {
                Directory.Move(backup, o.TargetDir);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class Options
    {
        public bool Worker { get; set; }
        public string Package { get; set; } = "";
        public string TargetDir { get; set; } = "";
        public int WaitPid { get; set; }
        public string Launch { get; set; } = "";
    }
}