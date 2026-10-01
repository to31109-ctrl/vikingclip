using System.IO;
using System.Threading;
using Velopack;
using VikingClip.Core.Logging;
using VikingClip.Core.Startup;

namespace VikingClip.App;

public static class Program
{
    public const string SingleInstanceMutexName = @"Local\VikingClip.SingleInstance";
    public const string ShowWindowEventName = @"Local\VikingClip.ShowWindow";

    private static Mutex? _mutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AutoStart.Set(false, LauncherExePath))
            .Run();

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // Another VikingClip is running: ask it to show its window and leave.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowWindowEventName);
                evt.Set();
            }
            catch { }
            return 0;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            throw;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// The exe to put in shortcuts and the Run key. When installed by Velopack the app lives in
    /// ...\VikingClip\current\VikingClip.exe and a stable stub ...\VikingClip\VikingClip.exe survives updates.
    /// </summary>
    public static string LauncherExePath
    {
        get
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VikingClip.exe");
            var dir = Path.GetDirectoryName(exe);
            if (dir is not null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(exe));
                if (File.Exists(stub)) return stub;
            }
            return exe;
        }
    }
}
