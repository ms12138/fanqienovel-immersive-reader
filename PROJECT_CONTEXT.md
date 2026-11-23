# 番茄沉浸阅读器 · 项目核心上下文（Bug 修复版）

> 用途：给外部模型提供最小但足够准确的工程背景。项目已能运行，当前进入修 Bug 阶段。

## 1. 项目定位

Windows 桌面端番茄小说阅读器，不是网页套壳：

- 从 `https://fanqienovel.com` 抓取书名、章节、目录、正文。
- 用本地 HTML/CSS/JS 重新排版成极简阅读器。
- 支持 Cookie/二维码登录、书架、继续阅读、字号、透明度、主题、贴边隐藏、沉浸式阅读。

## 2. 技术栈

| 层 | 技术 |
|---|---|
| 桌面宿主 | .NET 9 WPF，`net9.0-windows10.0.19041.0` |
| 内嵌浏览器 | Microsoft.Web.WebView2 `1.0.3351.48` |
| HTML 解析 | AngleSharp `1.2.0` |
| 截图/透明度 | `System.Drawing.Common 9.0.0` + Win32 |
| UI | 原生 HTML + CSS + vanilla JS，无前端框架、无 MVVM |
| 打包 | 自包含 win-x64，约 156 MB（zip 约 66 MB） |

## 3. 核心文件结构

```text
app/FanqieReader/
├─ App.xaml / App.xaml.cs          启动、单实例 Mutex、全局异常
├─ MainWindow.xaml / .cs           窗口、WebView2、桥接、登录、书架、阅读、透明度、热键（约 1000 行）
├─ Models/ReaderModels.cs          AppSettings / UserProfile / BookSummary /
│                                  CatalogChapter / ChapterDocument / CatalogResult
├─ Services/
│  ├─ FanqieClient.cs              HTTP、Cookie Header、章节/目录/书架、字体下载与缓存
│  ├─ FanqieParser.cs              AngleSharp 解析正文元数据、段落、目录
│  ├─ CookieService.cs             WebView2 CookieManager 的导入/导出/清理
│  ├─ QrLoginService.cs            番茄 passport 二维码获取与轮询
│  ├─ EdgeHideController.cs        4px 贴边隐藏与 SetWindowPos 动画
│  ├─ HotkeyManager.cs             RegisterHotKey：Alt+Shift+Q
│  └─ SettingsStore.cs             %APPDATA%\FanqieReader\settings.json
└─ WebAssets/
   ├─ index.html                   欢迎页/书架/阅读区/菜单/登录/目录
   ├─ styles.css                   日间/夜间、玻璃透明度、响应式排版
   └─ app.js                       状态、消息桥接、渲染、设置、快捷键
```

## 4. 主要类职责

### MainWindow
- 创建无边框窗口，初始化 WebView2 环境、虚拟域名 `https://fanqie.reader/` 映射到 `WebAssets`。
- 处理所有 JS → C# 消息；把 C# 结果通过 `PostWebMessageAsJson` 推回。
- 包装 `FanqieClient` / `CookieService` / `QrLoginService`，处理登录、书架、章节、目录、进度保存。
- `TryEnableNativeGlass`：DWM 系统亚克力 + 透明 WebView2（真透明）；`--no-glass` 可关。
- 自绘拖动（`dragStart/dragMove/dragEnd`）：用 `GetCursorPos + SetWindowPos` 跟随鼠标，避免系统贴边预览框。
- 管理贴边、沉浸、全局热键、窗口位置持久化。
- `DebugLog` 写 `%LOCALAPPDATA%\FanqieReader\logs\reader.log`。
- 限制外部导航、弹窗、下载、权限请求。

