using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using FanqieReader.Models;
using FanqieReader.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FanqieReader;

public enum GlassMode
{
    /// <summary>No desktop behind the window: the reader stays an opaque sheet.</summary>
    None,

    /// <summary>Legacy acrylic accent, tint controlled by us — the transparent path.</summary>
    Accent,

    /// <summary>Windows 11 Mica/Acrylic system backdrop (fallback, more opaque).</summary>
    DwmBackdrop
}

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly EdgeHideController _edgeHide;
    private readonly HotkeyManager _hotkey;
    private readonly DispatcherTimer _progressSaveTimer;

    private CookieService? _cookies;
    private FanqieClient? _fanqie;
    private UserProfile _user = new();
    private ChapterDocument? _currentChapter;
    private QrLoginService? _qrService;
    private CancellationTokenSource? _qrCts;
    private int _qrGeneration;
    private bool _webReady;
    private bool _initSent;
    private bool _hotkeyOk;
    private bool _isClosing;
    private double _pendingScrollRatio = -1;
    private int _opacityPercent = 100;
    private GlassMode _glassMode = GlassMode.None;
    private bool _dragging;
    private bool _dragMoved;
    private NativePoint _dragOrigin;
    private NativeRect _dragStartRect;
    private int _dragMoves;
    private readonly DispatcherTimer _dragWatchdog;

    public MainWindow()
    {
        InitializeComponent();
        _settingsStore = new SettingsStore();
        ApplyProfileOverride();
        _settings = _settingsStore.Load();
        FanqieClient.Trace = DebugLog;
        _edgeHide = new EdgeHideController(this);
        _hotkey = new HotkeyManager(this);
        _hotkey.Pressed += ToggleImmersiveFromHotkey;
        _dragWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dragWatchdog.Tick += (_, _) =>
        {
            // Safety net: if the page stops reporting moves, release the drag as
            // soon as the physical button is up so the window can never latch.
            if (!_dragging || !IsLeftButtonDown()) EndDrag();
        };
        _progressSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _progressSaveTimer.Tick += (_, _) =>
        {
            _progressSaveTimer.Stop();
            if (_pendingScrollRatio >= 0 && _currentChapter is not null)
            {
                _settings.LastScrollRatio = _pendingScrollRatio;
                _settingsStore.Save();
            }
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _edgeHide.Stop();
            }
            else
            {
                _edgeHide.Start();
            }
        };
        ApplyWindowSettings();
    }

    private void ApplyWindowSettings()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
        {
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }

        // Bring a stale off-screen position back into the virtual desktop.
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;
        if (Left + Width < vLeft + 80 || Left > vRight - 80 || Top + 40 < vTop || Top > vBottom - 40)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        else
        {
            // Keep at least a grab-able title strip on screen after a monitor change.
            Left = Math.Clamp(Left, vLeft - Math.Max(0, Width - 160), vRight - 160);
            Top = Math.Clamp(Top, vTop, vBottom - 120);
        }

        ApplyOpacity(_settings.Opacity);
        ApplyTheme(_settings.Theme);
        if (_settings.EdgeHide) Topmost = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        TryEnableNativeGlass();
        ApplyDwmWindowStyle();
        ApplyOpacity(_opacityPercent);
        _hotkeyOk = _hotkey.Register();

        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            DebugLog("window loaded, initializing WebView2");
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            DebugLog("WebView init failed: " + ex);
            BootOverlay.Visibility = Visibility.Visible;
            var busy = IsProfileBusy(ex);
            var hint = busy
                ? "另一个阅读器窗口，或上一次异常退出留下的浏览器进程，仍占用登录配置。\n请关闭其它阅读器窗口，等几秒后重新打开。"
                : "请确认系统已安装 Microsoft Edge WebView2 Runtime。";
            MessageBox.Show(this,
                "无法启动内嵌浏览器（WebView2）。\n" + hint + "\n\n" + ex.Message,
                "沉浸式阅读器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>0x800700AA / 0x8000FFFF: the login profile is still held by another process.</summary>
    private static bool IsProfileBusy(Exception ex)
    {
        if (ex is COMException com)
        {
            var hr = com.HResult & 0xFFFF;
            if (hr == 0x00AA) return true;
        }
        return ex.Message.Contains("0x800700AA", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("0x8000FFFF", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("使用中", StringComparison.Ordinal)
               || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>由代码创建的 WebView2；登录配置不可用时整块换掉重建。</summary>
    private WebView2 View { get; set; } = null!;

    private string? _pendingProfileNotice;

    private WebView2 CreateWebView(string userDataFolder)
    {
        var view = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder },
            DefaultBackgroundColor = System.Drawing.Color.Transparent
        };
        WebHost.Children.Clear();
        WebHost.Children.Add(view);
        return view;
    }

    private async Task InitializeWebViewAsync()
    {
        // 依次尝试：上次用过的配置目录 → 默认 → -2 → -3 → -4。任何一个能用就继续，
        // 这样"配置目录被占用 / 不可写"不会再让整个程序打不开（代价是要重新登录）。
        var candidates = new List<string>();
        void Add(string suffix)
        {
            if (!candidates.Contains(suffix)) candidates.Add(suffix);
        }

        Add(_settings.WebViewProfileSuffix ?? "");
        Add("");
        Add("-2");
        Add("-3");
        Add("-4");

        // 后缀和目录成对保留，避免跳过某个候选时后缀与目录错位
        var attempts = new List<(string Suffix, string Folder)>();
        foreach (var candidate in candidates)
        {
            try { attempts.Add((candidate, _settingsStore.WebViewDataDirectoryFor(candidate))); }
            catch (Exception ex) { DebugLog($"profile dir '{candidate}' unusable: {ex.Message}"); }
        }
        try
        {
            // 最后兜底：临时目录（LOCALAPPDATA 只读时仍能启动）
            attempts.Add(("-temp", Path.Combine(Path.GetTempPath(), "FanqieReader", "WebView2-temp")));
        }
        catch { }

        Exception? lastError = null;
        foreach (var (suffix, folder) in attempts)
        {
            DebugLog($"WebView2 profile try: '{folder}'");

            View = CreateWebView(folder);
            try
            {
                await EnsureCoreAsync();
                DebugLog($"WebView2 core ready (profile suffix '{suffix}')");
                if (!string.Equals(suffix, _settings.WebViewProfileSuffix, StringComparison.Ordinal))
                {
                    _settings.WebViewProfileSuffix = suffix;
                    _settingsStore.Save();
                    if (suffix.Length > 0)
                    {
                        _pendingProfileNotice =
                            "原来的登录配置被其它进程占用或不可写，已切换到新的配置目录，需要重新登录一次。";
                    }
                }
                break;
            }
            catch (Exception ex) when (IsProfileBusy(ex) || ex is UnauthorizedAccessException or IOException)
            {
                lastError = ex;
                DebugLog($"profile '{suffix}' unusable: {ex.Message}");
                try { View?.Dispose(); } catch { }
            }
        }

        if (View is null || View.CoreWebView2 is null)
        {
            throw lastError ?? new FanqieException("无法初始化内嵌浏览器。");
        }

        var core = View.CoreWebView2;
        View.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = Environment.GetCommandLineArgs().Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ProcessFailed += (_, args) =>
        {
            if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Send("toast", new { message = "内嵌浏览器遇到问题，正在恢复…", kind = "error" });
            }
        };
        core.NavigationCompleted += (_, args) =>
        {
            DebugLog($"navigation completed success={args.IsSuccess} error={args.WebErrorStatus}");
            if (args.IsSuccess)
            {
                BootOverlay.Visibility = Visibility.Collapsed;
            }
        };

        _cookies = new CookieService(() => core.CookieManager);
        _fanqie = new FanqieClient(() => _cookies.GetCookieHeaderAsync());

        var webRoot = Path.Combine(AppContext.BaseDirectory, "WebAssets");
        DebugLog("web root " + webRoot + " exists=" + Directory.Exists(webRoot));
        if (!Directory.Exists(webRoot))
        {
            throw new DirectoryNotFoundException("缺少 WebAssets 资源目录：" + webRoot);
        }
        core.SetVirtualHostNameToFolderMapping(
            "fanqie.reader",
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://fanqie.reader/index.html");

        _edgeHide.Enabled = _settings.EdgeHide;
        _edgeHide.Start();
        ApplyOpacity(_opacityPercent);

        if (_pendingProfileNotice is not null)
        {
            Send("toast", new { message = _pendingProfileNotice, kind = "error" });
            _pendingProfileNotice = null;
        }
    }

    private async Task EnsureCoreAsync()
    {
        // 上一次异常退出可能还占着配置目录几秒，重试一次比直接报错友好。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var initTask = View.EnsureCoreWebView2Async();
                if (await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(20))) != initTask)
                {
                    throw new TimeoutException("WebView2 初始化超时。请确认 WebView2 Runtime 正常。");
                }
                await initTask;
                return;
            }
            catch (Exception ex) when (attempt < 2 && IsProfileBusy(ex))
            {
                DebugLog($"profile busy (attempt {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(900 * attempt));
            }
        }
    }
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://fanqie.reader/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmExitSizeMove)
        {
            // The OS finished a move/resize (our own drag included): let the edge
            // controller snap to the nearest edge.
            _edgeHide.NotifyDragEnded();
        }
        return IntPtr.Zero;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = GetString(root, "type") ?? "";
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    SendInit();
                    _ = VerifySessionAsync();
                    if (!_hotkeyOk)
                    {
                        Send("toast", new { message = "全局快捷键 Alt+Shift+Q 被其他程序占用，可用菜单切换沉浸模式", kind = "error" });
                    }
                    break;
                case "dragStart":
                    DebugLog("drag start");
                    BeginDrag();
                    break;
                case "dragMove":
                    ContinueDrag();
                    break;
                case "dragEnd":
                    DebugLog("drag end (from page)");
                    EndDrag();
                    break;
                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "close":
                    Close();
                    break;
                case "toggleMaximize":
                    DebugLog("toggleMaximize, state=" + WindowState);
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
                case "resize":
                    try
                    {
                        var hit = (int)Math.Round(GetDouble(root, "value") ?? 0);
                        if (hit is >= 10 and <= 17)
                        {
                            _edgeHide.Suppressed = true;
                            var hwnd = new WindowInteropHelper(this).Handle;
                            ReleaseCapture();
                            SendMessage(hwnd, WmNcLeftButtonDown, (IntPtr)hit, IntPtr.Zero);
                            _edgeHide.Suppressed = false;
                        }
                    }
                    catch { }
                    break;
                case "opacityReady":
                    ApplyOpacity(_opacityPercent);
                    break;
                case "setTheme":
                    SetTheme(GetString(root, "value") ?? "day");
                    break;
                case "setFontSize":
                    SetFontSize((int)Math.Round(GetDouble(root, "value") ?? _settings.FontSize));
                    break;
                case "setOpacity":
                    SetOpacity((int)Math.Round(GetDouble(root, "value") ?? _settings.Opacity));
                    break;
                case "setEdgeHide":
                    SetEdgeHide(GetBool(root, "enabled") ?? false);
                    break;
                case "setImmersive":
                    SetImmersive(GetBool(root, "enabled") ?? false, true);
                    break;
                case "getShelf":
                    _ = GetShelfAsync();
                    break;
                case "openBook":
                    _ = OpenBookAsync(
                        GetString(root, "bookId") ?? "",
                        GetString(root, "chapterId") ?? "",
                        (int)Math.Round(GetDouble(root, "order") ?? 0));
                    break;
                case "openChapter":
                    _ = LoadChapterAsync(GetString(root, "chapterId") ?? "", true);
                    break;
                case "openInput":
                    _ = OpenInputAsync(GetString(root, "text") ?? "");
                    break;
                case "getCatalog":
                    _ = LoadCatalogAsync(GetString(root, "bookId") ?? "", GetBool(root, "background") ?? false);
                    break;
                case "cookieLogin":
                    _ = CookieLoginAsync(GetString(root, "text") ?? "");
                    break;
                case "openQrLogin":
                    _ = StartQrLoginAsync();
                    break;
                case "closeQr":
                    CancelQrLogin();
                    break;
                case "logout":
                    _ = LogoutAsync();
                    break;
                case "readClipboard":
                    ReadClipboard();
                    break;
                case "progress":
                    var ratio = GetDouble(root, "ratio") ?? 0;
                    _pendingScrollRatio = Math.Clamp(ratio, 0, 1);
                    _progressSaveTimer.Stop();
                    _progressSaveTimer.Start();
                    break;
            }
        }
        catch (Exception ex)
        {
            Send("toast", new { message = "内部消息处理失败：" + ex.Message, kind = "error" });
        }
    }

    // ------------------------------------------------------------------
    // window dragging (done by hand so the OS snap preview never appears)
    //
    // Deliberately no WPF mouse capture here: the drag is driven by the page,
    // and taking capture away from the WebView2 child window stopped the page
    // from ever seeing pointerup, which left every click captured and made the
    // window look frozen. The page owns pointer capture; this side only follows
    // the physical cursor and stops when the button is really up.
    // ------------------------------------------------------------------

    private void BeginDrag()
    {
        if (_dragging) EndDrag();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetCursorPos(out _dragOrigin)) return;
        if (!GetWindowRect(handle, out _dragStartRect)) return;

        _dragging = true;
        _dragMoved = false;
        _dragWatchdog.Start();

        if (WindowState == WindowState.Maximized) return;
        _edgeHide.NotifyDragStarted();
    }

    private void ContinueDrag()
    {
        if (!_dragging) return;

        // The button is the source of truth: if the page stops sending moves
        // (pointer lost the window) the drag still has to end.
        if (!IsLeftButtonDown())
        {
            EndDrag();
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetCursorPos(out var cursor)) return;

        // Un-maximize when the user drags a maximized window down.
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (!GetWindowRect(handle, out _dragStartRect)) return;
            _dragOrigin = cursor;
            return;
        }

        var x = _dragStartRect.Left + (cursor.X - _dragOrigin.X);
        var y = _dragStartRect.Top + (cursor.Y - _dragOrigin.Y);
        _dragMoved = true;
        _dragMoves++;

        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            SwpNosize | SwpNozorder | SwpNoactivate | SwpNosendchanging);
    }

    private void EndDrag()
    {
        _dragWatchdog.Stop();
        if (_dragging || _dragMoved) DebugLog($"drag end: moves={_dragMoves}");
        _dragMoves = 0;
        if (!_dragging && !_dragMoved) return;
        _dragging = false;
        _dragMoved = false;
        _edgeHide.NotifyDragEnded();
    }

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    private void SendInit()
    {
        if (!_webReady || _initSent) return;
        _initSent = true;
        object? recent = null;
        if (!string.IsNullOrWhiteSpace(_settings.LastChapterId))
        {
            recent = new
            {
                bookId = _settings.LastBookId,
                chapterId = _settings.LastChapterId,
                bookName = _settings.LastBookName,
                chapterTitle = _settings.LastChapterTitle
            };
        }
        Send("init", new
        {
            settings = _settings,
            user = _user,
            recent,
            glass = _glassMode != GlassMode.None
        });
    }

    private async Task VerifySessionAsync()
    {
        if (_fanqie is null || _cookies is null) return;
        try
        {
            var cookie = await _cookies.GetCookieHeaderAsync();
            if (string.IsNullOrWhiteSpace(cookie))
            {
                UpdateUser(new UserProfile());
                return;
            }
            var profile = await _fanqie.GetUserAsync();
            UpdateUser(profile);
        }
        catch
        {
            // Reading is still available when the session check cannot complete.
        }
    }

    private void UpdateUser(UserProfile profile)
    {
        _user = profile;
        Send("user", new { user = profile });
    }

    private async Task CookieLoginAsync(string text)
    {
        if (_cookies is null || _fanqie is null)
        {
            Send("loginResult", new { success = false, message = "阅读器还在启动，请稍后再试" });
            return;
        }

        try
        {
            text = text.Trim();
            if (text.Length < 6)
            {
                Send("loginResult", new { success = false, message = "Cookie 太短，请复制完整内容" });
                return;
            }

            await _cookies.ClearFanqieCookiesAsync();
            var count = await _cookies.SetCookieStringAsync(text);
            if (count == 0)
            {
                Send("loginResult", new { success = false, message = "没有识别到有效的 Cookie" });
                return;
            }

            var profile = await _fanqie.GetUserAsync();
            if (!profile.LoggedIn)
            {
                Send("loginResult", new { success = false, message = "Cookie 已写入，但没有取到账号。可能已过期，请重新登录网页后复制。" });
                return;
            }

            UpdateUser(profile);
            Send("loginResult", new { success = true, user = profile });
        }
        catch (Exception ex)
        {
            Send("loginResult", new { success = false, message = "登录失败：" + ex.Message });
        }
    }

    private async Task StartQrLoginAsync()
    {
        CancelQrLogin();
        var generation = ++_qrGeneration;
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        _qrCts = cts;
        var service = new QrLoginService();
        _qrService = service;

        try
        {
            Send("qrLoading", new { });
            var qr = await service.StartAsync(cts.Token);
            if (generation != _qrGeneration) return;
            Send("qr", new
            {
                dataUrl = qr.QrDataUrl,
                expiresAt = qr.ExpiresAt.ToUnixTimeSeconds()
            });

            var networkErrors = 0;
            var verifyAttempts = 0;
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1500, cts.Token);
                if (generation != _qrGeneration) return;

                QrPollResult poll;
                try
                {
                    poll = await service.PollAsync(qr.Token, cts.Token);
                    networkErrors = 0;
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    networkErrors++;
                    if (networkErrors >= 5)
                    {
                        Send("loginResult", new { success = false, message = "二维码状态查询失败，请刷新后重试" });
                        return;
                    }
                    continue;
                }

                var status = poll.Status.Trim().ToLowerInvariant();
                if (status is "scan" or "scanned" or "scanning")
                {
                    Send("qrStatus", new { status = "scanned", message = "已扫码，请在手机上确认登录" });
                    continue;
                }
                if (status is "expired" or "expire")
                {
                    Send("loginResult", new { success = false, message = "二维码已过期，请点击刷新" });
                    return;
                }
                if (status is "confirmed" or "success" or "login")
                {
                    Send("qrStatus", new { status = "confirming", message = "已确认，正在同步登录状态…" });
                }

                if (poll.HasSessionCookie)
                {
                    if (++verifyAttempts > 4)
                    {
                        Send("loginResult", new { success = false, message = "登录状态同步失败，请重试" });
                        return;
                    }

                    await _cookies!.ClearFanqieCookiesAsync();
                    await _cookies.SetCookieStringAsync(poll.CookieHeader);
                    var profile = await _fanqie!.GetUserAsync();
                    if (profile.LoggedIn)
                    {
                        UpdateUser(profile);
                        Send("loginResult", new { success = true, user = profile });
                        return;
                    }
                    Send("qrStatus", new { status = "confirming", message = "已确认，正在同步登录状态…" });
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _qrGeneration)
            {
                Send("loginResult", new { success = false, message = "二维码已过期，请点击刷新" });
            }
        }
        catch (Exception ex)
        {
            if (generation == _qrGeneration)
            {
                Send("loginResult", new { success = false, message = "获取二维码失败：" + ex.Message });
            }
        }
        finally
        {
            if (generation == _qrGeneration)
            {
                cts.Dispose();
                service.Dispose();
                if (ReferenceEquals(_qrService, service)) _qrService = null;
                if (ReferenceEquals(_qrCts, cts)) _qrCts = null;
            }
        }
    }

    private void CancelQrLogin()
    {
        _qrGeneration++;
        try { _qrCts?.Cancel(); } catch { }
        _qrCts?.Dispose();
        _qrCts = null;
        _qrService?.Dispose();
        _qrService = null;
    }

    private async Task LogoutAsync()
    {
        try
        {
            if (_cookies is not null) await _cookies.ClearFanqieCookiesAsync();
        }
        catch { }
        UpdateUser(new UserProfile());
        Send("toast", new { message = "已退出登录", kind = "info" });
    }

    private async Task GetShelfAsync()
    {
        if (_fanqie is null)
        {
            Send("shelf", new { books = Array.Empty<BookSummary>(), error = "阅读器还在启动，请稍后再试" });
            return;
        }

        try
        {
            var books = await _fanqie.GetBookshelfAsync();
            Send("shelf", new { books, error = (string?)null });
        }
        catch (Exception ex)
        {
            var message = ex is FanqieException ? ex.Message : "网络请求失败：" + ex.Message;
            Send("toast", new { message = "书架加载失败：" + message, kind = "error", timeout = 5200 });
            // Keep the shelf view in charge of the message so it does not just claim
            // "书架还是空的" when the request actually failed.
            Send("shelf", new { books = Array.Empty<BookSummary>(), error = message });
        }
    }

    private async Task OpenBookAsync(string bookId, string chapterId, int order)
    {
        if (_fanqie is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(chapterId))
            {
                var catalog = await _fanqie.GetCatalogAsync(bookId);
                Send("catalog", new { catalog });

                // "读到第 100 章" from the phone app: order is 1 based.
                CatalogChapter? pick = null;
                if (order > 0 && order <= catalog.Chapters.Count)
                {
                    pick = catalog.Chapters[order - 1];
                }
                pick ??= catalog.Chapters.FirstOrDefault();
                if (pick is null) throw new FanqieException("这本书暂时没有可阅读的章节");
                chapterId = pick.Id;
                DebugLog($"open {bookId}: chapterId={chapterId} (order={order})");
            }
            await LoadChapterAsync(chapterId, true);
        }
        catch (Exception ex)
        {
            Send("error", new { message = "打开书籍失败：" + ex.Message });
        }
    }

    private async Task LoadChapterAsync(string chapterId, bool sendError)
    {
        if (_fanqie is null) return;
        try
        {
            var id = FanqieClient.ExtractDigits(chapterId);
            if (string.IsNullOrEmpty(id)) throw new FanqieException("章节编号无效");
            var chapter = await _fanqie.GetChapterAsync(id);
            if (string.IsNullOrEmpty(chapter.BookId) && _currentChapter is not null)
            {
                chapter.BookId = _currentChapter.BookId;
            }
            var restore = _settings.LastChapterId == chapter.ChapterId ? _settings.LastScrollRatio : 0;
            _currentChapter = chapter;
            _settings.LastBookId = chapter.BookId;
            _settings.LastChapterId = chapter.ChapterId;
            _settings.LastBookName = chapter.BookName;
            _settings.LastChapterTitle = chapter.Title;
            _settings.LastScrollRatio = 0;
            _settingsStore.Save();
            Send("chapter", new { chapter, restoreRatio = restore });
        }
        catch (Exception ex)
        {
            if (sendError)
            {
                Send("error", new { message = "正文加载失败：" + ex.Message });
            }
            else
            {
                throw;
            }
        }
    }

    private async Task LoadCatalogAsync(string bookId, bool background)
    {
        if (_fanqie is null) return;
        try
        {
            bookId = FanqieClient.ExtractDigits(bookId);
            if (string.IsNullOrEmpty(bookId)) throw new FanqieException("书籍编号无效");
            var catalog = await _fanqie.GetCatalogAsync(bookId);
            Send("catalog", new { catalog });
        }
        catch (Exception ex)
        {
            if (!background)
            {
                Send("toast", new { message = "目录加载失败：" + ex.Message, kind = "error" });
            }
        }
    }

    private async Task OpenInputAsync(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;

        var reader = Regex.Match(text, @"/reader/(\d+)");
        if (reader.Success)
        {
            await LoadChapterAsync(reader.Groups[1].Value, true);
            return;
        }
        var page = Regex.Match(text, @"/(?:page|book)/(\d+)");
        if (page.Success)
        {
            await OpenBookAsync(page.Groups[1].Value, "", 0);
            return;
        }

        var number = Regex.Match(text, @"\d{6,}");
        if (!number.Success)
        {
            Send("error", new { message = "没有识别到有效的编号，请粘贴番茄小说的章节链接或数字编号" });
            return;
        }

        var id = number.Value;
        try
        {
            await LoadChapterAsync(id, false);
        }
        catch
        {
            try
            {
                await OpenBookAsync(id, "", 0);
            }
            catch (Exception ex)
            {
                Send("error", new { message = "没有找到这本书或章节：" + ex.Message });
            }
        }
    }

    private void ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                Send("clipboard", new { text = Clipboard.GetText() });
            }
            else
            {
                Send("toast", new { message = "剪贴板里没有文字", kind = "info" });
            }
        }
        catch
        {
            Send("toast", new { message = "暂时无法读取剪贴板，请手动粘贴", kind = "error" });
        }
    }

    private void SetTheme(string theme)
    {
        theme = theme == "night" ? "night" : "day";
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        ApplyTheme(theme);
        _settingsStore.Save();
    }

    /// <summary>
    /// <c>--profile-dir &lt;path&gt;</c>：便携模式，把 WebView2 登录配置放到指定目录，
    /// 方便整个文件夹随身带走，也方便排查"配置目录不可用"的问题。
    /// </summary>
    private static void ApplyProfileOverride()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--profile-dir", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var path = Path.GetFullPath(args[i + 1]);
                Directory.CreateDirectory(path);
                SettingsStore.ProfileRootOverride = path;
                DebugLog("profile root override: " + path);
            }
            catch (Exception ex)
            {
                DebugLog("profile override failed: " + ex.Message);
            }
            return;
        }
    }

    private void ApplyTheme(string theme)
    {
        var night = theme == "night";
        var background = night
            ? System.Drawing.Color.FromArgb(0x12, 0x12, 0x14)
            : System.Drawing.Color.FromArgb(0xF4, 0xF0, 0xE8);

        // The page paints the paper; the WPF surface stays transparent so the DWM
        // backdrop (or the desktop, when glass is unavailable) can show through.
        Background = Brushes.Transparent;
        BootOverlay.Background = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B));
        if (View is not null) View.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        ApplyDwmWindowStyle();
    }

    private void SetFontSize(int size)
    {
        _settings.FontSize = Math.Clamp(size, 12, 32);
        _settingsStore.Save();
    }

    private void SetOpacity(int opacity)
    {
        _settings.Opacity = Math.Clamp(opacity, 30, 100);
        ApplyOpacity(_settings.Opacity);
        _settingsStore.Save();
    }

    private void ApplyOpacity(int percent)
    {
        _opacityPercent = Math.Clamp(percent, 30, 100);
        if (_glassMode == GlassMode.None)
        {
            // Without the DWM backdrop there is no desktop to blend with, so the
            // reader stays a solid sheet and says so instead of faking it.
            Send("opacity", new { value = 100, glass = false });
            return;
        }
        Send("opacity", new { value = _opacityPercent, glass = true });
    }

    private void SetEdgeHide(bool enabled)
    {
        _settings.EdgeHide = enabled;
        Topmost = enabled;
        _edgeHide.Enabled = enabled;
        if (enabled) _edgeHide.Start();
        _settingsStore.Save();
    }

    private void SetImmersive(bool enabled, bool fromUi)
    {
        _settings.Immersive = enabled;
        _settingsStore.Save();
        if (enabled)
        {
            _edgeHide.RestoreNow();
            Activate();
        }
        Send("immersive", new { enabled });
        if (!fromUi)
        {
            Send("toast", new { message = enabled ? "已进入沉浸式阅读 · Alt+Shift+Q 退出" : "已退出沉浸式阅读", kind = "info" });
        }
    }

    private void ToggleImmersiveFromHotkey()
    {
        if (!_webReady) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _edgeHide.RestoreNow();
        Show();
        Activate();
        SetImmersive(!_settings.Immersive, false);
    }

    private void ApplyDwmWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var dark = _settings.Theme == "night" ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            switch (_glassMode)
            {
                case GlassMode.Accent:
                    // The legacy acrylic accent keeps its own tint, so re-apply it when
                    // the theme changes and leave the DWM system backdrop alone.
                    ApplyAccentAcrylic(handle);
                    break;

                case GlassMode.DwmBackdrop:
                    var backdrop = DwmsbtTransientWindow;
                    if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
                    {
                        var mica = DwmsbtMainWindow;
                        if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int)) != 0)
                        {
                            DebugLog("DWM backdrop rejected; falling back to a solid window");
                            DisableGlass();
                        }
                    }
                    break;

                default:
                    var none = DwmsbtNone;
                    _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref none, sizeof(int));
                    break;
            }

            var corner = 2; // DWMWCP_ROUND — DWM ignores it for maximized/fullscreen windows
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch (Exception ex)
        {
            DebugLog("dwm style failed: " + ex.Message);
        }
    }

    private void DisableGlass()
    {
        _glassMode = GlassMode.None;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF0, 0xE8));
        Send("opacity", new { value = 100, glass = false });
    }

    /// <summary>
    /// Real per-pixel transparency under a WebView2 needs a backdrop that WPF itself
    /// can compose against. Preferred order:
    ///   1. the legacy acrylic accent (<c>SetWindowCompositionAttribute</c>) — it blurs
    ///      the desktop with a tint we control, so 30% really looks 30%;
    ///   2. the Windows 11 system backdrop (Mica/Acrylic), whose fixed tint is much
    ///      more opaque, kept as a fallback.
    /// A screenshot of the desktop is never used.
    /// </summary>
    private bool TryEnableNativeGlass()
    {
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--no-glass", StringComparison.OrdinalIgnoreCase)))
        {
            DebugLog("native glass disabled by --no-glass");
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;

        Background = Brushes.Transparent;
        if (View is not null) View.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        if (ApplyAccentAcrylic(handle))
        {
            _glassMode = GlassMode.Accent;
            DebugLog("native glass: accent acrylic");
            return true;
        }

        if (Environment.OSVersion.Version.Build >= 22621)
        {
            try
            {
                var backdrop = DwmsbtTransientWindow;
                if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                {
                    var chrome = WindowChrome.GetWindowChrome(this);
                    if (chrome is not null)
                    {
                        chrome.GlassFrameThickness = new Thickness(-1);
                        chrome.CornerRadius = new CornerRadius(0);
                    }

                    var margins = new Margins { Left = -1, Top = -1, Right = -1, Bottom = -1 };
                    _ = DwmExtendFrameIntoClientArea(handle, ref margins);
                    _glassMode = GlassMode.DwmBackdrop;
                    DebugLog("native glass: dwm backdrop");
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLog("dwm backdrop failed: " + ex.Message);
            }
        }

        DebugLog("native glass unavailable");
        DisableGlass();
        return false;
    }

    /// <summary>
    /// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> with a nearly transparent tint.
    /// GradientColor is 0xAABBGGRR, so alpha is the knob for "how much paper".
    /// </summary>
    private bool ApplyAccentAcrylic(IntPtr handle)
    {
        try
        {
            var night = _settings.Theme == "night";
            // day:  #F4F0E8 at ~0.4% tint   night: #121214 at ~25% tint
            var gradient = night ? unchecked((int)0x40141212) : unchecked((int)0x01E8F0F4);

            var tintOverride = Environment.GetEnvironmentVariable("FANQIE_ACCENT_TINT");
            if (!string.IsNullOrWhiteSpace(tintOverride)
                && uint.TryParse(tintOverride, System.Globalization.NumberStyles.HexNumber, null, out var tintValue))
            {
                gradient = unchecked((int)tintValue);
            }

            // 2 = transparent gradient tint, 3 = gaussian blur behind (colour preserved),
            // 4 = acrylic (blurred + tinted + desaturated). Tunable for testing.
            var state = int.TryParse(Environment.GetEnvironmentVariable("FANQIE_ACCENT_STATE"), out var parsed)
                ? parsed
                : AccentEnableAcrylicBlurBehind;

            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(handle, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception ex)
        {
            DebugLog("accent acrylic failed: " + ex.Message);
            return false;
        }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        _progressSaveTimer.Stop();
        CancelQrLogin();
        EndDrag();
        _dragWatchdog.Stop();
        _edgeHide.RestoreNow();
        _edgeHide.Dispose();
        _hotkey.Dispose();
        _fanqie?.Dispose();

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settingsStore.Save();
    }

    private void Send(string type, object data)
    {
        if (!_webReady || View.CoreWebView2 is null) return;
        try
        {
            var json = JsonSerializer.Serialize(new { type, data }, JsonOptions);
            View.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // The window may be closing while an async request completes.
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out n)) return n;
        return null;
    }

    /// <summary>Diagnostics go to %LOCALAPPDATA%\FanqieReader\logs\reader.log.</summary>
    private static void DebugLog(string message)
    {
        try
        {
            // 便携模式（--profile-dir）下日志跟着配置目录走，整个文件夹可以随身带走
            var root = SettingsStore.ProfileRootOverride
                       ?? Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "FanqieReader");
            var dir = Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "reader.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the reader down
        }
    }

    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmExitSizeMove = 0x0232;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNosendchanging = 0x0400;
    private const int VkLeftButton = 0x01;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
