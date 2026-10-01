using System.Security.Cryptography;
using System.Text;

namespace VikingClip.Core.Settings;

/// <summary>
/// Webhook URLs are effectively passwords for a channel. They are stored encrypted with Windows DPAPI
/// (bound to the Windows user account) so a copied settings.json is useless on another PC/account.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VikingClip.webhook.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // legacy/plain
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // settings copied from another account (or corrupted): treat as not configured
        }
    }

    public static string GetWebhookUrl(this DiscordChannel channel) => Unprotect(channel.WebhookProtected);
    public static void SetWebhookUrl(this DiscordChannel channel, string url) => channel.WebhookProtected = Protect(url.Trim());
}