### FanqieClient
- 无自动 Cookie，手动从 `CookieService.GetCookieHeaderAsync()` 取 Cookie 并写请求头。
- `GetUserAsync()` → `/api/reader/user/info`
- `GetChapterAsync(id)` → `/reader/{id}` HTML，交给 Parser，再下载章节字体。
- `GetCatalogAsync(bookId)` → `/page/{bookId}` HTML，交给 Parser。
- `GetBookshelfAsync()` → `/reading/bookapi/bookshelf/info/v1/?aid=1967&from=web_novel_pc&app_name=muye_novel`（`aid` 缺失会返回 `100103 PARAM_INVALID`），details 用 `/api/bookshelf/multidetail`，失败再走 `/api/book/simple/info`；含 v0/v2 回退。
- 字体缓存：`Dictionary<string,string>` + FIFO 24 条；字体转 `data:font/woff2;base64,...`，多 CDN/多格式按顺序尝试。

### FanqieParser
- `ParseChapter`：从 `.muye-reader-title`、`.muye-reader-content p`、`nextItemId`/`preItemId`/`bookId`/`chapterWordNumber`/`needPay` 等提取。
- `ParseCatalog`：抓 `a[href*="/reader/"]`，去重、去掉“最近更新”前缀，按“第 N 章”数字排序。
- 段落只取 `p.TextContent`；没有 `p` 时按整块文本换行拆分。
- `ExtractChapterFont`：解析 `<style>` 中的 `@font-face`，得到真实家族名、字重 400 的 URL 列表，并从容器 `font-family` 推断黑体/宋体风格（`FontFamily` / `FontKind` / `FontUrls`）。
- `ReadJsonString` 跳过空值（修过 author 空串问题）。

### CookieService
- 基于 `CoreWebView2CookieManager`。
- 支持请求头、`document.cookie`、JSON 数组；统一按 `.fanqienovel.com` 写入。
- 退出登录清空 `fanqienovel.com` / `www.fanqienovel.com` / `reading.snssdk.com` 相关 Cookie。

### QrLoginService
- `GET /passport/web/get_qrcode/` 获取 `qrcode`（PNG base64）和 `token`。
- `GET /passport/web/check_qrconnect/?token=...` 轮询状态；检测到 `sessionid`/`sid_guard` 后同步 Cookie。
- 不依赖作者的登录网页，直接显示二维码。

### EdgeHideController
- 45ms `DispatcherTimer` 轮询光标与窗口矩形；距工作区左/右/上边缘 26px 内即视为贴边，松手后 `SetWindowPos` 吸齐。
- 鼠标离开 420ms 防抖 + 二次确认后滑出，保留 5px 感应条；光标进入感应条（+3px 容差）滑回。
- 动画由独立 15ms 定时器驱动，`easeOutCubic`、190ms，结束强制落到目标矩形。
- 用 `MonitorFromWindow`/`MonitorFromPoint` 取工作区，支持多屏与混合 DPI。
- `Suppressed` 用于临时抑制；外部移动窗口会让 `_hidden` 自动复位。

## 5. 前后端通讯

### 通道
- WebView2 虚拟主机：`https://fanqie.reader/` → `WebAssets`。
- JS → C#：`window.chrome.webview.postMessage({ type, ...payload })`
- C# 接收：`CoreWebView2.WebMessageReceived`，读 `e.WebMessageAsJson`，`JsonDocument` 解析。
- C# → JS：`PostWebMessageAsJson(JsonSerializer.Serialize(new { type, data }))`
- JS 接收：`window.chrome.webview.addEventListener('message', handleMessage)`，读 `msg.type` / `msg.data`。

### 启动握手
1. JS `ready`
2. C# `SendInit()` → `init`，带 `settings`、`user`、`recent`
3. JS `applyInit()` 恢复界面

### 主要消息

| 方向 | type | 说明 |
|---|---|---|
| JS→C# | `dragStart` / `dragMove` / `dragEnd` | 自绘拖动（不走系统 `HTCAPTION`） |
| JS→C# | `minimize` / `close` / `toggleMaximize` / `resize` | 无边框窗口控制 |
| JS→C# | `setTheme` / `setFontSize` / `setOpacity` / `setEdgeHide` / `setImmersive` | 设置 |
| JS→C# | `getShelf` / `openBook` / `openChapter` / `getCatalog` / `openInput` | 内容 |
| JS→C# | `cookieLogin` / `openQrLogin` / `closeQr` / `logout` / `readClipboard` | 登录/剪贴板 |
| JS→C# | `progress` | 阅读进度（0~1） |
| C#→JS | `init` / `settings` / `user` / `shelf` / `catalog` / `chapter` | 数据 |
| C#→JS | `loginResult` / `qr` / `qrStatus` / `qrLoading` | 登录 |
| C#→JS | `opacity` | `{ value, glass }`：透明度百分比 + 系统是否支持真透明 |
| C#→JS | `toast` / `error` / `immersive` / `clipboard` | 提示/状态 |

