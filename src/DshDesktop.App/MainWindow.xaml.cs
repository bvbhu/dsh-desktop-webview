using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DshDesktop.Application;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshDesktop.App;

public partial class MainWindow : System.Windows.Window
{
    // 非 readonly：设置窗口保存后要能就地换掉当前生效配置。
    private AppConfig _config;
    private readonly ShellViewModel _shell;
    private readonly SessionViewModel _session;
    private readonly SettingsViewModel _settings;
    private readonly ConfigStore _configStore;
    private readonly WebViewProbe _probe;
    private readonly string _webViewProfilePath;
    private readonly RunLog? _runLog;
    private SettingsWindow? _settingsWindow;
    private CoreWebView2Environment? _environment;
    private CoreWebView2ContextMenuItem? _settingsMenuItem;
    private CoreWebView2ContextMenuItem? _dragStripMenuItem;

    // 两种 WebView 宿主（见 SetupWebViewHost）：默认合成版（可被 WPF 浮层覆盖），
    // 仅当 DSH_WEBVIEW_HOST=hwnd 时才退回 XAML 声明的 HwndHost 版。
    private WebView2? _hwndView;
    private WebView2CompositionControl? _compositionView;

    // ---- 拖动热区手势判别（备选1·透传点击）----
    // 合成版宿主必须捕获鼠标才能拖动窗口，因此改用手势判别：快速点按 → 把这次点击
    // 合成转发给页面；按下后拖动超过阈值 → 拖动窗口。四个字段只在左键按下期间有效。
    private bool _stripActive;          // 左键已在拖动条按下
    private Point _stripDownPos;        // 按下位置（窗口坐标，用于算位移阈值）
    private MouseButtonEventArgs? _stripDownArgs; // 保存 Down 事件，抬起时重建出点击
    private bool _stripDragging;        // 已越过阈值、进入窗口拖动

    /// <summary>判定"拖动"的最小位移（DIP）。小于它属点击，转发进页面。</summary>
    private const double DragGestureThreshold = 4;

    /// <summary>重置标志，防止浏览器进程连续崩溃时反复重建（见 RecoverFromBrowserCrash）。</summary>
    private bool _recovering;

    /// <summary>
    /// 窗口最小 500×500（用户指定 2026-09-14）。单一真相源，两处消费：构造期设
    /// MinWidth/MinHeight 拦住拖拽缩小；RestoreWindowPosition 用它判定配置里的异常尺寸。
    /// </summary>
    private const double MinWindowWidth = 500;
    private const double MinWindowHeight = 500;

    /// <summary>
    /// 设置窗口刚提交过临时 URL、正在等这次导航完成。成功后据此清错误提示并关掉设置
    /// 窗口；平时的导航（启动流程、刷新）不该碰设置窗口。
    /// </summary>
    private bool _awaitTempUrlNavigation;

    /// <summary>
    /// 启动加载浮层的导航追踪：探测本身也会导航主 WebView（<c>WebViewProbe.ProbeAsync</c>），
    /// 不能把探测完成当成"页面加载完成"。只有 <see cref="OnNavigateRequested"/> 发起的导航
    /// 是真实导航，用 <see cref="_pendingRealNav"/>/<see cref="_realNavId"/> 把它的
    /// NavigationStarting/NavigationCompleted 配对出来。
    /// </summary>
    private bool _pendingRealNav;
    private long _realNavId = -1;

