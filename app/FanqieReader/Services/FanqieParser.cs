using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FanqieReader.Models;

namespace FanqieReader.Services;

public sealed partial class FanqieParser
{
    private static readonly IHtmlParser HtmlParser = new HtmlParser();

    private static readonly Regex ChapterNextRegex = new("\"nextItemId\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
    private static readonly Regex ChapterPrevRegex = new("\"preItemId\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
    private static readonly Regex BookIdRegex = new("\"bookId\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
    private static readonly Regex WordCountRegex = new("\"chapterWordNumber\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
    private static readonly Regex NeedPayRegex = new("\"needPay\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
    private static readonly Regex NeedleWhitespace = new(@"[ \t\f\v\u00A0\u3000]+", RegexOptions.Compiled);

    private static readonly Regex FontFaceRegex = new(
        @"@font-face\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FontFamilyDeclRegex = new(
        @"font-family\s*:\s*(?<value>[^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FontWeightDeclRegex = new(
        @"font-weight\s*:\s*(?<value>[^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FontUrlRegex = new(
        @"url\(\s*(?<quote>['""]?)(?<url>[^)'""]+)\k<quote>\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SerifHints =
    {
        "serif", "songti", "stsong", "simsun", "song", "ming", "kai", "fangsong", "宋", "楷", "仿宋"
    };

    private static readonly string[] PreviewMarkers =
    {
        "扫码下载APP免费读",
        "会员登录后，可在网页畅读全文",
        "SVIP网页畅读"
    };

    public ChapterDocument ParseChapter(string html, string requestedId)
    {
        var doc = HtmlParser.ParseDocument(html);

        var title = Clean(doc.QuerySelector(".muye-reader-title")?.TextContent)
                    ?? Clean(doc.QuerySelector(".muye-reader-box-header h1")?.TextContent)
                    ?? Clean(doc.QuerySelector("title")?.TextContent)?.Split('_')[0]
                    ?? "";

        var bookName = Clean(doc.QuerySelector(".muye-reader-nav-title")?.TextContent)
                       ?? ReadJsonString(html, "bookName")
                       ?? "";

        var contentNode = doc.QuerySelector(".muye-reader-content")
                          ?? doc.QuerySelector(".muye-reader-box .content")
                          ?? doc.QuerySelector("article");

        var paragraphs = new List<string>();
        if (contentNode is not null)
        {
            foreach (var p in contentNode.QuerySelectorAll("p"))
            {
                var text = NormalizeParagraph(p.TextContent);
                if (text.Length > 0) paragraphs.Add(text);
            }

            if (paragraphs.Count == 0)
            {
                var raw = contentNode.TextContent ?? "";
                foreach (var line in raw.Replace("\r", "").Split('\n'))
                {
                    var text = NormalizeParagraph(line);
                    if (text.Length > 0) paragraphs.Add(text);
                }
            }
        }

        var bookId = MatchValue(BookIdRegex, html) ?? "";
        var next = MatchValue(ChapterNextRegex, html) ?? "";
        var prev = MatchValue(ChapterPrevRegex, html) ?? "";
        if (next == "0") next = "";
        if (prev == "0") prev = "";

        var words = int.TryParse(MatchValue(WordCountRegex, html), out var w) ? w : 0;
        var needPay = MatchValue(NeedPayRegex, html) == "1";
        var preview = needPay || PreviewMarkers.Any(marker => html.Contains(marker, StringComparison.Ordinal));

        var author = ReadJsonString(html, "author") ?? "";
        var font = ExtractChapterFont(doc);

        return new ChapterDocument
        {
            BookId = bookId,
            BookName = bookName,
            ChapterId = string.IsNullOrEmpty(requestedId) ? "" : requestedId,
            Title = title,
            Author = author,
            PrevId = prev,
            NextId = next,
            WordCount = words,
            IsPreview = preview,
            Paragraphs = paragraphs,
            FontFamily = font.Family,
            FontKind = font.Kind,
            FontUrls = font.Urls,
            FontDataUrl = "",
            SourceUrl = $"https://fanqienovel.com/reader/{requestedId}"
        };
    }

    public CatalogResult ParseCatalog(string html, string bookId)
    {
        var doc = HtmlParser.ParseDocument(html);
        var chapters = new List<CatalogChapter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in doc.QuerySelectorAll("a[href*=\"/reader/\"]"))
        {
            var href = a.GetAttribute("href") ?? "";
            var match = Regex.Match(href, @"/reader/(\d+)");
            if (!match.Success) continue;
            var id = match.Groups[1].Value;
            if (!seen.Add(id)) continue;

            var title = Clean(a.TextContent) ?? "";
            title = title.Replace("最近更新：", "", StringComparison.Ordinal)
                .Replace("最近更新:", "", StringComparison.Ordinal)
                .Trim();
            if (title.Length == 0) continue;
            if (title.Contains("最新章节", StringComparison.Ordinal)) continue;

            chapters.Add(new CatalogChapter { Id = id, Title = title });
        }

        var bookName = Clean(doc.QuerySelector("h1")?.TextContent)
                       ?? Clean(doc.QuerySelector(".book-name")?.TextContent)
                       ?? "";

        // The "最近更新" card is rendered before the catalogue, so put numeric
        // chapters back in reading order. Books without numbers keep DOM order.
        var numbered = chapters
            .Select(c => (Chapter: c, Match: Regex.Match(c.Title, @"第\s*(\d+)\s*[章节回卷]")))
            .Where(x => x.Match.Success)
            .ToList();
        if (numbered.Count >= Math.Max(2, chapters.Count / 2))
        {
            chapters = chapters
                .OrderBy(c =>
                {
                    var m = Regex.Match(c.Title, @"第\s*(\d+)\s*[章节回卷]");
                    return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
                })
                .ThenBy(c => c.Title, StringComparer.Ordinal)
                .ToList();
        }

        return new CatalogResult
        {
            BookId = bookId,
            BookName = bookName,
            SerialCount = chapters.Count,
            Chapters = chapters
        };
    }

    /// <summary>Find the first chapter of a book, used when only a book id is supplied.</summary>
    public CatalogChapter? FirstChapter(CatalogResult catalog) =>
        catalog.Chapters.Count > 0 ? catalog.Chapters[0] : null;

    // ------------------------------------------------------------------
    // chapter font (dynamic WOFF obfuscation)
    // ------------------------------------------------------------------

    private sealed class ChapterFont
    {
        public string Family { get; init; } = "";
        public string Kind { get; init; } = "sans";
        public List<string> Urls { get; init; } = new();
    }

    /// <summary>
    /// Reads the chapter's own @font-face declarations instead of matching the first
    /// .woff2 in the document. Fanqie ships a per-book subset that only contains the
    /// Private-Use glyphs used to mask the real characters, so the surrounding
    /// fallback stack has to match the font's design (sans vs serif) or the paragraph
    /// renders in two different typefaces.
    /// </summary>
    private static ChapterFont ExtractChapterFont(IDocument doc)
    {
        var faces = new List<(string Family, int Weight, List<string> Urls)>();

        foreach (var style in doc.QuerySelectorAll("style"))
        {
            var css = style.TextContent ?? "";
            if (css.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) < 0) continue;

            foreach (Match match in FontFaceRegex.Matches(css))
            {
                var body = match.Groups["body"].Value;
                var faceFamily = ReadDeclaration(FontFamilyDeclRegex, body).Trim().Trim('\'', '"');
                if (faceFamily.Length == 0) continue;

                var urls = new List<string>();
                foreach (Match urlMatch in FontUrlRegex.Matches(body))
                {
                    var url = NormalizeFontUrl(urlMatch.Groups["url"].Value);
                    if (url.Length > 0 && !urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
                }
                if (urls.Count == 0) continue;

                var weightText = ReadDeclaration(FontWeightDeclRegex, body).Trim();
                var weight = int.TryParse(weightText, out var parsedWeight) ? parsedWeight : 400;

                // Merge repeated declarations of the same family/weight (the page
                // repeats each face once per CDN host in some snapshots).
                var existingIndex = faces.FindIndex(f =>
                    string.Equals(f.Family, faceFamily, StringComparison.OrdinalIgnoreCase) && f.Weight == weight);
                if (existingIndex >= 0)
                {
                    var merged = faces[existingIndex].Urls;
                    foreach (var url in urls)
                    {
                        if (!merged.Contains(url, StringComparer.OrdinalIgnoreCase)) merged.Add(url);
                    }
                }
                else
                {
                    faces.Add((faceFamily, weight, urls));
                }
            }
        }

        if (faces.Count == 0)
        {
            return new ChapterFont();
        }

        // The container carries the real family list, e.g.
        // <div class="muye-reader-box font-xxx" style="font-family:DNMrHsV173Pd4pgy, -apple-system, 'PingFang SC', ...">
        var container = doc.QuerySelector(".muye-reader-box") ?? doc.QuerySelector(".muye-reader-content");
        var containerStyle = container?.GetAttribute("style") ?? "";
        var declared = ParseFontFamilyList(containerStyle);

        var family = faces[0].Family;
        if (declared.Count > 0)
        {
            var wanted = declared[0];
            var known = faces.FirstOrDefault(f => string.Equals(f.Family, wanted, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(known.Family)) family = known.Family;
        }

        var matching = faces.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) matching = faces;

        // Body text is regular weight; 400 (or the lightest available face) avoids
        // the browser synthesising a fake bold from a 500/700 face.
        var preferred = matching.FirstOrDefault(f => f.Weight == 400);
        if (string.IsNullOrEmpty(preferred.Family))
        {
            preferred = matching.OrderBy(f => Math.Abs(f.Weight - 400)).First();
        }

        var ordered = preferred.Urls
            .OrderBy(u => FontFormatRank(u))
            .ThenBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var kind = DetectFontKind(declared);
        return new ChapterFont { Family = family, Kind = kind, Urls = ordered };
    }

    private static string ReadDeclaration(Regex regex, string body)
    {
        var match = regex.Match(body);
        return match.Success ? match.Groups["value"].Value.Trim() : "";
    }

    private static List<string> ParseFontFamilyList(string style)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(style)) return result;

        var decl = FontFamilyDeclRegex.Match(style);
        if (!decl.Success) return result;

        foreach (var piece in decl.Groups["value"].Value.Split(','))
        {
            var name = piece.Trim().Trim('\'', '"').Trim();
            if (name.Length == 0) continue;
            result.Add(name);
        }
        return result;
    }

    private static string DetectFontKind(List<string> families)
    {
        foreach (var family in families)
        {
            var lower = family.ToLowerInvariant();

            // "sans-serif" also contains "serif", so the sans families win first.
            if (lower.Contains("sans", StringComparison.Ordinal)) continue;
            if (lower is "serif" or "ui-serif" or "ms-serif") return "serif";
            if (SerifHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal))) return "serif";
        }

        // No family list: the shipped subsets so far are SourceHanSansSC based.
        return "sans";
    }

    private static int FontFormatRank(string url)
    {
        var path = ExtractPath(url);
        if (path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static string ExtractPath(string url)
    {
        var noQuery = url.Split('?')[0].Split('#')[0];
        return noQuery;
    }

    private static string NormalizeFontUrl(string raw)
    {
        var url = WebUtility.HtmlDecode(raw ?? "").Trim();
        if (url.Length == 0) return "";

        url = url.Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                 .Replace("\\u002f", "/", StringComparison.Ordinal)
                 .Replace("\\/", "/")
                 .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
                 .Trim();

        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var path = ExtractPath(url);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return url;
    }

    private static string? MatchValue(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadJsonString(string html, string property)
    {
        var regex = new Regex("\"" + Regex.Escape(property) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        foreach (Match match in regex.Matches(html))
        {
            string? value;
            try
            {
                value = JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"");
            }
            catch
            {
                value = match.Groups[1].Value;
            }
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = WebUtility.HtmlDecode(text)
            .Replace('\u00A0', ' ')
            .Replace('\u200B', '\0')
            .Replace("\0", "")
            .Trim();
        return value.Length == 0 ? null : value;
    }

    private static string NormalizeParagraph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = WebUtility.HtmlDecode(text)
            .Replace('\u00A0', ' ')
            .Replace('\u200B', '\0')
            .Replace("\0", "");

        // Only collapse ASCII/ideographic padding between words; never insert or keep
        // stray blanks between CJK characters (they are what broke the line rhythm).
        value = NeedleWhitespace.Replace(value, " ");
        value = Regex.Replace(value, @"(?<=[\u4E00-\u9FFF\u3000-\u303F\uFF00-\uFFEF])\s+(?=[\u4E00-\u9FFF\u3000-\u303F\uFF00-\uFFEF])", "");
        return value.Trim();
    }
}
