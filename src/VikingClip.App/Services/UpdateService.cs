using System.Windows.Threading;
using Velopack;
using Velopack.Sources;
using VikingClip.Core.Logging;

namespace VikingClip.App.Services;

/// <summary>
/// Auto-update from the GitHub Releases of the repo. Checks shortly after start and every 6 hours,
/// downloads in the background, and applies when the user says so (or on exit if nothing is recording).
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/to31109-ctrl/vikingclip";

    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _canApplyNow;
    private bool _checking;

    public string Status { get; private set; } = "";
    public UpdateInfo? Downloaded { get; private set; }
    public bool IsInstalled => _manager?.IsInstalled == true;
    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public event Action? Changed;

    public UpdateService(Func<bool> canApplyNow)
    {
        _canApplyNow = canApplyNow;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            Status = _manager.IsInstalled ? "Not checked yet" : "Updates only work in the installed app (this is a dev build)";
        }
        catch (Exception ex)
        {
            Status = "Updater unavailable: " + ex.Message;
            Log.Warn(Status);
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += async (_, _) => await CheckAsync(userInitiated: false);
    }

    public void Start(bool enabled)
    {
        _timer.Stop();
        if (!enabled || !IsInstalled) return;
        _timer.Start();
        // First check a minute after launch so startup stays snappy.
        var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        once.Tick += async (_, _) => { once.Stop(); await CheckAsync(userInitiated: false); };
        once.Start();
    }

    public async Task<bool> CheckAsync(bool userInitiated)
    {
        if (_manager is null || !_manager.IsInstalled || _checking) return false;
        _checking = true;
        try
        {
            SetStatus("Checking for updates…");
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                SetStatus($"Up to date (v{CurrentVersion})");
                return false;
            }
            var v = info.TargetFullRelease.Version;
            SetStatus($"Downloading v{v}…");
            await _manager.DownloadUpdatesAsync(info, p => SetStatus($"Downloading v{v}… {p}%"));
            Downloaded = info;
            SetStatus($"v{v} is ready - restart VikingClip to update");
            Log.Info($"Update v{v} downloaded");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Update check failed: " + ex.Message);
            Log.Warn("Update check failed: " + ex.Message);
            return false;
        }
        finally { _checking = false; }
    }

    /// <summary>Restart into the new version now.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || Downloaded is null) return;
        Log.Info("Applying update and restarting");
        _manager.ApplyUpdatesAndRestart(Downloaded, new[] { "--minimized" });
    }

    /// <summary>Called on exit: install the downloaded update silently without relaunching.</summary>
    public void ApplyOnExitIfReady()
    {
        if (_manager is null || Downloaded is null || !_canApplyNow()) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(Downloaded, silent: true, restart: false);
            Log.Info("Update will be applied after exit");
        }
        catch (Exception ex) { Log.Warn("Could not schedule update on exit: " + ex.Message); }
    }

    private void SetStatus(string s)
    {
        Status = s;
        try { Changed?.Invoke(); } catch { }
    }
}
