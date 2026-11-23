using System.Net;
using System.Text.Json;

namespace FanqieReader.Services;

public sealed class QrStartResult
{
    public string Token { get; set; } = "";
    public string QrDataUrl { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string Message { get; set; } = "";
}

public sealed class QrPollResult
{
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public bool HasSessionCookie { get; set; }
    public string CookieHeader { get; set; } = "";
}

/// <summary>
/// Direct implementation of the official Douyin/Fanqie passport QR flow.
/// The captured get_qrcode/check_qrconnect requests are deliberately minimal,
/// which keeps the reader independent from the author login page UI.
/// </summary>
public sealed class QrLoginService : IDisposable
{
    private const string BaseUrl = "https://fanqienovel.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36 Edg/152.0.0.0";

    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;

    public QrLoginService()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _jar,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript");
        _http.DefaultRequestHeaders.Referrer = new Uri(BaseUrl + "/main/writer/login");
    }

    public async Task<QrStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var next = Uri.EscapeDataString(BaseUrl + "/main/writer/login");
        var url = BaseUrl + "/passport/web/get_qrcode/?" +
                  $"next={next}&need_logo=true&passport_jssdk_version=3.0.16" +
                  "&passport_jssdk_type=normal&aid=2503&language=zh&account_sdk_source=web";

        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            throw new FanqieException("二维码接口没有返回数据");
        }

        var qr = GetString(data, "qrcode") ?? "";
        var token = GetString(data, "token") ?? "";
        if (qr.Length == 0 || token.Length == 0)
        {
            throw new FanqieException("没有取到二维码，请稍后重试");
        }

        var expire = GetLong(data, "expire_time");
        return new QrStartResult
        {
            Token = token,
            QrDataUrl = "data:image/png;base64," + qr,
            ExpiresAt = expire > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expire)
                : DateTimeOffset.UtcNow.AddMinutes(3)
        };
    }

    public async Task<QrPollResult> PollAsync(string token, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string>
        {
            ["next"] = "/",
            ["token"] = token,
            ["passport_jssdk_version"] = "3.0.16",
            ["passport_jssdk_type"] = "normal",
            ["aid"] = "2503",
            ["language"] = "zh",
            ["account_sdk_source"] = "web"
        };
        var url = BaseUrl + "/passport/web/check_qrconnect/?" +
                  string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var status = "";
        var description = "";
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            status = GetString(data, "status") ?? "";
            description = GetString(data, "description") ?? "";
        }

        var cookies = _jar.GetCookies(new Uri(BaseUrl));
        var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new QrPollResult
        {
            Status = status,
            Description = description,
            HasSessionCookie = names.Contains("sessionid") || names.Contains("sessionid_ss") || names.Contains("sid_guard"),
            CookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"))
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out n)) return n;
        return 0;
    }

    public void Dispose() => _http.Dispose();
}
