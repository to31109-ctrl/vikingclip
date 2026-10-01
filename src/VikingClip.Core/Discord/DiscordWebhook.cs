using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Discord;

public sealed record WebhookInfo(string Name, string ChannelId, string GuildId);

public sealed record UploadResult(bool Success, string? Error, string? AttachmentUrl = null)
{
    public static UploadResult Fail(string error) => new(false, error);
}

/// <summary>Posts files to Discord channels through webhooks (no bot, no login needed).</summary>
public static partial class DiscordWebhook
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VikingClip (https://github.com/to31109-ctrl/vikingclip, 1.0)");
        return c;
    }

    public static bool TryNormalizeUrl(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = WebhookUrl().Match(input.Trim());
        if (!m.Success) return false;
        normalized = $"https://discord.com/api/webhooks/{m.Groups[1].Value}/{m.Groups[2].Value}";
        return true;
    }

    public static async Task<WebhookInfo> GetInfoAsync(string url, CancellationToken ct = default)
    {
        if (!TryNormalizeUrl(url, out var u)) throw new ArgumentException("That is not a Discord webhook URL.");
        using var resp = await Http.GetAsync(u, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Discord does not know this webhook (deleted, or the URL is incomplete).");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        return new WebhookInfo(
            root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            root.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "",
            root.TryGetProperty("guild_id", out var g) ? g.GetString() ?? "" : "");
    }

    public static async Task<UploadResult> UploadAsync(string url, string filePath, string message, string? username, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!TryNormalizeUrl(url, out var u)) return UploadResult.Fail("Invalid webhook URL");
        var size = new FileInfo(filePath).Length;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                var payload = new Dictionary<string, object?>
                {
                    ["content"] = message,
                    ["allowed_mentions"] = new { parse = Array.Empty<string>() },
                };
                if (!string.IsNullOrWhiteSpace(username)) payload["username"] = username;
                form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                var fileContent = new ProgressStreamContent(fs, size, progress);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(filePath).ToLowerInvariant() switch
                {
                    ".mp4" => "video/mp4",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream",
                });
                form.Add(fileContent, "files[0]", Path.GetFileName(filePath));

                using var resp = await Http.PostAsync(u + "?wait=true", form, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    string? attachmentUrl = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("attachments", out var atts) && atts.GetArrayLength() > 0)
                            attachmentUrl = atts[0].GetProperty("url").GetString();
                    }
                    catch { }
                    return new UploadResult(true, null, attachmentUrl);
                }

                if ((int)resp.StatusCode == 429)
                {
                    var wait = 2.0;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("retry_after", out var ra)) wait = ra.GetDouble();
                    }
                    catch { }
                    Log.Warn($"Discord rate limited; retrying in {wait:0.#}s");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 30)), ct).ConfigureAwait(false);
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge || body.Contains("40005"))
                    return UploadResult.Fail($"Discord rejected the file as too large ({size / 1024 / 1024} MB). Lower the channel's upload limit in settings so it gets compressed more.");
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                    return UploadResult.Fail("Webhook not found - it was deleted or the URL is wrong.");

                Log.Warn($"Discord upload failed {(int)resp.StatusCode}: {body}");
                return UploadResult.Fail($"Discord returned {(int)resp.StatusCode}: {Trim(body)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return UploadResult.Fail("Cancelled"); }
            catch (HttpRequestException ex)
            {
                Log.Warn($"Discord upload network error (attempt {attempt + 1}): {ex.Message}");
                if (attempt == 2) return UploadResult.Fail("Network error: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        return UploadResult.Fail("Discord kept rate-limiting the upload.");
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    [GeneratedRegex(@"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/(\d+)/([A-Za-z0-9_\-]+)/?(?:\?.*)?$")]
    private static partial Regex WebhookUrl();

    /// <summary>StreamContent that reports upload progress.</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly long _length;
        private readonly IProgress<double>? _progress;

        public ProgressStreamContent(Stream stream, long length, IProgress<double>? progress)
        {
            _stream = stream;
            _length = length;
            _progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
        {
            var buffer = new byte[128 * 1024];
            long sent = 0;
            int n;
            while ((n = await _stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                sent += n;
                _progress?.Report(_length > 0 ? (double)sent / _length : 0);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
