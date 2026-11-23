using System.Text.Json.Serialization;

namespace FanqieReader.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "day";
    public int FontSize { get; set; } = 19;
    public int Opacity { get; set; } = 100;
    public bool EdgeHide { get; set; }
    public bool Immersive { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 760;
    public string? LastBookId { get; set; }
    public string? LastChapterId { get; set; }
    public string? LastBookName { get; set; }
    public string? LastChapterTitle { get; set; }
    public double LastScrollRatio { get; set; }

    /// <summary>WebView2 配置目录后缀：登录态被占用/不可写时自动换成 -2、-3。</summary>
    public string WebViewProfileSuffix { get; set; } = "";

    public void Clamp()
    {
        if (Theme is not ("day" or "night")) Theme = "day";
        FontSize = Math.Clamp(FontSize, 12, 32);
        Opacity = Math.Clamp(Opacity, 30, 100);
        WindowWidth = Math.Clamp(WindowWidth, 460, 2400);
        WindowHeight = Math.Clamp(WindowHeight, 360, 1800);
        LastScrollRatio = Math.Clamp(LastScrollRatio, 0, 1);
        if (WebViewProfileSuffix is not ("") and not ("-2") and not ("-3") and not ("-4")) WebViewProfileSuffix = "";
    }
}

public sealed class UserProfile
{
    public bool LoggedIn { get; set; }
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Id { get; set; } = "";
}

public sealed class BookSummary
{
    public string BookId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ChapterId { get; set; } = "";

    /// <summary>Chapter sequence to resume at when only the order is known (1 based).</summary>
    public int? StartOrder { get; set; }

    public int SerialCount { get; set; }
    public string StatusText { get; set; } = "";

    /// <summary>False when the API did not tell us whether the book is finished.</summary>
    public bool StatusKnown { get; set; }

    public bool IsFinished { get; set; }
    public string LastChapterTitle { get; set; } = "";

    /// <summary>Human readable reading position, e.g. "读到 100 章".</summary>
    public string ReadText { get; set; } = "";
}

public sealed class CatalogChapter
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class ChapterDocument
{
    public string BookId { get; set; } = "";
    public string BookName { get; set; } = "";
    public string ChapterId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string PrevId { get; set; } = "";
    public string NextId { get; set; } = "";
    public int WordCount { get; set; }
    public bool IsPreview { get; set; }
    public List<string> Paragraphs { get; set; } = new();

    /// <summary>Real @font-face family name used by the chapter (e.g. "DNMrHsV173Pd4pgy").</summary>
    public string FontFamily { get; set; } = "";

    /// <summary>"sans" or "serif" — the design of the chapter font, so the fallback stack can match it.</summary>
    public string FontKind { get; set; } = "sans";

    public string FontDataUrl { get; set; } = "";

    /// <summary>Candidate font URLs in preference order. Server side only.</summary>
    [JsonIgnore]
    public List<string> FontUrls { get; set; } = new();

    public string SourceUrl { get; set; } = "";
}

public sealed class CatalogResult
{
    public string BookId { get; set; } = "";
    public string BookName { get; set; } = "";
    public int SerialCount { get; set; }
    public List<CatalogChapter> Chapters { get; set; } = new();
}

public sealed class ApiMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    public string? Value { get; set; }
    public bool? Enabled { get; set; }
    public string? Text { get; set; }
    public string? BookId { get; set; }
    public string? ChapterId { get; set; }
    public string? Url { get; set; }
    public double? Ratio { get; set; }
    public double? Level { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Order { get; set; }
}
