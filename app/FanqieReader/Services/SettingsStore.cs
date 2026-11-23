using System.Text.Json;
using System.Text.Json.Serialization;
using FanqieReader.Models;

namespace FanqieReader.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _dir;
    private readonly string _file;

    public AppSettings Current { get; private set; } = new();

    public SettingsStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FanqieReader");
        _file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
    }

    public string DataDirectory => _dir;

    public string WebViewDataDirectory => WebViewDataDirectoryFor(Current.WebViewProfileSuffix);

    /// <summary>
    /// 登录态放在 WebView2 自己的配置目录里。后缀为空时是默认目录；被别的实例占用
    /// 或不可写时会退到 -2 / -3 / -4，保证程序总能启动（代价是要重新登录一次）。
    /// </summary>
    /// <summary>便携模式：把登录配置放到指定目录（--profile-dir 或 settings.json 旁边）。</summary>
    public static string? ProfileRootOverride { get; set; }

    public string WebViewDataDirectoryFor(string suffix)
    {
        var root = ProfileRootOverride
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "FanqieReader");
        var local = Path.Combine(root, "WebView2" + suffix);
        Directory.CreateDirectory(local);
        return local;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        Current.Clamp();
        return Current;
    }

    public void Save(AppSettings? settings = null)
    {
        if (settings is not null) Current = settings;
        Current.Clamp();
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            // Persisting settings must never take the reader down, but leave a trail.
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "fanqie-reader.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} settings save failed: {ex.Message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public void MarkRead(string bookId, string chapterId, string bookName, string chapterTitle, double ratio = 0)
    {
        Current.LastBookId = bookId;
        Current.LastChapterId = chapterId;
        Current.LastBookName = bookName;
        Current.LastChapterTitle = chapterTitle;
        Current.LastScrollRatio = Math.Clamp(ratio, 0, 1);
        Save();
    }
}
