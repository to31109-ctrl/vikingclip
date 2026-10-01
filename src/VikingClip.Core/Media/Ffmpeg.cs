using System.Diagnostics;
using System.Text.RegularExpressions;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;

namespace VikingClip.Core.Media;

public sealed record FfmpegResult(int ExitCode, string StdErr, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;

    /// <summary>Last meaningful stderr line, for error messages.</summary>
    public string Summary
    {
        get
        {
            var lines = StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var last = lines.LastOrDefault(l => !l.StartsWith("Conversion failed", StringComparison.Ordinal)) ?? lines.LastOrDefault();
            return last ?? $"ffmpeg exited with code {ExitCode}";
        }
    }
}

/// <summary>Thin wrapper for running the bundled ffmpeg.</summary>
public static partial class Ffmpeg
{
    private static string? _exe;
    private static readonly object Gate = new();
    private static HashSet<string>? _filters;

    public static string ExePath
    {
        get
        {
            lock (Gate)
            {
                _exe ??= Paths.FindFfmpeg() ?? throw new FileNotFoundException(
                    "ffmpeg.exe was not found. It should be in the 'ffmpeg' folder next to VikingClip.exe (run tools\\get-ffmpeg.ps1 in a dev checkout).");
                return _exe;
            }
        }
    }

    public static bool IsAvailable => Paths.FindFfmpeg() is not null;

    public static string VersionLine()
    {
        try
        {
            var psi = Psi(new[] { "-version" }, redirectStdout: true);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var first = p.StandardOutput.ReadLine() ?? "";
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return first.Length > 0 ? first : "ffmpeg (unknown version)";
        }
        catch (Exception ex) { return "ffmpeg: " + ex.Message; }
    }

    public static bool HasFilter(string name)
    {
        lock (Gate)
        {
            if (_filters is null)
            {
                _filters = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var psi = Psi(new[] { "-hide_banner", "-filters" }, redirectStdout: true);
                    using var p = Process.Start(psi)!;
                    string? line;
                    while ((line = p.StandardOutput.ReadLine()) is not null)
                    {
                        var m = FilterLine().Match(line);
                        if (m.Success) _filters.Add(m.Groups[1].Value);
                    }
                    p.WaitForExit(10000);
                }
                catch (Exception ex) { Log.Warn("Could not list ffmpeg filters: " + ex.Message); }
            }
            return _filters.Contains(name);
        }
    }

    public static ProcessStartInfo Psi(IEnumerable<string> args, bool redirectStdout)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Paths.TempDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs ffmpeg to completion (short jobs: muxing, probing, thumbnails, compression).</summary>
    public static async Task<FfmpegResult> RunAsync(
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        ProcessPriorityClass priority = ProcessPriorityClass.Normal,
        Action<string>? onStderrLine = null)
    {
        var argList = args.ToList();
        var sw = Stopwatch.StartNew();
        var stderr = new System.Text.StringBuilder();
        using var p = new Process { StartInfo = Psi(argList, redirectStdout: false) };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 64 * 1024) stderr.Remove(0, stderr.Length - 48 * 1024);
            }
            onStderrLine?.Invoke(e.Data);
        };

        Log.Debug("ffmpeg " + string.Join(' ', argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        p.Start();
        ChildProcessJob.Add(p);
        try { if (priority != ProcessPriorityClass.Normal) p.PriorityClass = priority; } catch { }
        p.BeginErrorReadLine();
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            lock (stderr) stderr.AppendLine(ct.IsCancellationRequested ? "Cancelled" : $"Timed out after {timeout}");
            return new FfmpegResult(-1, stderr.ToString(), sw.Elapsed);
        }
        p.WaitForExit(); // flush async stderr
        return new FfmpegResult(p.ExitCode, stderr.ToString(), sw.Elapsed);
    }

    /// <summary>Reads "Duration: 00:00:45.12" from ffmpeg -i output.</summary>
    public static async Task<double?> ProbeDurationAsync(string file, CancellationToken ct = default)
    {
        double? result = null;
        await RunAsync(new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(15), ct, onStderrLine: line =>
        {
            var m = DurationLine().Match(line);
            if (m.Success)
                result = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }).ConfigureAwait(false);
        return result;
    }

    [GeneratedRegex(@"^\s*[A-Z.]{3}\s+(\S+)\s")]
    private static partial Regex FilterLine();

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationLine();
}