    public MainWindow(
        AppConfig config,
        ShellViewModel shell,
        SessionViewModel session,
        SettingsViewModel settings,
        ConfigStore configStore,
        WebViewProbe probe,
        string webViewProfilePath,
        RunLog? runLog = null)
    {
        InitializeComponent();
        _config = config;
        _shell = shell;
        _session = session;
        _settings = settings;
        _configStore = configStore;
        _probe = probe;
        _webViewProfilePath = webViewProfilePath;
        _runLog = runLog;

        // 窗口最小 500×500：拦住拖拽缩小的下限（异常值判定见 MinWindowWidth 注释）。
        MinWidth = MinWindowWidth;
        MinHeight = MinWindowHeight;

        // 窗口样式：默认保留 WS_CAPTION。当初用 WindowStyle="None" 是为了"无标题栏"，
        // 但它会把 WS_CAPTION 一起摘掉，而 DWM 的最小化/最大化过渡动画与 caption/frame
        // 绑定 —— 没有它就没有原生过渡动画（2026-09-13 实测）。"无标题栏"本来也不靠它：
        // WindowChrome 的 CaptionHeight=0 已让客户区铺满整窗。
        // 退路：DSH_WINDOW_STYLE=none 退回旧行为，不必重新编译。
        if (string.Equals(Environment.GetEnvironmentVariable("DSH_WINDOW_STYLE"), "none",
                StringComparison.OrdinalIgnoreCase))
        {
            WindowStyle = System.Windows.WindowStyle.None;
            _runLog?.Append("[shell] 窗口样式=None（WS_CAPTION 已移除；原生过渡动画会失去，且不做自绘动画）");
        }
        else
        {
            _runLog?.Append("[shell] 窗口样式=SingleBorderWindow（保留 WS_CAPTION，用 DWM 原生过渡动画）");
        }

        // 把"最大化"的尺寸夹到显示器工作区，否则 WindowChrome 让客户区 == 整窗时，
        // 系统那圈边框（本机 8px）会造成"内容四周被裁"（见 WorkAreaMaximizer）。
        WorkAreaMaximizer.Attach(this, line => _runLog?.Append(line));

        // 启动即最大化延后到这里：句柄已创建、上面的 WM_GETMINMAXINFO 钩子已挂上。
        // 若在构造函数里直接设 WindowState，那次最大化会走系统默认值（工作区 + 一圈边框），
        // 内容被裁 8px 直到用户手动切一次。两个 SourceInitialized 处理器按注册顺序执行。
        var startMaximized = _config.WindowMaximized;
        SourceInitialized += (_, _) =>
        {
            if (startMaximized)
                WindowState = WindowState.Maximized;
        };

        SetupWebViewHost();
        RestoreWindowPosition();
        ApplyAppearance();

        ChromeBar.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        ChromeBar.MaximizeRequested += (_, _) => ToggleMaximize();
        ChromeBar.CloseRequested += (_, _) => Close();

        _session.NavigateRequested += OnNavigateRequested;
        _session.OpenConfigRequested += OnOpenConfigRequested;
        // 启动加载浮层的显隐与文字随会话阶段实时变化（探测中 / 启动服务中 / 打开页面中）
        _session.PropertyChanged += OnSessionPhaseChanged;

        SizeChanged += (_, _) =>
        {
            UpdateDragStrip();
            ApplyMaximizedContentInset();
        };
        StateChanged += OnStateChanged;
        // 初始状态也要归一：RestoreWindowPosition 可能已把 WindowState 设成 Maximized，
        // 而那时还没订阅 StateChanged，不补这一下会留下"带边框 + 缩放热区"的不一致状态。
        OnStateChanged(this, EventArgs.Empty);
        Closing += OnClosing;
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>
    /// 是否用合成宿主。默认合成版：它能被 WPF 浮层覆盖，因而能做出设计文档 §7.1 要求的
    /// "全窗口铺满 + 三键浮层 + 拖动热区"。两处开关：设置项 <c>UseHwndHost</c>（持久，
    /// 为文件拖放而设）与环境变量 <c>DSH_WEBVIEW_HOST=hwnd</c>（临时，不必重新编译）。
    /// </summary>
    private bool UseCompositionHost =>
        !_config.UseHwndHost &&
        !string.Equals(Environment.GetEnvironmentVariable("DSH_WEBVIEW_HOST"), "hwnd",
            StringComparison.OrdinalIgnoreCase);

    private void SetupWebViewHost()
    {
        _hwndView = WebView;
        if (!UseCompositionHost)
        {
            ApplyHwndLayout();
            _runLog?.Append(_config.UseHwndHost
                ? "[shell] WebView 宿主=hwnd（设置项开启；HwndHost 子窗口，顶栏形态）"
                : "[shell] WebView 宿主=hwnd（HwndHost 子窗口，顶栏形态）");
            return;
        }

        // 合成版的运行期前提：WinRT 投影程序集必须在位。必须显式探测，因为抛出点在
        // TryInitializeD3DImage → OnApplyTemplate → MeasureCore，而首次 Measure 发生在
        // Window.Show() 里，早于 Loaded，本类的 try/catch 够不着 —— 症状是两条 [fatal]
        // 且完全没有降级（2026-09-13 实测：单文件产物缺该 DLL）。按简单名加载一次正是
        // CsWinRT 解析投影程序集走的那条路；加载不了就根本不创建合成控件。
        if (!TryLoadWinRtProjection(out var projectionDetail))
        {
            _runLog?.Append($"[host-fallback] 合成版前置检查未通过：WinRT 投影程序集不可加载（{projectionDetail}）");
            return;
        }

        // 先摘掉 XAML 声明的 HwndHost 控件：此时还没到 Loaded，子 HWND 尚未创建，
        // 不会留下多余的隐藏窗口。
        WebHost.Children.Remove(_hwndView);

        try
        {
            var view = new WebView2CompositionControl();
            _compositionView = view;
            // 让 DragStrip 的光标跟随页面：合成宿主在 CursorChanged 里把页面光标写进自己的
            // Cursor DP，绑过去后悬停在拖动条上显示的也是页面光标（手型/文本等）而非默认
            // 箭头 —— 与悬停透传配合，顶部 40px 视觉上"不存在"。
            DragStrip.SetBinding(System.Windows.FrameworkElement.CursorProperty,
                new System.Windows.Data.Binding(nameof(WebView2CompositionControl.Cursor)) { Source = view });
            ApplyCompositionLayout();
            WebHost.Children.Add(view);
            _runLog?.Append("[shell] WebView 宿主=composition（全窗口铺满，顶栏为透明浮层）");
        }
        catch (Exception ex)
        {
            _compositionView = null;
            ApplyHwndLayout();
            WebHost.Children.Add(_hwndView);
            _runLog?.Append($"[host-fallback] 合成版不可用，已降级回 hwnd：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 合成版布局：宿主挪到第 0 行并跨满两行 —— 页面真正铺满整窗；顶栏底色收起
    /// （<c>TopBar.Collapsed</c>），只留可配置的 DragStrip 与三键浮层（§7.1、§7.3）。
    /// Row 必须和 RowSpan 一起改：两行网格里对 <c>Row=1</c> 的元素设 RowSpan=2 跨不出
    /// 第 2 行来，等于没跨 —— 这就是"全铺满"此前一直没真正生效的原因。
    /// </summary>
    private void ApplyCompositionLayout()
    {
        System.Windows.Controls.Grid.SetRow(WebHost, 0);
        System.Windows.Controls.Grid.SetRowSpan(WebHost, 2);
        // 加载浮层跟着宿主一起铺满整窗（否则顶上 40px 露出底层空白）
        System.Windows.Controls.Grid.SetRow(LoadingOverlay, 0);
        System.Windows.Controls.Grid.SetRowSpan(LoadingOverlay, 2);
        TopBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// hwnd 布局：宿主退回顶栏之下，并交还给系统标题栏。
    /// <para>
    /// 关键：<c>WindowChrome</c> 必须整个摘掉。<c>GlassFrameThickness=0</c> 会**隐藏**标准
    /// 标题栏（微软文档原文："disable and hide the standard frame"），<c>CaptionHeight</c>
    /// 只决定"哪块区域当成标题栏可拖动"，并不能让系统绘制标题文字 —— 实测把 CaptionHeight
    /// 调成 WindowCaptionHeight 后，客户区仍是 1500×750 == 整窗，标题栏像素高度 0。
    /// 所以要让 Windows 真正画出标题栏，只能清掉 WindowChrome。
    /// </para>
    /// </summary>
    private void ApplyHwndLayout()
    {
        System.Windows.Controls.Grid.SetRow(WebHost, 1);
        System.Windows.Controls.Grid.SetRowSpan(WebHost, 1);
        // hwnd 模式浮层只盖 WebView 行：TopBar 必须可见（它是唯一可拖动的地方）
        System.Windows.Controls.Grid.SetRow(LoadingOverlay, 1);
        System.Windows.Controls.Grid.SetRowSpan(LoadingOverlay, 1);

        // 交还系统标题栏：清掉 WindowChrome，Windows 才会绘制标题文字与三键，
        // 并让客户区从标题栏下方开始（这是"标题栏能看见"的唯一途径）。
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

        // 自绘顶栏整行收掉、三键浮层让位：系统标题栏已经提供了同样的功能，
        // 留着只会变成一条 40px 空白外加一套与系统按钮重叠的自绘按钮。
        TopBarRow.Height = new GridLength(0);
        TopBar.Visibility = Visibility.Collapsed;
        DragStrip.Visibility = Visibility.Collapsed;
        ChromeBar.Visibility = Visibility.Collapsed;
        _runLog?.Append("[shell] hwnd 布局：已移除 WindowChrome，交还系统标题栏");
    }

    /// <summary>
    /// WinRT 投影程序集是否可加载。单文件打包若没把 Microsoft.Windows.SDK.NET.dll
    /// 打进 bundle 就在这里失败 —— 提前失败，而不是留到 Window.Show 里变成不可捕获的 [fatal]。
    /// </summary>
    private static bool TryLoadWinRtProjection(out string detail)
    {
        try
        {
            var assembly = System.Reflection.Assembly.Load("Microsoft.Windows.SDK.NET");
            detail = assembly.GetName().Version?.ToString() ?? "version unknown";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>当前生效宿主的 CoreWebView2；未初始化完成时为 null。</summary>
    private CoreWebView2? Core =>
        _compositionView?.CoreWebView2 ?? _hwndView?.CoreWebView2;

    private async Task EnsureCoreAsync(CoreWebView2Environment env)
    {
        if (_compositionView is not null)
            await _compositionView.EnsureCoreWebView2Async(env);
        else if (_hwndView is not null)
            await _hwndView.EnsureCoreWebView2Async(env);
    }

    /// <summary>
    /// <c>CoreWebView2.ProcessFailed</c> 的统一处理。抽成方法而不是 lambda，是因为重建后的
    /// 新 core 也必须挂上同一个处理器 —— 否则第一次崩溃能自愈、第二次就只能留个死窗口。
    /// </summary>
    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _runLog?.Append($"[shell] WebView2 进程异常: kind={e.ProcessFailedKind}");

        // 只记日志不够：浏览器进程一旦退出 CoreWebView2 就永久失效，之后每次 Navigate 都抛
        // InvalidOperationException，窗口从此一片空白（2026-09-14 run.log 实际踩到）。
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            RecoverFromBrowserCrash();
    }

    /// <summary>
    /// 浏览器进程崩了之后重建视图：丢掉旧控件、换一个新的，而不是"把旧控件 Dispose 掉
    /// 再让它在原地复活"。
    /// <para>
    /// 必须换控件：控件的浏览器进程一旦退出，其内部 <c>CoreWebView2</c> 就处于已释放状态，
    /// 再对同一个控件调 <c>EnsureCoreWebView2Async</c> 会抛 <c>ObjectDisposedException</c>，
    /// 恢复永远失败。只恢复一次：连崩两次说明环境真跑不起来，无限重试只会变成崩溃循环。
    /// </para>
    /// </summary>
    private void RecoverFromBrowserCrash()
    {
        if (_recovering)
            return;

        _recovering = true;
        _runLog?.Append("[shell] 浏览器进程已退出，尝试重建 WebView");

        Dispatcher.Invoke(async () =>
        {
            try
            {
                if (_environment is null)
                {
                    _runLog?.Append("[shell] 无可用环境，放弃重建");
                    return;
                }

                // 关键：旧控件先摘掉再释放，且不能再拿它去 EnsureCoreWebView2Async。
                // 先摘是为了让它脱离可视树，否则已死的控件仍留在布局里，新控件叠上去会看到残影。
                var deadComposition = _compositionView;
                var deadHwnd = _compositionView is null ? _hwndView : null;

                if (deadComposition is not null)
                    WebHost.Children.Remove(deadComposition);
                else if (deadHwnd is not null)
                    WebHost.Children.Remove(deadHwnd);

                _compositionView = null;

                // 装一个新的控件。合成版与 hwnd 版都走这条路，只是类型不同。
                if (deadComposition is not null)
                {
                    var fresh = new WebView2CompositionControl();
                    _compositionView = fresh;
                    ApplyCompositionLayout();
                    WebHost.Children.Add(fresh);
                }
                else
                {
                    // hwnd 版的 WebView2 是 XAML 声明的实例、扔不掉，但它的 HwndHost 子窗口
                    // 同样是"一次性"的，所以用新建控件替换它，并把 _hwndView 指向新实例。
                    var fresh = new WebView2();
                    ApplyHwndLayout();
                    WebHost.Children.Add(fresh);
                    _hwndView = fresh;
                }

                // 旧控件这时才释放：放在新控件接上之后，中间不留"无控件"的窗口，布局不会跳。
                deadComposition?.Dispose();

                await EnsureCoreAsync(_environment);

                var core = Core;
                if (core is null)
                {
                    _runLog?.Append("[shell] 重建后 CoreWebView2 仍为 null，放弃");
                    return;
                }

                // 新 core 是全新对象：之前挂在旧 core 上的事件与菜单项都不再有效，必须重接。
                _settingsMenuItem = null;
                core.ProcessFailed += OnCoreProcessFailed;
                _probe.SetWebView(core);
                SetupContextMenu(core);

                _runLog?.Append("[shell] WebView 已重建，重载默认 URL");
                _session.Note("WebView 进程曾异常退出，已重建视图");
                try
                {
                    core.Navigate(_config.DefaultUrl);
                }
                catch (Exception ex)
                {
                    _runLog?.Append($"[nav-fail] 重建后导航仍失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _runLog?.Append($"[shell] WebView 重建失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _recovering = false;
            }
        });
    }

    private static string TryGetBrowserVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            // 未安装 WebView2 运行时会在这一步抛异常
            return $"(不可用: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// 恢复窗口位置与大小。配置里 <c>WindowX/WindowY</c> 为 0 是"从未记忆过"的哨兵值，
    /// 此时按工作区居中摆放。居中必须在运行期算：屏幕分辨率与任务栏位置各机不同，
    /// 写死的坐标换台机器就会偏到屏幕外或压在任务栏下。
    /// <para>
    /// 异常值防护（2026-09-14）：config.json 可能被手改、截断后仍可解析、或从小屏机器拷来。
    /// 尺寸低于最小 500×500 或超过工作区、位置整体落在工作区之外时一律恢复默认并居中。
    /// 结构非法的 JSON 已在 App.OnStartup 整体回退，这里兜的是"结构合法但数值离谱"这一层。
    /// 注意这里不恢复最大化状态：那要等句柄创建、WM_GETMINMAXINFO 钩子挂上之后再做。
    /// </para>
    /// </summary>
    private void RestoreWindowPosition()
    {
        var work = SystemParameters.WorkArea;
        var width = _config.WindowWidth;
        var height = _config.WindowHeight;

        // 尺寸护栏：低于最小 500×500（含 <=0）或大于工作区都算异常，恢复默认尺寸。
        // 上限用工作区而非硬编码常数：4K 屏上 1920×1080 的记忆值是合法的，不能误伤。
        var def = AppConfig.CreateDefault();
        if (width < MinWindowWidth || width > work.Width || height < MinWindowHeight || height > work.Height)
        {
            _runLog?.Append($"[shell] 窗口尺寸异常 ({width}×{height})，恢复默认 {def.WindowWidth}×{def.WindowHeight}");
            width = def.WindowWidth;
            height = def.WindowHeight;
        }

        Width = width;
        Height = height;

        if (!IsPlacementSane(_config.WindowX, _config.WindowY, work))
            CenterOnWorkArea();
        else
        {
            Left = _config.WindowX;
            Top = _config.WindowY;
        }
    }

    /// <summary>
    /// 位置是否可信：哨兵 (0,0) 表示"从未记忆"→ 居中；其余情况哪怕只露出一角也尊重用户记忆，
    /// 只有整体落在工作区之外才判定异常。
    /// </summary>
    private static bool IsPlacementSane(double x, double y, Rect work)
    {
        if (x == 0 && y == 0)
            return false; // 哨兵 → 走居中分支
        return x + 100 > work.Left && x < work.Right - 100
            && y + 100 > work.Top && y < work.Bottom - 100;
    }

    /// <summary>
    /// 按当前显示器工作区居中。用工作区而不是整屏：任务栏会吃掉一条边，按整屏居中会显得偏。
    /// </summary>
    private void CenterOnWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    /// <summary>
    /// 把当前配置套到所有"纯外观"元素上。构造期调用一次；设置窗口每次保存后再调一次，
    /// 这样三键与拖动热区不必重启就能生效。
    /// </summary>
    private void ApplyAppearance()
    {
        ChromeBar.Configure(_config);
        SetupDragStrip();

        // 把"实际生效了什么"写进日志：颜色配置解析不了就会静默回落主题色/默认色，
        // 用户只看到"设置疑似不生效"，这行日志让这件事一眼可查。
        _runLog?.Append($"[chrome] 图标色={Describe(_config.ChromeButtonIconColor)} "
            + $"底色={Describe(_config.ChromeButtonBackground)} "
            + $"悬停延迟={_config.ChromeHoverDelayMs}ms 默认显隐={_config.ChromeButtonsDefault}");
        _runLog?.Append($"[strip] 启用={_config.DragStripEnabled} 色={Describe(_config.DragStripColor)} "
            + $"透明度={_config.DragStripOpacity} "
            + $"inset={_config.DragStripLeftInset}/{_config.DragStripRightInset} 高={_config.DragStripHeight}");
    }

    private static string Describe(string color) =>
        string.IsNullOrWhiteSpace(color) ? "(跟随主题)" : color.Trim();

    private void SetupDragStrip()
    {
        if (!_config.DragStripEnabled)
        {
            DragStrip.Visibility = Visibility.Collapsed;
            return;
        }
        DragStrip.Height = _config.DragStripHeight;
        DragStrip.Margin = new Thickness(_config.DragStripLeftInset, 0, _config.DragStripRightInset, 0);

        // 解析失败不再静默：回落设计文档的默认红并说明原因（手改 config.json 时最容易踩）。
        if (HexColor.TryParse(_config.DragStripColor, out var a, out var r, out var g, out var b))
        {
            DragStrip.Background = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        else
        {
            _runLog?.Append($"[strip] 颜色无法解析：'{_config.DragStripColor}'，已回落默认 #FF0000");
            DragStrip.Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));
        }

        DragStrip.Opacity = _config.DragStripOpacity;
        UpdateDragStrip();
    }

    private void UpdateDragStrip()
    {
        if (!_config.DragStripEnabled)
            return;
        var width = ActualWidth - _config.DragStripLeftInset - _config.DragStripRightInset;
        DragStrip.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 拖动热区的左键按下：不立即拖动，先进入手势判别。设计文档 §7.3 原本把这一条记为
    /// "刻意挡在页面之上"，现改为"快速点按 → 点击透传进页面；位移超阈值 → 拖动窗口"。
    /// <para>
    /// 点按能透传，是因为合成版宿主经 D3DImage 输出，命中测试在拖动条像素上 WPF 优先 ——
    /// 鼠标事件被 WPF 吃掉、WebView2 收不到，只能由我们重建按下/抬起再转发进浏览器。
    /// 按下后不捕获鼠标，只要位移越过阈值即判定为拖动，放弃透传、改走原生标题栏拖动。
    /// </para>
    /// </summary>
    private void DragStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 不捕获鼠标：CaptureMouse 会在 Win32 层 SetCapture，与 BeginWindowDrag 的
        // WM_NCLBUTTONDOWN 标题栏拖动循环打架（窗口拖不动）；更糟的是抬起时 ReleaseMouseCapture
        // 会同步触发 LostMouseCapture、清空 _stripDownArgs，让 ForwardClickToPage 永远进不去。
        // 手势判别也不需要捕获：阈值仅 4 DIP，40px 高的拖动条内必然收得到 MouseMove 与 MouseUp。
        _stripActive = true;
        _stripDownArgs = e;
        _stripDownPos = e.GetPosition(this);
        _stripDragging = false;
        e.Handled = true;

        // hwnd 宿主没有可转发点击的组合控制，手势判别无意义 —— 保持旧行为（按下即拖动）。
        if (_compositionView is null)
        {
            BeginWindowDrag();
        }
    }

    /// <summary>
    /// 捕获意外丢失时兜底复位手势状态。本类已不再 <c>CaptureMouse</c>，正常流程不会触发，
    /// 保留是为防御（系统/第三方抢走捕获而恰好没收到 MouseUp）。这条路径千万别转发点击。
    /// </summary>
    private void DragStrip_LostMouseCapture(object? sender, MouseEventArgs e)
    {
        _stripActive = false;
        _stripDragging = false;
        _stripDownArgs = null;
    }

    /// <summary>
    /// 拖动热区上的鼠标移动，两条职责：未按下时把移动以 Move 转发给页面（悬停效果、光标样式、
    /// tooltip 在顶部 40px 才跟随，否则合成宿主的 OnMouseMove 被本条截走，页面悬停状态停在
    /// 进入拖动条前的那一刻）；按下手势中位移越过阈值则升级为窗口拖动。
    /// </summary>
    private void DragStrip_MouseMove(object sender, MouseEventArgs e)
    {
        // 未按下时把光标位置以 Move（无按键）转发给页面；一旦按下就交给下面的拖动判别，
        // 避免两条路径抢消息。
        if (!_stripActive)
        {
            SendMouseEvent(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None);
            return;
        }
        if (_stripDragging)
            return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _stripDownPos.X) + Math.Abs(pos.Y - _stripDownPos.Y) < DragGestureThreshold)
            return;

        _stripDragging = true;
        _stripDownArgs = null;
        // 交还给系统标题栏拖动循环。没有 CaptureMouse 无需释放；SendMessage 同步跑完整个
        // 拖动直到用户松开，期间 _stripDragging=true 阻止重复进入或误判为点击。
        BeginWindowDrag();
    }

    /// <summary>
    /// 拖动热区上的左键抬起：若从未越过阈值（即一次"点按"），把这次点击重建出来
    /// 转发进页面；否则手势已升级为拖动，抬起交给系统拖动循环处理，这里什么都不做。
    /// </summary>
    private void DragStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stripActive)
            return;
        _stripActive = false;

        // 拖动手势已被系统接管，抬起不归我们管。
        if (_stripDragging)
        {
            _stripDragging = false;
            return;
        }

        // 快速点按：反射调宿主的 SendMouseInput（Move→Down→Up）把点击送进页面 —— 与宿主
        // 自身 OnMouseDown 同一条转发通路，但跳过其焦点守卫与 WPF 路由事件（RaiseEvent
        // 重放实测页面收不到）。不要在转发前 ReleaseMouseCapture：会清空 _stripDownArgs。
        if (_stripDownArgs is not null && _compositionView is not null)
        {
            ForwardClickToPage();
        }
        _stripDownArgs = null;
    }

    /// <summary>
    /// 缓存的 <c>WebView2CompositionControl.SendMouseInput</c> 反射句柄：宿主把鼠标送进
    /// 浏览器的转发原语（其 OnMouseDown/Up/Move 内部即调它），非 public 故用反射。
    /// 点击透传与悬停透传共用。
    /// </summary>
    private static System.Reflection.MethodInfo? _sendMouseInput;

    /// <summary>
    /// 把一次鼠标事件经宿主的 <c>SendMouseInput</c> 转发进页面。它非 public 但 XML 文档有载，
    /// 直接反射调用可绕开 OnMouseDown 里可能的焦点守卫与 WPF 路由事件的不确定性
    /// （<c>RaiseEvent(Mouse.MouseDownEvent)</c> 重放实测页面收不到）。坐标取光标相对宿主的
    /// DIP 乘以 DpiScaleX 得物理像素，与宿主 OnMouseDown 内算法一致。Core 为 null
    /// （CoreWebView2 尚未就绪）时直接放弃，否则只会反复抛异常刷屏。
    /// </summary>
    private void SendMouseEvent(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys)
    {
        if (_compositionView is null || Core is null)
            return;

        var p = Mouse.GetPosition(_compositionView);
        var scale = VisualTreeHelper.GetDpi(_compositionView).DpiScaleX;
        var pt = new System.Drawing.Point((int)(p.X * scale), (int)(p.Y * scale));

        var send = _sendMouseInput ??= typeof(WebView2CompositionControl).GetMethod(
            "SendMouseInput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null,
            new[] { typeof(CoreWebView2MouseEventKind), typeof(CoreWebView2MouseEventVirtualKeys), typeof(uint), typeof(System.Drawing.Point) },
            null);

        if (send is null)
        {
            _runLog?.Append("[strip] SendMouseInput 反射未找到，放弃转发鼠标事件");
            return;
        }

        try
        {
            send.Invoke(_compositionView, new object[] { kind, keys, 0u, pt });
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            _runLog?.Append($"[strip] SendMouseInput 转发抛异常：{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
    }

    /// <summary>
    /// 把一次"点按"转发进页面：Move（定位）→ LeftButtonDown → LeftButtonUp。
    /// 先 Move 是因为拖动条盖住了宿主顶部，其 OnMouseMove 没收到这次移动，浏览器侧的
    /// "最近光标"可能陈旧。virtualKeys 对齐 Win32 MK_*：Down/Up 左键按下。
    /// </summary>
    private void ForwardClickToPage()
    {
        SendMouseEvent(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None);
        SendMouseEvent(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton);
        SendMouseEvent(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.LeftButton);
    }

    /// <summary>
    /// 把左键按下交给系统标题栏拖动（WM_NCLBUTTONDOWN + HTCAPTION）。不用
    /// <c>Window.DragMove()</c>：它最大化时既不还原也不移动，且跟手性差、不参与 Aero 吸附。
    /// 本窗口保留了 <c>WS_CAPTION</c>（见构造函数），系统能接住这个 hit-test 并接管整个拖拽
    /// 循环，由此免费得到"最大化时往下拖会还原"与"拖到屏幕边缘吸附"两件原生能力，
    /// 也不需要手动 SetCapture。退路的 <c>DSH_WINDOW_STYLE=none</c> 模式没有它，属已知取舍。
    /// </summary>
    private void BeginWindowDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // 取物理像素的全局光标位置：WM_NCLBUTTONDOWN 的 lParam 就是物理屏幕坐标，
        // 不经过 WPF 的 DIP 换算（高 DPI 下 PointToScreen 拿到的是 DIP，直接套会偏位）。
        if (!GetCursorPos(out var pt))
            return;

        // lParam：低 16 位 = x，高 16 位 = y。取 16 位有符号值，多点屏上坐标可为负。
        var lParam = (pt.Y & 0xFFFF) << 16 | (pt.X & 0xFFFF);

        // 必须在左键仍按下、且鼠标捕获仍在本窗口时发送，系统才认得这是一次"标题栏按下"。
        SendMessage(hwnd, WmNcLButtonDown, HTCaption, lParam);
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HTCaption = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    // ---- 最大化 / 最小化 / 还原 ----
    // 只设 WindowState，过渡动画全部交给 DWM：窗口保留着 WS_CAPTION，系统会照常播放原生
    // 动画。曾自绘过一套"缩放 + 淡出"兜底过渡，与原生动画叠加会打架，已应用户要求移除。
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // hwnd 模式已摘掉 WindowChrome，此时 _winChrome 为 null —— 缩放热区由系统标题栏接管。
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = maximized ? new Thickness(0) : new Thickness(6);
        // 最大化时收掉细边框：省回那一圈像素，也与「最大化时移除缩放热区」同调（§7.4）。
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        ChromeBar.SetMaximized(maximized);

        // 状态与尺寸两条路径都算一次（幂等，谁后到谁生效）。
        ApplyMaximizedContentInset();
    }

    /// <summary>
    /// 最大化时把内容内缩一圈，抵掉系统额外加上的那圈边框（见 WorkAreaMaximizer）。
    /// 不改窗口矩形 —— 那条路系统不让改。
    /// </summary>
    private void ApplyMaximizedContentInset()
    {
        WindowFrame.Margin = WorkAreaMaximizer.ComputeMaximizedContentInset(this);
    }

    private async Task InitializeAsync()
    {
        _runLog?.Append($"[shell] WebView2 用户数据目录: {_webViewProfilePath}");
        _runLog?.Append($"[shell] WebView2 运行时版本: {TryGetBrowserVersion()}");
        // Tier 0 = 软件渲染（无可用 GPU / 远程会话常见）。视觉宿主依赖合成路径，
        // 这里是判断"合成版为何空白"的第一手依据。
        _runLog?.Append($"[shell] WPF 渲染层级: {RenderCapability.Tier >> 16}（0=软件渲染）");

        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, _webViewProfilePath);
            await EnsureCoreAsync(_environment);
        }
        catch (Exception ex)
        {
            // Loaded 处理器是 async void：此处不捕获，异常会直达 Dispatcher，表现为
            // "运行 exe 什么也不显示"且日志无痕。标签用纯 ASCII 便于脚本判读关键词。
            _runLog?.Append($"[init-fail] WebView2 初始化失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, $"WebView2 初始化失败：{ex.Message}",
                "DSH Desktop Webview", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var core = Core;
        if (core is null)
        {
            _runLog?.Append("[init-fail] 初始化后 CoreWebView2 仍为 null，放弃会话");
            return;
        }

        core.ProcessFailed += OnCoreProcessFailed;
        core.NavigationCompleted += OnCoreNavigationCompleted;
        // 探测自身也会导航主 WebView，所以"真实导航"要用 NavigationStarting 把 Id 配对出来，
        // 它的 NavigationCompleted 才算"页面加载完成"（浮层消失的时刻）。
        core.NavigationStarting += OnCoreNavigationStarting;

        _probe.SetWebView(core);
        SetupContextMenu(core);

        // "ready" 是纯 ASCII 锚点，便于脚本判读日志关键词；中文负载仅供人读。
        _runLog?.Append("[shell] ready 窗口就绪，开始会话");
        _ = _session.StartSessionAsync(_config, CancellationToken.None);
    }

    private void SetupContextMenu(CoreWebView2 core)
    {
        core.ContextMenuRequested += (s, args) =>
        {
            // 菜单项复用（官方样例：custom items should be reused whenever possible），
            // 避免每次右键都新建对象并重复挂 CustomItemSelected。
            var item = _settingsMenuItem;
            if (item is null)
            {
                var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var label = language.Equals("zh", StringComparison.OrdinalIgnoreCase)
                    ? "设置"
                    : "Settings";
                item = _environment!.CreateContextMenuItem(
                    label, null, CoreWebView2ContextMenuItemKind.Command);
                item.CustomItemSelected += (_, _) => Dispatcher.Invoke(OpenSettings);
                _settingsMenuItem = item;
            }

            // 拖动层开关（2026-09-14 用户要求）：CheckBox 自带勾选态，「按下后直接切换」。
            // Label 创建后不可改，所以状态展示靠 IsChecked 而不是换文字 —— 每次弹出前刷新。
            var strip = _dragStripMenuItem;
            if (strip is null)
            {
                var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var label = language.Equals("zh", StringComparison.OrdinalIgnoreCase)
                    ? "拖动层"
                    : "Drag strip";
                strip = _environment!.CreateContextMenuItem(
                    label, null, CoreWebView2ContextMenuItemKind.CheckBox);
                strip.CustomItemSelected += (_, _) => Dispatcher.Invoke(ToggleDragStripFromMenu);
                _dragStripMenuItem = strip;
            }
            strip.IsChecked = _config.DragStripEnabled;

            // 只「追加」不「接管」：一旦 args.Handled = true 即声明菜单由宿主呈现，WebView2
            // 便不再显示任何菜单，而本类并不创建自己的菜单 —— 那样右键会完全没反应。
            // §7.5 要求的是「追加一项」，因此必须保持 Handled = false（默认值）。
            args.MenuItems.Insert(args.MenuItems.Count, item);
            args.MenuItems.Insert(args.MenuItems.Count, strip);
        };
    }

    /// <summary>
    /// 右键菜单切换拖动层。与设置窗口「保存」同一条生效路径（<see cref="ApplyAppearance"/>）
    /// 并立即落盘 —— 不然重启后又回到旧状态，用户会觉得开关"记不住"。共享的 <c>_settings</c>
    /// 也要同步：设置窗口直接绑它，若窗口开着复选框要立即反映新状态，其后再点「保存」
    /// 也不会把旧值写回去。
    /// </summary>
    private void ToggleDragStripFromMenu()
    {
        _config = _config with { DragStripEnabled = !_config.DragStripEnabled };
        _settings.DragStripEnabled = _config.DragStripEnabled;

        // 与 OnConfigSaved/OnClosing 一致：先并入当前窗口几何再落盘，避免把旧坐标写回。
        _config = _shell.ApplyTo(_config);
        _configStore.Save(_config);

        ApplyAppearance();
        // ASCII 锚点便于脚本判读；中文负载仅供人读（ApplyAppearance 已另记 [strip] 明细）。
        _runLog?.Append($"[strip] toggled via context menu -> {(_config.DragStripEnabled ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// 会话要求导航。**必须容错**：浏览器进程崩了之后，<c>CoreWebView2.Navigate</c> 会抛
    /// <c>InvalidOperationException</c>（"The WebView control is no longer valid because the
    /// browser process crashed"），而这条路径运行在会话流程里 —— 异常会一路冒到
    /// Dispatcher 弹「发生未处理异常」，或者让会话停在一个既没导航也没提示的状态。
    /// 上一次真实日志里就是这条（[error] InvalidOperationException: ... browser process crashed）。
    /// <para>
    /// 崩了就记一行日志并放弃这一次导航；重建由 <c>ProcessFailed</c> 的恢复逻辑负责，
    /// 不在这里硬试。
    /// </para>
    /// </summary>
    private void OnNavigateRequested(string url)
    {
        // 只有会话显式发起的这次导航是"真实导航"；它的 NavigationCompleted
        // 才是加载浮层消失的时刻（探测的导航不算，见 OnCoreNavigationStarting）
        _pendingRealNav = true;
        ShowLoadingOverlay("正在打开页面…");
        _runLog?.Append($"[nav] 导航到 {LogRedaction.Url(url)}");
        Dispatcher.Invoke(() =>
        {
            try
            {
                Core?.Navigate(url);
            }
            catch (Exception ex)
            {
                // 标签留纯 ASCII 便于脚本判读；中文负载仅供人读。
                _runLog?.Append($"[nav-fail] 导航失败（WebView 可能已失效）：{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void OnOpenConfigRequested()
    {
        Dispatcher.Invoke(OpenSettings);
    }

    /// <summary>
    /// 会话阶段变化 → 更新加载浮层（<see cref="LoadingOverlay"/>）的显隐与文字。
    /// 只认 <see cref="SessionViewModel.Phase"/> 的通知；NeedsToken/Failed 时让位
    /// —— 那两种阶段需要用户介入（填临时 URL / 看错误），浮层再盖着就没法交互了。
    /// </summary>
    private void OnSessionPhaseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.Phase))
            return;

        Dispatcher.Invoke(UpdateLoadingOverlay);
    }

    private void UpdateLoadingOverlay()
    {
        switch (_session.Phase)
        {
            case SessionPhase.Probing:
                ShowLoadingOverlay("正在连接服务，请稍候…");
                break;
            case SessionPhase.Launching:
                ShowLoadingOverlay("正在启动服务，请稍候…");
                break;
            case SessionPhase.Ready:
                // Ready 只说明抓到了 URL，页面可能还在路上；真正的"加载完成"
                // 由真实导航的 NavigationCompleted（见下）收尾
                ShowLoadingOverlay("正在打开页面…");
                break;
            case SessionPhase.NeedsToken:
            case SessionPhase.Failed:
                HideLoadingOverlay();
                break;
        }
    }

    private void ShowLoadingOverlay(string message)
    {
        LoadingMessage.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoadingOverlay() =>
        LoadingOverlay.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 导航开始。把"会话显式发起的真实导航"（<see cref="_pendingRealNav"/>）标记出
    /// NavigationId，供 <see cref="OnCoreNavigationCompleted"/> 识别——探测走的也是
    /// 同一个 WebView，不能混淆两者的完成事件。
    /// </summary>
    private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_pendingRealNav)
            return;

        _pendingRealNav = false;
        // NavigationId 是 ulong；数值远小于 long 上限，收窄存 long 与哨兵 -1 配套
        _realNavId = unchecked((long)e.NavigationId);
    }

    /// <summary>
    /// 导航完成。只在"设置窗口刚提交过临时 URL"这次导航上做事（见 <see cref="_awaitTempUrlNavigation"/>）：
    /// 页面成功打开 → 清除会话错误提示（那条「探测返回 401」已经过时）并关闭设置窗口。
    /// 导航失败则保留标志与错误提示，用户可以改 URL 再试；下次导航成功时同样生效。
    /// <para>
    /// NavigationCompleted 在 UI 线程回调，无需再 Invoke。挂接点在 CoreWebView2 初始化后
    /// （与 ProcessFailed 同处），重建 WebView 后依然有效。
    /// </para>
    /// </summary>
    private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // 真实导航完成 = 页面加载完成（无论成败）：加载浮层让位。
        // 失败时不重开浮层 —— 错误信息会出现在控制台/设置窗口，盖着反而挡视线。
        if (unchecked((long)e.NavigationId) == _realNavId)
        {
            _realNavId = -1;
            _runLog?.Append($"[nav] 页面加载完成（IsSuccess={e.IsSuccess}），收起加载浮层");
            HideLoadingOverlay();
        }

        if (!_awaitTempUrlNavigation)
            return;

        if (!e.IsSuccess)
        {
            _runLog?.Append($"[nav] 临时 URL 导航未成功（Id={e.NavigationId}），保留错误提示等用户重试");
            return;
        }

        _awaitTempUrlNavigation = false;
        _runLog?.Append("[nav] 临时 URL 页面已打开，清除错误并关闭设置窗口");
        _session.ClearError();
        _settingsWindow?.Close();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, _session);
        window.ConfigSaved += OnConfigSaved;
        window.ClearCookiesRequested += OnClearCookiesRequested;
        window.ClearWebViewDataRequested += OnClearWebViewDataRequested;
        window.TempUrlSubmitted += () => _awaitTempUrlNavigation = true;
        window.Closed += (_, _) =>
        {
            window.ConfigSaved -= OnConfigSaved;
            window.ClearCookiesRequested -= OnClearCookiesRequested;
            window.ClearWebViewDataRequested -= OnClearWebViewDataRequested;
            // 窗口被关掉（无论哪条路径）就不再等它的导航，否则之后一次无关的成功导航
            // 会误触发"关窗"逻辑（虽然那时窗口已关，ClearError 仍会错误地清掉提示）。
            _awaitTempUrlNavigation = false;
        };
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>
    /// 清空 WebView2 的全部 Cookie（设置窗口的按钮）。
    /// <para>
    /// 结果必须写进控制台：这个动作没有任何可见副作用 —— 成功与否用户看不出来，
    /// 而控制台就是这个项目约定的输出出口（见 <see cref="SessionViewModel.Note"/>）。
    /// </para>
    /// </summary>
    private void OnClearCookiesRequested()
    {
        try
        {
            var core = Core;
            if (core is null)
            {
                _session.Note("清除 Cookie 失败：WebView2 尚未初始化");
                return;
            }

            core.CookieManager.DeleteAllCookies();
            _session.Note("已清除 WebView2 的全部 Cookie（页面需刷新后才会体现为登出）");
        }
        catch (Exception ex)
        {
            _session.Note($"清除 Cookie 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 设置窗口点「保存」后的唯一写盘出口。
    /// 用 _shell.ApplyTo 把当前窗口位置并进去，避免把内存里启动时的旧坐标写回 config.json。
    /// </summary>
    private void OnConfigSaved(AppConfig cfg)
    {
        _config = _shell.ApplyTo(cfg);
        _configStore.Save(_config);
        ApplyAppearance();
        _runLog?.Append("[config] 设置已保存，外观即时套用");
    }

    /// <summary>
    /// 清除 WebView2 的浏览数据（设置窗口的新按钮，紧挨着「清除 Cookie」）。
    /// <para>
    /// 与「清除 Cookie」的区别：Cookie 只是浏览数据的一种。这个动作清的是
    /// <c>CoreWebView2BrowsingDataKinds.AllProfile</c> 覆盖的全套本地数据 ——
    /// 缓存、localStorage、IndexedDB、Service Worker、已保存的表单等，
    /// 对应"页面状态脏了但清 Cookie 也没用"的排查场景。
    /// </para>
    /// <para>
    /// 这是个 async API，必须等它真的完成才能说"已清除"—— 否则用户看到提示就去刷新，
    /// 结果清净到一半又被写回去。不能用 <c>.Wait()</c>：在 UI 线程上会死锁
    /// （continuation 需要回到这个被阻塞的线程）。
    /// </para>
    /// </summary>
    private async void OnClearWebViewDataRequested()
    {
        try
        {
            var core = Core;
            if (core is null)
            {
                _session.Note("清除 WebView 数据失败：WebView2 尚未初始化");
                return;
            }

            _session.Note("正在清除 WebView 数据（缓存 / 存储 / Cookie 等）…");
            // 只清浏览数据，不碰用户数据文件夹本身：删目录会连 WebView2 的运行时文件
            // 一起带走，下次启动直接起不来。
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            _session.Note("已清除 WebView 数据（页面需刷新后才会体现）");
        }
        catch (Exception ex)
        {
            _session.Note($"清除 WebView 数据失败：{ex.Message}");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _shell.WindowX = (int)Left;
        _shell.WindowY = (int)Top;
        _shell.WindowWidth = (int)Width;
        _shell.WindowHeight = (int)Height;
        _shell.WindowMaximized = WindowState == WindowState.Maximized;

        // 只写"当前生效配置 + 窗口记忆"。这里**不能**去读 _settings.BuildConfig()：
        // 设置窗口的绑定是直接改共享 SettingsViewModel 的，读它等于把用户改了却没点保存的
        // 字段一起落盘。
        var cfg = _shell.ApplyTo(_config);
        _configStore.Save(cfg);
        _runLog?.Append("[config] 关窗时已保存窗口位置与当前生效配置");
        _runLog?.Append("[shell] 窗口关闭，释放会话与子进程");
        _session.Dispose();
    }
}