### 重要约束
- `OnNavigationStarting` 只允许 `https://fanqie.reader/`；外部链接全部取消。
- `NewWindowRequested`、`DownloadStarting`、`PermissionRequested` 全部拒绝。
- 正文 HTML 不直接注入：Parser 只给纯文本段落，JS 用 `textContent` 渲染，避免 XSS。
- `WebMessageReceived` 整体 try/catch，异常用 `toast` 返回。

## 6. 已修复的关键问题（11-23）

### A. 透明度：已改为“真透明 + 系统亚克力”

旧实现是 `WDA_EXCLUDEFROMCAPTURE` + `CopyFromScreen` + CSS 背景图（快照会过期、会抓到上层窗口、旧系统会闪），已全部删除。

现在的实现（`GlassMode.Accent` 为主，`DwmBackdrop` 兜底）：

1. `SetWindowCompositionAttribute(WCA_ACCENT_POLICY, ACCENT_ENABLE_ACRYLICBLURBEHIND)`，`GradientColor` 用近乎 0 的 alpha
   （day `0x01E8F0F4` / night `0x40141212`），桌面被系统模糊但**不被染成奶白**；
2. WebView2 控制器 `DefaultBackgroundColor = Transparent`（另有 `WEBVIEW2_DEFAULT_BACKGROUND_COLOR=00000000` 消除首帧白闪）；
3. 页面分层**只叠一次**：
   - `.app` 玻璃模式透明（`is-opaque` 时恢复 `var(--bg)`）
   - `.main` = `rgb(bg / chrome*0.45)`
   - `.titlebar` / `.reader-nav` = `rgb(bg / --chrome-alpha)`
   - `.reader-paper` = `rgb(paper / --paper-alpha)`
   - 菜单/弹窗/目录 = `rgb(paper / --surface-alpha)`
   - 下限：chrome 0.06 / paper 0.34 / surface 0.58
4. 玻璃模式下 `.titlebar` 等去掉 `backdrop-filter`：宿主已是模糊的，页面内再滤会在标题栏顶留下异色条；
5. 兜底与边界：`SetWindowCompositionAttribute` 失败时回退到 `DWMWA_SYSTEMBACKDROP_TYPE + GlassFrameThickness=-1`；
   都失败则 `GlassMode.None`（不透明、滑块禁用、`opacity` 消息带 `glass:false`）；`--no-glass` 可强制关闭。
   测试用 `FANQIE_ACCENT_STATE` / `FANQIE_ACCENT_TINT` 两个环境变量可切换 accent 状态与染色。

关键教训：**系统亚克力（DWMSBT_*）自带强染色与去饱和，用它做“低透明度”一定像奶白玻璃**；
另外 `.app` 与 `.reader-paper` 各自带 alpha 会叠加，必须让外壳透明。

注意：`backdrop-filter: blur()` 在这里**无效**——页面背景透明后浏览器没有“桌面”可取样，`backdrop-filter` 只会模糊页面内元素，桌面模糊必须靠 DWM。

### B. 贴边隐藏：已重写为“宽容判定 + 吸附 + 缓动”

要点：

