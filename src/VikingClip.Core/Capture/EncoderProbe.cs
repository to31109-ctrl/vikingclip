using VikingClip.Core.Display;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;
using VikingClip.Core.Settings;

namespace VikingClip.Core.Capture;

/// <summary>Finds a working encoder plan per GPU adapter by actually running a short capture.</summary>
public static class EncoderProbe
{
    public sealed record ProbeOutcome(Dictionary<int, EncoderPlan> PlanByAdapter, List<string> Failures, bool FromCache);

    public static async Task<ProbeOutcome> ProbeAsync(
        IReadOnlyList<MonitorInfo> monitors,
        string? overrideId,
        EncoderCache? cache,
        string fingerprint,
        bool gpuScalerAvailable,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new Dictionary<int, EncoderPlan>();
        var failures = new List<string>();
        var adapters = monitors.GroupBy(m => m.AdapterIndex).ToList();
        var vendors = monitors.Select(m => m.Vendor).Distinct().ToList();

        if (overrideId is null && cache is not null && cache.Fingerprint == fingerprint)
        {
            var complete = true;
            foreach (var g in adapters)
            {
                if (cache.PlanByAdapter.TryGetValue(g.Key, out var id) && EncoderPlan.ById(id) is { } plan)
                    result[g.Key] = plan;
                else complete = false;
            }
            if (complete) return new ProbeOutcome(result, failures, FromCache: true);
            result.Clear();
        }

        foreach (var g in adapters)
        {
            ct.ThrowIfCancellationRequested();
            var monitor = g.First();
            IEnumerable<EncoderPlan> candidates = overrideId is not null && EncoderPlan.ById(overrideId) is { } forced
                ? new[] { forced }
                : EncoderPlan.CandidatesFor(monitor.Vendor, vendors);

            EncoderPlan? chosen = null;
            foreach (var plan in candidates)
            {
                progress?.Report($"Testing {plan.DisplayName} on {monitor.AdapterName}…");
                var r = await Ffmpeg.RunAsync(FfmpegArgs.Probe(monitor, plan, gpuScalerAvailable), TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                if (r.Success)
                {
                    chosen = plan;
                    Log.Info($"Encoder for adapter {g.Key} ({monitor.AdapterName}): {plan.DisplayName} ({r.Elapsed.TotalMilliseconds:0} ms)");
                    break;
                }
                Log.Warn($"Encoder {plan.Id} failed on {monitor.AdapterName}: {r.Summary}");
            }

            if (chosen is null)
                failures.Add($"No working encoder for {monitor.AdapterName} (monitor {monitor.DeviceName}).");
            else
                result[g.Key] = chosen;
        }

        return new ProbeOutcome(result, failures, FromCache: false);
    }
}
