using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace FanqieReader.Services;

public sealed class CookieService
{
    private readonly Func<CoreWebView2CookieManager> _managerProvider;

    public CookieService(Func<CoreWebView2CookieManager> managerProvider)
    {
        _managerProvider = managerProvider;
    }

    public async Task<string> GetCookieHeaderAsync()
    {
        var manager = _managerProvider();
        var cookies = await manager.GetCookiesAsync("https://fanqienovel.com");
        return string.Join("; ", cookies
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => $"{c.Name}={c.Value}"));
    }

    public async Task<int> SetCookieStringAsync(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) throw new FanqieException("请先粘贴 Cookie");

        var pairs = ParsePairs(raw);
        if (pairs.Count == 0) throw new FanqieException("没有识别到有效的 Cookie");

        var manager = _managerProvider();
        foreach (var (name, value) in pairs)
        {
            var cookie = manager.CreateCookie(name, value, ".fanqienovel.com", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = false;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            // WebView2 默认创建的是会话 Cookie（Expires = DateTime.MinValue），
            // 进程退出就没了，于是每次重开都要重新登录。给一个明确的过期时间，
            // 登录态才会写进 WebView2 的 profile 里。
            cookie.Expires = DateTime.Now.AddDays(LoginCookieDays);
            manager.AddOrUpdateCookie(cookie);
        }

        return pairs.Count;
    }

    /// <summary>How long an imported / scanned login cookie is kept.</summary>
    private const int LoginCookieDays = 180;

    public async Task ClearFanqieCookiesAsync()
    {
        var manager = _managerProvider();
        foreach (var domain in new[] { "https://fanqienovel.com", "https://www.fanqienovel.com", "https://reading.snssdk.com" })
        {
            IReadOnlyList<CoreWebView2Cookie> cookies;
            try { cookies = await manager.GetCookiesAsync(domain); }
            catch { continue; }
            foreach (var cookie in cookies)
            {
                try { manager.DeleteCookie(cookie); }
                catch { }
            }
        }
    }

    private static List<(string Name, string Value)> ParsePairs(string raw)
    {
        var result = new List<(string, string)>();

        // EditThisCookie / browser extension JSON export.
        if (raw.StartsWith("[", StringComparison.Ordinal) || raw.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var array = doc.RootElement;
                if (array.ValueKind == JsonValueKind.Object && array.TryGetProperty("cookies", out var nested))
                {
                    array = nested;
                }
                if (array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        var name = Get(item, "name");
                        var value = Get(item, "value");
                        if (!string.IsNullOrEmpty(name)) result.Add((name!, value ?? ""));
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch
            {
                // Fall through to the plain cookie header parser.
            }
        }

        var text = raw.Replace("\r", "\n");
        if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }

        foreach (var line in text.Split('\n'))
        {
            var source = line;
            // Netscape cookie file: domain, flag, path, secure, expiry, name, value.
            if (source.Contains('\t'))
            {
                var parts = source.Split('\t');
                if (parts.Length >= 7 && !parts[0].TrimStart().StartsWith("#"))
                {
                    result.Add((parts[5].Trim(), parts[6].Trim()));
                    continue;
                }
            }

            foreach (var piece in source.Split(';'))
            {
                var item = piece.Trim();
                if (item.Length == 0) continue;
                var idx = item.IndexOf('=');
                if (idx <= 0) continue;
                var name = item[..idx].Trim();
                var value = item[(idx + 1)..].Trim();
                var lower = name.ToLowerInvariant();
                if (lower is "path" or "domain" or "expires" or "max-age" or "secure" or "httponly" or "samesite")
                {
                    continue;
                }
                if (name.Any(char.IsWhiteSpace)) continue;
                result.Add((name, value));
            }
        }

        return result
            .GroupBy(x => x.Item1, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
    }

    private static string? Get(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}