- 删除了 Windows/HTML 两侧的边缘装饰条（用户看到的“黄框”之一），拖动也不再走系统 `HTCAPTION`，所以没有系统贴边预览框；
- `EdgeHideController` 45ms 轮询：距工作区边缘 26px 内即视为贴边，松手后 `SetWindowPos` 吸齐；
- 隐藏：鼠标离开 420ms 防抖 + 移动前二次确认光标位置；
- 动画：15ms 定时 + `easeOutCubic` + 190ms 时长，结束时强制落到目标矩形（避免半路“卡住”）；
- 唤醒：光标进入工作区边缘 5px（+3px 容差）感应条即滑回；
- 任何外部移动（键盘、系统吸附、换屏）都会让 `_hidden` 复位，不再复原到过期坐标；
- `RestoreNow()` 是同步的，关闭窗口/热键/沉浸模式都会先调用它；
- 已知未做：`TrySuspendAsync()` 挂起 WebView2（会破坏页面状态，收益不抵风险）、显示器拔插事件（靠轮询自然恢复）。

### C. 章节字体：显示层已稳定，文本层仍未还原

番茄把正文中**一部分**字符替换成私用区码位（本机抓到的字体映射范围 U+E3E8–U+E55B），其余字符保持原样；页面用 `@font-face`（字体家族名形如 `DNMrHsV173Pd4pgy`，字重 400/500/700，woff2/woff/otf 多 CDN）只为私用区码位提供字形。字体本体是 **SourceHanSansSC-Normal 的子集**（name 表可读），cmap 只含 362 个私用区码位，没有字形名（post 3.0）。

因此正文必然混排“私用区字形（章节字体）+ 普通汉字（兜底字体）”，兜底字体必须和章节字体同风格，否则就是“一会儿粗一会儿细”。

现在的做法：

- `FanqieParser.ExtractChapterFont` 解析页面 `<style>` 里的 `@font-face`：取家族名、字重 400 优先、URL 按 woff2 > woff > otf 排序并去重；
- 从容器 `style="font-family:..."` 推断风格（`--reading-body-font` 走黑体或宋体栈）；
- CSS 去掉 `text-align: justify` / `text-justify: inter-ideograph` 与多余字距（CJK 两端对齐只在字之间加空隙，正是“不规则空白”的来源）；
- `.chapter-body` 固定 `font-weight: 400`、`font-synthesis: none`；
- 字体缓存改为 24 条 FIFO。

仍未解决：复制/搜索/TTS 拿到的是映射后的码位。要还原文本层需要“私用区码位 → 真实字符”的表，字体里没有字形名，只能靠字形比对或 OCR，属于另一个量级的工程。

### D. 书架：三路数据合并（11-23 修复）

- 书架接口（`bookshelf/info` + `aid=1967`）**只返回 book_id**；
- `/api/book/simple/info`（POST `{book_ids: [...]}`）返回书名/作者/`creation_status`/`serial_count`/`last_chapter_title`/`latest_read_item_id`；
- `/api/reader/book/progress`（GET，无参）返回账号每本书读到哪（`book_id` + `item_id`/`order`）。

实现要点：
- `FanqieClient.Shelf.cs`（partial）里的 `CollectBooks` / `CollectProgress` 用**递归遍历**取任何形状的数据
  （数组 / 字典 / 驼峰与下划线字段），不依赖固定嵌套；
- 合并优先级：详情 > 书架条目；进度里的 `item_id`/`order` 覆盖；
- `creation_status`：**0 = 完结，1 = 连载**（官方前端 `_()` 里 `0 == +r ? "已读完" : "最新章"` 可佐证）；
- 阅读文案：`order<=0` → 空；`<=1` → 未读过；`< serial_count` → `读到 N 章`；否则 完结→已读完 / 连载→已读到最新章；
- 打开书籍：有 `chapterId` 直接开；否则拉目录后按 `order-1` 取章，再退回第一章。

## 7. 其他未完成/待验证项

- 真实扫码确认登录、真实账号书架列表：当前环境无番茄账号，只验证到二维码生成/轮询、书架鉴权错误提示与空书架。
- 书架接口参数已按官方前端修正（`aid=1967`），但仍需真实账号验证渲染效果。
- 发布包在受限 ACL 目录下可能触发 WebView2 初始化失败；用户应解压到普通目录。
- 当前版本未做自动更新、代理、下载、离线缓存。