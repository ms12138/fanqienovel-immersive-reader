using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FanqieReader.Models;

namespace FanqieReader.Services;

public sealed class FanqieException : Exception
{
    public FanqieException(string message) : base(message) { }
}

public sealed partial class FanqieClient : IDisposable
{
    private const string BaseUrl = "https://fanqienovel.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36 Edg/152.0.0.0";

    /// <summary>
    /// The web reader identifies itself with aid=1967. Without it the bookshelf API
    /// answers 100103 PARAM_INVALID, which is what used to make the shelf look broken.
    /// </summary>
    private const string ShelfQuery = "aid=1967&from=web_novel_pc&app_name=muye_novel";

    private static readonly string[] ShelfInfoPaths =
    {
        "/reading/bookapi/bookshelf/info/v1/?" + ShelfQuery,
        "/reading/bookapi/bookshelf/info/v0/?" + ShelfQuery,
        "/reading/bookapi/bookshelf/info/v2/?" + ShelfQuery,
        "/reading/bookapi/bookshelf/info/v1/?aid=1967"
    };

    private readonly HttpClient _http;
    private readonly Func<Task<string>> _cookieProvider;
    private readonly FanqieParser _parser = new();
    private readonly Dictionary<string, string> _fontCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _fontOrder = new();

    public FanqieClient(Func<Task<string>> cookieProvider)
    {
        _cookieProvider = cookieProvider;
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");
    }

    public async Task<UserProfile> GetUserAsync()
    {
        using var doc = await GetJsonAsync(BaseUrl + "/api/reader/user/info");
        var root = doc.RootElement;
        if (GetInt(root, "code") != 0)
        {
            return new UserProfile();
        }

        var data = GetObject(root, "data");
        if (data is null) return new UserProfile();

        var id = GetString(data.Value, "id") ?? GetString(data.Value, "user_id") ?? "";
        if (id.Length == 0) return new UserProfile();

        return new UserProfile
        {
            LoggedIn = true,
            Id = id,
            Name = GetString(data.Value, "name")
                   ?? GetString(data.Value, "nickname")
                   ?? GetString(data.Value, "screen_name")
                   ?? "番茄读者",
            Avatar = GetString(data.Value, "avatar")
                     ?? GetString(data.Value, "avatar_url")
                     ?? ""
        };
    }

    public async Task<ChapterDocument> GetChapterAsync(string chapterId)
    {
        chapterId = ExtractDigits(chapterId);
        if (chapterId.Length < 6) throw new FanqieException("章节编号无效");

        var url = $"{BaseUrl}/reader/{chapterId}";
        var html = await GetStringAsync(url);
        var chapter = _parser.ParseChapter(html, chapterId);
        if (chapter.Paragraphs.Count == 0)
        {
            throw new FanqieException("没有取到正文。该章节可能需要登录，或已被下架。");
        }

        if (chapter.FontUrls.Count > 0)
        {
            foreach (var fontUrl in chapter.FontUrls)
            {
                try
                {
                    var dataUrl = await GetFontDataUrlAsync(fontUrl);
                    if (!string.IsNullOrEmpty(dataUrl))
                    {
                        chapter.FontDataUrl = dataUrl;
                        break;
                    }
                }
                catch
                {
                    // Try the next CDN / format before giving up on the chapter font.
                }
            }
        }

        if (string.IsNullOrWhiteSpace(chapter.BookName))
        {
            chapter.BookName = "番茄小说";
        }
        if (string.IsNullOrWhiteSpace(chapter.Title))
        {
            chapter.Title = "第 " + chapterId + " 章";
        }

        return chapter;
    }

    public async Task<CatalogResult> GetCatalogAsync(string bookId)
    {
        bookId = ExtractDigits(bookId);
        if (bookId.Length < 6) throw new FanqieException("书籍编号无效");

        var html = await GetStringAsync($"{BaseUrl}/page/{bookId}");
        var catalog = _parser.ParseCatalog(html, bookId);
        if (catalog.Chapters.Count == 0)
        {
            throw new FanqieException("目录加载失败，可能是网络波动或页面改版。");
        }
        return catalog;
    }

    public async Task<string> ResolveFirstChapterAsync(string bookId)
    {
        var catalog = await GetCatalogAsync(bookId);
        var first = _parser.FirstChapter(catalog);
        if (first is null) throw new FanqieException("这本书暂时没有可阅读的章节");
        return first.Id;
    }

    private async Task<string> GetFontDataUrlAsync(string fontUrl)
    {
        if (_fontCache.TryGetValue(fontUrl, out var cached)) return cached;

        using var response = await _http.GetAsync(fontUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0 || bytes.Length > 4_000_000) return "";

        var path = fontUrl.Split('?')[0].Split('#')[0];
        var mime = path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ? "font/woff2"
            : path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ? "font/woff"
            : "font/otf";
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

        _fontCache[fontUrl] = dataUrl;
        _fontOrder.Enqueue(fontUrl);
        while (_fontOrder.Count > 24)
        {
            var oldest = _fontOrder.Dequeue();
            _fontCache.Remove(oldest);
        }
        return dataUrl;
    }

    private async Task<string> GetStringAsync(string url, string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyHeadersAsync(request, referer);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new FanqieException($"网络请求失败（HTTP {(int)response.StatusCode}）");
        }
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string referer = BaseUrl + "/")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyHeadersAsync(request, referer);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new FanqieException($"接口请求失败（HTTP {(int)response.StatusCode}）");
        }
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            throw new FanqieException("接口返回了无法识别的内容，可能触发了风控。");
        }
    }

    private async Task<JsonDocument> PostJsonAsync(string url, object payload, string referer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await ApplyHeadersAsync(request, referer);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new FanqieException($"接口请求失败（HTTP {(int)response.StatusCode}）");
        }
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            throw new FanqieException("接口返回了无法识别的内容");
        }
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage request, string? referer)
    {
        var cookie = await _cookieProvider();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        request.Headers.Referrer = new Uri(referer ?? BaseUrl + "/");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    public static string ExtractDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(input, @"(\d{6,})");
        return match.Success ? match.Groups[1].Value : input.Trim();
    }

    private static JsonElement? GetObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object) return value;
        if (value.ValueKind == JsonValueKind.String)
        {
            // Some endpoints double-encode the payload.
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var inner = JsonDocument.Parse(text);
                    return inner.RootElement.Clone();
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static JsonElement? GetArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

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

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
        return null;
    }

    public void Dispose() => _http.Dispose();
}
