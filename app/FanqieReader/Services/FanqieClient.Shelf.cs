using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FanqieReader.Models;

namespace FanqieReader.Services;

/// <summary>
/// Talks to the same endpoints the web reader uses. The shelf is a three step dance:
/// <c>bookshelf/info</c> only returns ids, <c>/api/book/simple/info</c> returns the
/// book metadata (author, 连载/完结, 总章数) and <c>/api/reader/book/progress</c>
/// returns where the account stopped reading.
/// </summary>
public sealed partial class FanqieClient
{

    public async Task<List<BookSummary>> GetBookshelfAsync()
    {
        Exception? lastError = null;
        string lastMessage = "";
        JsonDocument? info = null;
        var sawLoginRequired = false;

        foreach (var path in ShelfInfoPaths)
        {
            try
            {
                var doc = await GetJsonAsync(BaseUrl + path, referer: BaseUrl + "/bookshelf");
                var code = GetInt(doc.RootElement, "code") ?? -1;
                if (code == 0)
                {
                    info = doc;
                    break;
                }

                var message = GetString(doc.RootElement, "message") ?? "";
                if (code is -1 or 101119 || message.Contains("登录", StringComparison.Ordinal))
                {
                    sawLoginRequired = true;
                }
                if (message.Length > 0) lastMessage = message;
                lastError = new FanqieException(DescribeShelfError(code, message));
                doc.Dispose();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (info is null)
        {
            if (sawLoginRequired)
            {
                throw new FanqieException("登录状态已失效，请重新扫码或粘贴 Cookie 登录后再打开书架。");
            }
            throw lastError ?? new FanqieException("书架加载失败" + (lastMessage.Length > 0 ? "：" + lastMessage : ""));
        }

        var entries = new Dictionary<string, ShelfEntry>(StringComparer.Ordinal);
        var diag = new List<object>();
        using (info)
        {
            diag.Add(new { endpoint = "bookshelf/info", code = GetInt(info.RootElement, "code"), data = Sample(info.RootElement.GetRawText()) });
            var data = GetObject(info.RootElement, "data");
            if (data is null)
            {
                WriteDiagnostics(diag);
                return new List<BookSummary>();
            }

            // Shelf items sometimes nest a full book object (author, 完结状态, 总章数),
            // so harvest every book-shaped object first, then overlay the shelf-only
            // fields (阅读进度 / item_id) on top.
            CollectBooks(data.Value, entries);

            var rawItems = GetArray(data.Value, "book_shelf_info")
                           ?? GetArray(data.Value, "books")
                           ?? GetArray(data.Value, "book_list")
                           ?? GetArray(data.Value, "bookShelfInfo");

            if (rawItems is not null)
            {
                foreach (var item in rawItems.Value.EnumerateArray())
                {
                    var id = GetString(item, "book_id") ?? GetString(item, "bookId") ?? "";
                    if (id.Length == 0) continue;
                    MergeEntry(entries, ReadEntry(item, id));
                }
            }
        }

        if (entries.Count == 0) return new List<BookSummary>();

        // Book metadata (author / 完结 / 总章数) — one request for the whole shelf.
        var details = await GetBookDetailsAsync(entries.Keys.ToList(), diag);
        foreach (var pair in details)
        {
            if (entries.TryGetValue(pair.Key, out var entry)) entry.MergeFrom(pair.Value);
        }

        // Where the account actually stopped reading.
        var progress = await GetReadProgressAsync(diag);
        foreach (var pair in progress)
        {
            if (!entries.TryGetValue(pair.Key, out var entry)) continue;
            if (pair.Value.ItemId.Length > 0) entry.ProgressItemId = pair.Value.ItemId;
            if (pair.Value.Order > 0) entry.Order = pair.Value.Order;
        }

        await EnrichReadOrderAsync(entries, diag);

        var result = entries.Values.Select(e => e.ToSummary()).ToList();
        diag.Add(new
        {
            endpoint = "summary",
            books = result.Count,
            withAuthor = result.Count(b => b.Author.Length > 0),
            withProgress = result.Count(b => !string.IsNullOrEmpty(b.ReadText)),
            withChapter = result.Count(b => b.ChapterId.Length > 0)
        });
        WriteDiagnostics(diag);
        return result;
    }

    /// <summary>Wired to the app log so failed metadata calls are diagnosable.</summary>
    public static Action<string>? Trace { get; set; }

    private static void LogTrace(string message) => Trace?.Invoke("[shelf] " + message);

    /// <summary>
    /// 把一次书架加载的原始应答写到 %LOCALAPPDATA%\FanqieReader\logs\shelf-dump.json，
    /// 方便对着真实字段改解析（字段名随官方改版变化，靠猜不可靠）。
    /// </summary>
    private static void WriteDiagnostics(IReadOnlyList<object> entries)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FanqieReader",
                "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "shelf-dump.json");
            var payload = new
            {
                at = DateTime.Now.ToString("O"),
                attempts = entries
            };
            File.WriteAllText(file, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            LogTrace("dump written: " + file);
        }
        catch (Exception ex)
        {
            LogTrace("dump failed: " + ex.Message);
        }
    }

    /// <summary>取样时只保留数组前两项，避免 248 本书撑爆日志。</summary>
    private static object Sample(string raw, int maxItems = 2)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return Trim(doc.RootElement, maxItems)!;
        }
        catch
        {
            return raw.Length > 600 ? raw[..600] + "…" : raw;
        }
    }

    private static System.Text.Json.Nodes.JsonNode? Trim(JsonElement element, int maxItems, int depth = 0)
    {
        if (depth > 8) return JsonValue.Create("…");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = Trim(property.Value, maxItems, depth + 1);
                }
                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray().Take(maxItems))
                {
                    array.Add(Trim(item, maxItems, depth + 1));
                }
                return array;

            case JsonValueKind.String:
                var text = element.GetString() ?? "";
                return JsonValue.Create(text.Length > 160 ? text[..160] + "…" : text);

            case JsonValueKind.Number:
                return element.TryGetInt64(out var number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(element.GetDouble());

            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private static string DescribeShelfError(int code, string message) => code switch
    {
        100103 => "书架接口参数被拒绝（可能已改版）",
        101119 => "书架读取失败，登录状态可能已失效",
        -1 => "请先登录番茄账号",
        _ => string.IsNullOrWhiteSpace(message) ? $"书架接口返回异常（{code}）" : message
    };

    // ------------------------------------------------------------------
    // shelf entry (ids + whatever metadata the shelf happens to include)
    // ------------------------------------------------------------------

    private sealed class ShelfEntry
    {
        public string BookId = "";
        public string Title = "";
        public string Author = "";
        public string LastChapterTitle = "";
        public string LastChapterItemId = "";
        public string ProgressItemId = "";
        public int SerialCount;
        public int Order;              // 0 = unknown（multidetail 的 order 只是第一章序号，别当进度用）
        public int CreationStatus = -1; // -1 = unknown, 0 = 完结, 1 = 连载
        public int ReadOrder;          // 读到第几章（靠 progress 的 item_id 反查目录得到）
        public int ReadTotal;          // 目录总章数

        public void MergeFrom(ShelfEntry other)
        {
            if (Title.Length == 0) Title = other.Title;
            if (Author.Length == 0) Author = other.Author;
            if (LastChapterTitle.Length == 0) LastChapterTitle = other.LastChapterTitle;
            if (LastChapterItemId.Length == 0) LastChapterItemId = other.LastChapterItemId;
            if (ProgressItemId.Length == 0) ProgressItemId = other.ProgressItemId;
            if (SerialCount == 0) SerialCount = other.SerialCount;
            if (Order == 0) Order = other.Order;
            if (CreationStatus < 0) CreationStatus = other.CreationStatus;
        }

        public BookSummary ToSummary()
        {
            var finished = CreationStatus == 0;
            var read = DescribeRead(ReadOrder, ReadTotal, CreationStatus);
            return new BookSummary
            {
                BookId = BookId,
                Title = Title.Length > 0 ? Title : "书籍 " + BookId,
                Author = Author,
                ChapterId = ProgressItemId,
                StartOrder = ReadOrder > 0 ? ReadOrder : null,
                SerialCount = SerialCount,
                IsFinished = finished,
                StatusKnown = CreationStatus >= 0,
                StatusText = CreationStatus < 0 ? "" : finished ? "已完结" : "连载中",
                LastChapterTitle = LastChapterTitle,
                ReadText = read
            };
        }
    }

    private static ShelfEntry ReadEntry(JsonElement item, string id)
    {
        var entry = new ShelfEntry
        {
            BookId = id,
            Title = GetString(item, "book_name")
                    ?? GetString(item, "bookName")
                    ?? GetString(item, "book_title")
                    ?? "",
            Author = GetString(item, "author")
                     ?? GetString(item, "author_name")
                     ?? GetString(item, "authorName")
                     ?? GetString(item, "pen_name")
                     ?? GetString(item, "penName")
                     ?? "",
            SerialCount = GetInt(item, "serial_count") ?? GetInt(item, "serialCount") ?? 0,
            // "order" on a shelf entry is not the reading position; the book model
            // carries real_chapter_order and the progress record carries index.
            Order = GetInt(item, "real_chapter_order") ?? GetInt(item, "realChapterOrder") ?? 0,
            CreationStatus = GetInt(item, "creation_status") ?? GetInt(item, "creationStatus") ?? -1
        };

        entry.LastChapterTitle = GetString(item, "last_chapter_title")
                                 ?? GetString(item, "lastChapterTitle")
                                 ?? "";
        entry.LastChapterItemId = FirstId(
            GetString(item, "last_chapter_item_id"),
            GetString(item, "lastChapterItemId"));
        entry.ProgressItemId = FirstId(
            GetString(item, "latest_read_item_id"),
            GetString(item, "last_read_item_id"),
            GetString(item, "read_item_id"),
            GetString(item, "item_id"),
            GetString(item, "itemId"));
        return entry;
    }

    private static void MergeEntry(Dictionary<string, ShelfEntry> sink, ShelfEntry entry)
    {
        if (sink.TryGetValue(entry.BookId, out var existing)) existing.MergeFrom(entry);
        else sink[entry.BookId] = entry;
    }

    private static string DescribeRead(int readOrder, int total, int creationStatus)
    {
        if (readOrder <= 0) return "";
        if (total > 0 && readOrder >= total) return creationStatus == 0 ? "已读完" : "读到最新章";
        return $"读到 {readOrder} 章";
    }

    // ------------------------------------------------------------------
    // book metadata
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, ShelfEntry>> GetBookDetailsAsync(List<string> bookIds, List<object> diag)
    {
        var result = new Dictionary<string, ShelfEntry>(StringComparer.Ordinal);
        if (bookIds.Count == 0) return result;

        // 1) /api/bookshelf/multidetail (the shelf's own detail endpoint)
        foreach (var asNumbers in new[] { false, true })
        {
            try
            {
                object payload = asNumbers
                    ? new { books = bookIds.Select(id => new { book_id = ToNumberOrString(id), item_id = 0 }).ToArray() }
                    : new { books = bookIds.Select(id => new { book_id = id, item_id = "0" }).ToArray() };

                using var doc = await PostJsonAsync(
                    BaseUrl + "/api/bookshelf/multidetail?" + ShelfQuery,
                    payload,
                    referer: BaseUrl + "/bookshelf");

                var code = GetInt(doc.RootElement, "code");
                if (code == 0)
                {
                    CollectBooks(doc.RootElement, result);
                    LogTrace($"multidetail ok, books={result.Count} numbers={asNumbers}");
                    diag.Add(new { endpoint = "multidetail-ok", code, numbers = asNumbers, books = result.Count, data = Sample(doc.RootElement.GetRawText()) });
                    // multidetail 只给书架自己的字段（书名/章数/进度），作者要靠 simple/info，
                    // 所以这里不提前返回，缺作者就继续问下一个接口。
                    if (!NeedsAuthor(result)) return result;
                }
                else
                {
                    LogTrace($"multidetail code={code} msg={GetString(doc.RootElement, "message")} numbers={asNumbers}");
                    diag.Add(new { endpoint = "multidetail", code, numbers = asNumbers, data = Sample(doc.RootElement.GetRawText()) });
                }
            }
            catch (Exception ex)
            {
                LogTrace("multidetail failed: " + ex.Message);
            }
        }

        // 2) /api/book/simple/info — what the official shelf page uses
        foreach (var asNumbers in new[] { false, true })
        {
            try
            {
                object ids = asNumbers
                    ? bookIds.Select(ToNumberOrString).ToArray()
                    : bookIds.ToArray();

                using var doc = await PostJsonAsync(
                    BaseUrl + "/api/book/simple/info?" + ShelfQuery,
                    new { book_ids = ids },
                    referer: BaseUrl + "/bookshelf");

                var code = GetInt(doc.RootElement, "code");
                if (code == 0)
                {
                    CollectBooks(doc.RootElement, result);
                    LogTrace($"simple/info ok, books={result.Count} numbers={asNumbers}");
                    diag.Add(new { endpoint = "simple/info-ok", code, numbers = asNumbers, books = result.Count, data = Sample(doc.RootElement.GetRawText()) });
                    if (!NeedsAuthor(result)) return result;
                }
                else
                {
                    LogTrace($"simple/info code={code} msg={GetString(doc.RootElement, "message")} numbers={asNumbers}");
                    diag.Add(new { endpoint = "simple/info", code, numbers = asNumbers, data = Sample(doc.RootElement.GetRawText()) });
                }
            }
            catch (Exception ex)
            {
                LogTrace("simple/info failed: " + ex.Message);
            }
        }

        return result;
    }

    /// <summary>
    /// 书架接口只给出"上次读的章节 id"，没有章号；这里对**确实有阅读记录**的书
    /// 抓一次目录，把章节 id 换成"第 N 章"。没有记录的书就不显示进度，不编造。
    /// </summary>
    private async Task EnrichReadOrderAsync(Dictionary<string, ShelfEntry> entries, List<object> diag)
    {
        var targets = entries.Values
            .Where(e => e.ProgressItemId.Length > 0)
            .Take(12)
            .ToList();
        if (targets.Count == 0) return;

        using var gate = new SemaphoreSlim(3);
        var tasks = targets.Select(async entry =>
        {
            await gate.WaitAsync();
            try
            {
                var catalog = await GetCatalogAsync(entry.BookId);
                var index = catalog.Chapters.FindIndex(c => c.Id == entry.ProgressItemId);
                if (index < 0) return;
                entry.ReadOrder = index + 1;
                entry.ReadTotal = catalog.Chapters.Count;
            }
            catch (Exception ex)
            {
                LogTrace($"read order {entry.BookId} failed: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        diag.Add(new { endpoint = "read-order", tried = targets.Count, resolved = targets.Count(e => e.ReadOrder > 0) });
    }

    /// <summary>有书缺作者时才继续问别的接口，避免重复请求。</summary>
    private static bool NeedsAuthor(Dictionary<string, ShelfEntry> entries) =>
        entries.Count > 0 && entries.Values.Any(e => e.Author.Length == 0);

    private static object ToNumberOrString(string id) =>
        long.TryParse(id, out var number) ? number : id;

    /// <summary>
    /// Collects every object that looks like a book, whatever the response nesting is
    /// (list, keyed map, or a wrapper object with a different field name).
    /// </summary>
    private static void CollectBooks(
        JsonElement element,
        Dictionary<string, ShelfEntry> sink,
        int depth = 0,
        string? inheritedId = null)
    {
        if (depth > 5) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectBooks(item, sink, depth + 1, inheritedId);
                break;

            case JsonValueKind.Object:
                // A book object may be nested under its shelf entry (book / book_info)
                // and then has no id of its own, so the parent's id is carried down.
                var id = GetString(element, "book_id") ?? GetString(element, "bookId") ?? inheritedId;
                if (id is { Length: > 0 } && LooksLikeBook(element))
                {
                    MergeEntry(sink, ReadEntry(element, id));
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectBooks(property.Value, sink, depth + 1, id);
                    }
                }
                break;
        }
    }

    private static bool LooksLikeBook(JsonElement element) =>
        element.TryGetProperty("book_name", out _)
        || element.TryGetProperty("bookName", out _)
        || element.TryGetProperty("author", out _)
        || element.TryGetProperty("serial_count", out _)
        || element.TryGetProperty("creation_status", out _);

    // ------------------------------------------------------------------
    // reading progress
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, (string ItemId, int Order)>> GetReadProgressAsync(List<object> diag)
    {
        var map = new Dictionary<string, (string ItemId, int Order)>(StringComparer.Ordinal);
        try
        {
            using var doc = await GetJsonAsync(
                BaseUrl + "/api/reader/book/progress?" + ShelfQuery,
                referer: BaseUrl + "/bookshelf");

            var code = GetInt(doc.RootElement, "code");
            if (code != 0)
            {
                LogTrace($"progress code={code} msg={GetString(doc.RootElement, "message")}");
                diag.Add(new { endpoint = "progress", code, data = Sample(doc.RootElement.GetRawText()) });
                return map;
            }
            CollectProgress(doc.RootElement, map);
            LogTrace($"progress ok, books={map.Count}");
            diag.Add(new { endpoint = "progress", code, books = map.Count, data = Sample(doc.RootElement.GetRawText()) });
        }
        catch
        {
            // progress is a bonus: the shelf still works without it
        }
        return map;
    }

    private static void CollectProgress(JsonElement element, Dictionary<string, (string ItemId, int Order)> map, int depth = 0)
    {
        if (depth > 5) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectProgress(item, map, depth + 1);
                break;

            case JsonValueKind.Object:
                var bookId = GetString(element, "book_id") ?? GetString(element, "bookId");
                if (bookId is { Length: > 0 })
                {
                    var itemId = FirstId(
                        GetString(element, "item_id"),
                        GetString(element, "itemId"),
                        GetString(element, "chapter_id"),
                        GetString(element, "chapterId"),
                        GetString(element, "last_item_id"),
                        GetString(element, "read_item_id"));
                    var order = GetInt(element, "index")
                                ?? GetInt(element, "real_chapter_order")
                                ?? GetInt(element, "chapter_order")
                                ?? GetInt(element, "order")
                                ?? 0;
                    if (itemId.Length > 0 || order > 0) map[bookId] = (itemId, order);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectProgress(property.Value, map, depth + 1);
                    }
                }
                break;
        }
    }

    // ------------------------------------------------------------------
    // small helpers
    // ------------------------------------------------------------------

    private static string FirstId(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (candidate is "0" or "null" or "None") continue;
            return candidate;
        }
        return "";
    }
}
