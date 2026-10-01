using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using VikingClip.App.Services;
using VikingClip.App.Windows;
using VikingClip.Core.Capture;
using VikingClip.Core.Games;
using VikingClip.Core.Library;
using VikingClip.Core.Logging;
using VikingClip.Core.Settings;
using VikingClip.Core.Startup;

namespace VikingClip.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public SettingsStore Settings { get; private set; } = null!;
    public CaptureEngine Engine { get; private set; } = null!;
    public ClipLibrary Library { get; private set; } = null!;
    public GameDetector Games { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public ActionController Actions { get; private set; } = null!;
    public TrayService Tray { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;

    public bool StartedMinimized { get; private set; }
    public List<string> HotkeyProblems { get; } = new();
    public event Action? HotkeysChanged;

    private MainWindow? _main;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartedMinimized = e.Args.Any(a => string.Equals(a, AutoStart.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        Log.Info($"VikingClip starting (minimized={StartedMinimized}, exe={Environment.ProcessPath})");

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);

        Settings = new SettingsStore();
        Library = new ClipLibrary();
        Games = new GameDetector(() => Settings.Current.GameOverrides);
        Engine = new CaptureEngine(Settings);
        Updates = new UpdateService(() => !Actions.IsRecording);
        Hotkeys = new HotkeyService();
        Actions = new ActionController(this);
        Tray = new TrayService(this);

        var snap = Array.IndexOf(e.Args, "--ui-snapshots");
        if (snap >= 0)
        {
            var dir = e.Args.Length > snap + 1 ? e.Args[snap + 1] : Path.Combine(Core.Paths.TempDir, "ui");
            _ = UiSnapshots.RunAsync(this, dir);
            return;
        }

        Hotkeys.Pressed += name => Actions.OnHotkey(name);
        Hotkeys.DisplayChanged += () => Engine.NotifyDisplayChange();
        ApplyHotkeys();
        Settings.Changed += _ => Dispatcher.BeginInvoke(ApplyHotkeys);

        // Only installed builds register for auto-start; a dev build must not start itself at login.
        if (Settings.Current.LaunchAtLogin && Updates.IsInstalled) AutoStart.Set(true, Program.LauncherExePath);
        else if (!Updates.IsInstalled) Log.Info("Dev build: auto-start not registered");

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        _ = Engine.StartAsync();
        Updates.Start(Settings.Current.CheckForUpdates);

        var firstRun = !Settings.Current.FirstRunCompleted;
        if (!StartedMinimized || firstRun) ShowMainWindow();
        if (firstRun) Settings.Update(s => s.FirstRunCompleted = true);
    }

    private string? _appliedHotkeys;

    public void ApplyHotkeys()
    {
        var s = Settings.Current;
        var signature = string.Join("|", s.Hotkeys.Panel, s.Hotkeys.InstantClip, s.Hotkeys.ToggleRecording, s.Hotkeys.Screenshot);
        if (signature == _appliedHotkeys) return; // settings saved for another reason
        _appliedHotkeys = signature;
        HotkeyProblems.Clear();
        foreach (var (name, hk) in new[]
                 {
                     ("panel", s.PanelHotkey),
                     ("clip", s.InstantClipHotkey),
                     ("record", s.ToggleRecordingHotkey),
                     ("screenshot", s.ScreenshotHotkey),
                 })
        {
            var err = Hotkeys.Register(name, hk);
            if (err is not null) HotkeyProblems.Add(err);
        }
        HotkeysChanged?.Invoke();
    }

    public void ShowMainWindow()
    {
        if (_exiting) return;
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    public MainWindow? MainWindowInstance => _main;

    public void OpenClipsFolder(string? subfolder = null)
    {
        var dir = Settings.Current.ClipsRoot;
        if (subfolder is not null) dir = Path.Combine(dir, subfolder);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public static void RevealInExplorer(string file)
    {
        if (File.Exists(file))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    public static void OpenWithDefaultApp(string file)
    {
        if (File.Exists(file))
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        try
        {
            Hotkeys.Dispose();
            Tray.Dispose();
            await Actions.ShutdownAsync();
            await Engine.StopAsync();
            Updates.ApplyOnExitIfReady();
        }
        catch (Exception ex)
        {
            Log.Error("Exit cleanup failed", ex);
        }
        Shutdown();
    }
}
