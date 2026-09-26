using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DshDesktop.Application;
using DshDesktop.App;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;

namespace SettingsPreview;

/// <summary>
/// Renders the real <see cref="SettingsWindow"/> offscreen to a PNG.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run --project tools/settings-preview -c Debug -- &lt;out.png&gt; [light|dark] [follow|needs-token|running|started|started401]</c>
/// <para>
/// The third argument picks a session/appearance state: <c>follow</c> checks the 「跟随主题」box,
/// <c>needs-token</c> drives the session into NeedsToken so the temp-URL panel shows,
/// <c>running</c>/<c>started</c>/<c>started401</c> drive the three service states
/// (复用外部实例 / 服务已启动 / 服务已启动但裸 URL 返回 401).
/// </para>
/// <para>
/// Render rather than read the XAML: only a render proves the rows still line up and that no
/// renamed key broke a DynamicResource at load time.
/// </para>
/// <para>
/// Windows PowerShell 5.1 runs on .NET Framework and cannot load this net8.0-windows assembly,
/// so the harness has to be a real WPF app. The window is placed far offscreen before <c>Show()</c>:
/// it must be shown for template/DynamicResource lookups to resolve, but must never flash on screen.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 中文标签直接可读，不受控制台代码页影响（PS 5.1 下默认 GBK，会打出乱码）。
        // 输出被重定向时该 setter 会抛「句柄无效」，忽略即可（重定向场景只看 ASCII 状态行）。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch (System.IO.IOException) { }
        var outFile = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "settings-preview.png");
        var dark = args.Length > 1 && args[1].Equals("dark", StringComparison.OrdinalIgnoreCase);

        // Use the REAL App class: every style SettingsWindow references lives in App.xaml, and
        // merging a different ResourceDictionary would not bring those keys along.
        //
        // OnStartup does run, at the FIRST pump rather than at construction. Before the guard
        // existed it applied the SYSTEM theme over our requested one and spun up a real MainWindow
        // with a full WebView2 init. Two-part fix: (1) App.OnStartup returns early under
        // DSH_DESKTOP_PREVIEW=1, (2) we pump once below to CONSUME that posted startup now, so our
        // theme swap happens after OnStartup and nothing reverts it later.
        Environment.SetEnvironmentVariable("DSH_DESKTOP_PREVIEW", "1");
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Consume the posted startup now, or the swap below gets overwritten at the next pump.
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // App.xaml merges Themes/Dark.xaml at InitializeComponent time as a design-time default.
        // Remove that entry by Source (the /Themes/ marker ThemeSwitcher also keys off) and append
        // ours, leaving exactly one theme dictionary. Clear()+Add() fails to re-resolve values the
        // visual tree already resolved; Insert(0,...) leaves a mix of both dictionaries.
        var dictionaries = app.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true)
                dictionaries.RemoveAt(i);
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        });

        // Compare against the LITERAL the theme file declares. Do NOT compare FindResource against
        // app.Resources: a consistently-wrong theme makes both return the same wrong value and the
        // check would pass. If a theme's palette changes, update these.
        var expectedWindowBg = dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
        var expectedText = dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x1A, 0x1A, 0x1A);

        var appWindowBg = ((SolidColorBrush)app.Resources["WindowBackgroundBrush"]).Color;
        var activeText = ((SolidColorBrush)app.Resources["TextBrush"]).Color;
        Console.WriteLine($"[theme] requested={(dark ? "Dark" : "Light")} "
            + $"WindowBackgroundBrush=#{appWindowBg.R:X2}{appWindowBg.G:X2}{appWindowBg.B:X2} "
            + $"TextBrush=#{activeText.R:X2}{activeText.G:X2}{activeText.B:X2}");

        if (appWindowBg != expectedWindowBg || activeText != expectedText)
        {
            Console.Error.WriteLine($"[theme] WRONG DICTIONARY: expected bg=#{expectedWindowBg.R:X2}{expectedWindowBg.G:X2}{expectedWindowBg.B:X2} "
                + $"text=#{expectedText.R:X2}{expectedText.G:X2}{expectedText.B:X2}");
            return 3;
        }

        // A non-empty background so the checkbox renders UNCHECKED and the picker beside it is
        // enabled — the checked state is just one checkbox with the picker greyed out.
        // The third arg is a comma-separated state list, applied LIVE after Show() (see below) so
        // the render exercises the real INPC → Visibility path, not initial state.
        var modes = args.Length > 2
            ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        var wants = (string name) => modes.Contains(name, StringComparer.OrdinalIgnoreCase);

        // "main" renders the real MainWindow (real WebView2 + session machinery) to check the
        // startup loading overlay. Runs before the SettingsWindow flow.
        if (wants("main"))
            return RenderMain(app, outFile);

        // "ext-links-e2e"：真实 WebView2 端到端验证「点链接 → 交给外部浏览器」。
        if (wants("ext-links-e2e"))
            return RenderExternalLinksEndToEnd(app, outFile);

        // "needs-token" forces a real 401 probe: Phase's setter is private and faking it would not
        // prove the panel's visibility binding works.
        var needsToken = wants("needs-token");

        var config = AppConfig.CreateDefault() with
        {
            ChromeButtonBackground = "#123456",
        };

        var vm = new SettingsViewModel(config);
        // Explicit type: the fakes are siblings, so the conditional has no common type to infer.
        var started = wants("started");
        // "started401"：自家启动成功但后探测打裸 URL 返回 401 —— 回归 2026-09-24 的
        // 「服务已启动」被误判成「复用外部实例」（归属被反向清掉）的缺陷。
        var started401 = wants("started401");
        IEndpointProbe probe = wants("needs-token") ? new UnauthorizedProbe()
            : wants("running") ? new OkProbe()
            : started401 ? new SequenceProbe(
                new ProbeResult(ProbeOutcome.Unreachable, null, null), // 窗口ctor自动刷新（fire-and-forget，会先吃掉一个）
                new ProbeResult(ProbeOutcome.Unreachable, null, null), // StartServiceAsync 启动前：未启动
                new ProbeResult(ProbeOutcome.Other, 401, "unauthorized")) // 启动后：裸 URL 要 token
            : started ? new SequenceProbe(
                new ProbeResult(ProbeOutcome.Unreachable, null, null), // 窗口ctor自动刷新（fire-and-forget，会先吃掉一个）
                new ProbeResult(ProbeOutcome.Unreachable, null, null), // StartServiceAsync 启动前：未启动
                new ProbeResult(ProbeOutcome.Ok, 200, null))           // StartServiceAsync 启动后：已启动
            : new NopProbe();
        var session = new SessionViewModel(probe, started || started401 ? new UrlLauncher() : new NopLauncher());

        // Seed the console so the two clear buttons show above real-looking output, not an empty box.
        session.Note("已清除 WebView2 的全部 Cookie（页面需刷新后才会体现为登出）");
        session.Note("正在清除 WebView 数据（缓存 / 存储 / Cookie 等）…");

        // The window is created BEFORE the session runs, and the session starts only after Show():
        // the live app opens this window when the session hits NeedsToken, so this order exercises
        // the live NeedsTokenVisibility update path (phase change → window's PropertyChanged → panel
        // appears) — the path whose absence (SettingsWindow never implementing INotifyPropertyChanged)
        // this harness caught on 2026-09-14.
        var window = new SettingsWindow(vm, session, config)
        {
            Width = 660,
            // Tall enough to reach the console card, whose header carries the two clear buttons.
            Height = 1280,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
        };

        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();

        DumpConsoleCardState(window, session);

        // Apply the appearance states LIVE, after Show(): each flips a SettingsViewModel property,
        // and the render only looks right if the INPC → Visibility bindings collapse the dependent
        // blocks at runtime. Setting them in the config would only prove the initial layout.
        if (wants("follow"))
            vm.ChromeButtonBackgroundFollowTheme = true;
        if (wants("never-start"))
            vm.ServiceStrategy = ServiceStrategy.NeverStart;
        if (wants("no-drag"))
            vm.DragStripEnabled = false;
        if (wants("follow") || wants("never-start") || wants("no-drag"))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
        }

        // Drive the session now that the window exists, so the temp-URL panel appears through the
        // live binding rather than through initial state.
        if (needsToken)
        {
            session.StartSessionAsync(config, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"[session] phase={session.Phase} error={session.ErrorMessage}");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
        }

        // "started"（本软件启动的实例）：走真实启动管线 → owned=true，验证「服务已启动」态。
        // "started401"：同一条管线，但后探测 401（回归 2026-09-24 的归属误判）。
        if (started || started401)
        {
            session.StartServiceAsync(config, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"[session] started running={session.ServiceRunning} owned={session.ServiceOwned}");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
            DumpConsoleCardState(window, session);
        }

        // Read the brush the ROOT GRID actually paints with, not what FindResource returns:
        // DynamicResource resolution on a live visual tree is what determines the pixels.
        var rootGrid = (Panel)window.Content;
        var actualBg = ((SolidColorBrush)rootGrid.Background).Color;
        Console.WriteLine($"[window] root.Background=#{actualBg.R:X2}{actualBg.G:X2}{actualBg.B:X2} "
            + $"expected=#{expectedWindowBg.R:X2}{expectedWindowBg.G:X2}{expectedWindowBg.B:X2}");

        if (actualBg != expectedWindowBg)
        {
            Console.Error.WriteLine(
                "[theme] VISUAL TREE HAS THE WRONG THEME: the dictionary is right but the rendered "
                + "window would come out in the other theme. Refusing to write a misleading PNG.");
            window.Close();
            app.Shutdown();
            return 2;
        }

        // Show the TOP of the scroll region, where the console card (with the URL field and the
        // temp-URL panel inside it) lives — scrolling elsewhere renders the region we are NOT
        // inspecting. This said ScrollToEnd before the reorder.
        // "scroll-end" 覆盖这个默认：把滚动区拉到底，用于检查**最后几张卡片**（外部链接在末尾）。
        if (FindScrollViewer(window) is { } scroller)
        {
            if (wants("scroll-end"))
                scroller.ScrollToEnd();
            else
                scroller.ScrollToTop();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }

        var root = (Visual)window.Content;
        var w = (int)Math.Ceiling(((FrameworkElement)root).ActualWidth);
        var h = (int)Math.Ceiling(((FrameworkElement)root).ActualHeight);
        if (w <= 0 || h <= 0)
        {
            Console.Error.WriteLine($"layout produced {w}x{h}");
            window.Close();
            app.Shutdown();
            return 1;
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outFile))
            encoder.Save(fs);

        window.Close();
        app.Shutdown();

        Console.WriteLine($"saved {outFile} ({w}x{h}){(dark ? " dark" : " light")}{(modes.Length > 0 ? " [" + string.Join(",", modes) + "]" : string.Empty)}");
        return 0;
    }

    /// <summary>Depth-first search for the settings body's ScrollViewer (unnamed; naming it would mean touching shipping XAML for a dev tool).</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>
    /// Prints the LIVE runtime state of the console card — the three service buttons'
    /// Visibility/IsEnabled, the status hint text/brush, and the data-clear buttons row —
    /// so the harness verifies the bindings (ServiceRunning/ServiceIdle/ServiceStatusText)
    /// actually drive the UI, not just that the XAML parses. The buttons and status TextBlock
    /// are unnamed in the shipping XAML, so walk the ConsoleArea visual tree by structure:
    /// Border(ConsoleArea) → StackPanel → [0] header Grid → Buttons; [1] status Border → TextBlock;
    /// [2] clear-buttons Grid → Buttons.
    /// </summary>
    private static void DumpConsoleCardState(Window window, SessionViewModel session)
    {
        try
        {
            if (window.FindName("ConsoleArea") is not Border console)
            {
                Console.WriteLine("[console] ConsoleArea not found");
                return;
            }

            var stack = console.Child as StackPanel;
            var header = stack?.Children.Count > 0 ? stack.Children[0] as Grid : null;
            var status = stack?.Children.Count > 1 ? stack.Children[1] as Border : null;

            var buttons = header?.Children.OfType<Button>().ToArray() ?? Array.Empty<Button>();
            var labels = buttons.Select(b => (b.Content as string) ?? string.Empty).ToArray();
            var vis = buttons.Select(b => b.Visibility).ToArray();
            var enabled = buttons.Select(b => b.IsEnabled).ToArray();
            Console.WriteLine($"[console] buttons=[{string.Join(", ", labels)}] "
                + $"vis=[{string.Join(", ", vis)}] enabled=[{string.Join(", ", enabled)}]");

            var text = ((status?.Child as TextBlock)?.Text) ?? "(none)";
            var brush = ((status?.Child as TextBlock)?.Foreground as SolidColorBrush)?.Color.ToString() ?? "(none)";
            Console.WriteLine($"[console] statusText=\"{text}\" color={brush} "
                + $"running={session.ServiceRunning} owned={session.ServiceOwned} changing={session.ServiceChanging}");

            // 第三行：数据清除按钮（清除 WebView 数据 / 清除 Cookie），应为常显两枚按钮。
            var clearRow = stack?.Children.Count > 2 ? stack.Children[2] as Grid : null;
            var clearButtons = clearRow?.Children.OfType<Button>().ToArray() ?? Array.Empty<Button>();
            var clearLabels = clearButtons.Select(b => (b.Content as string) ?? string.Empty).ToArray();
            var clearVis = clearButtons.Select(b => b.Visibility).ToArray();
            Console.WriteLine($"[console] clearButtons=[{string.Join(", ", clearLabels)}] "
                + $"vis=[{string.Join(", ", clearVis)}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[console] dump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders the real <see cref="MainWindow"/> offscreen to verify the startup loading overlay:
    /// <list type="bullet">
    /// <item>Phase A — a URL that HANGS (non-routable 10.255.255.1): the probe stays in flight for
    /// its 10s timeout, so the overlay must still be VISIBLE at capture time.</item>
    /// <item>Phase B — a refused URL (127.0.0.1:3080) with direct navigate: the navigation fails
    /// within milliseconds, so by ~3s the overlay must be GONE.</item>
    /// </list>
    /// Real infra is used because MainWindow wires it in its constructor; everything writes into a
    /// scratch dir deleted on the way out.
    /// </summary>
    private static int RenderMain(App app, string outFile)
    {
        // Surface otherwise-invisible failures: MainWindow catches init errors into run.log /
        // MessageBox (null / offscreen here), so a stuck init would look like "phase stays Idle".
        app.DispatcherUnhandledException += (_, args) =>
        {
            Console.WriteLine($"[dispatch-exception] {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.Handled = true;
        };

        var scratch = Path.Combine(AppContext.BaseDirectory, "main-preview-scratch");
        Directory.CreateDirectory(scratch);

        // ---- Phase A: hanging URL → overlay must be visible ----
        var configA = AppConfig.CreateDefault() with
        {
            DefaultUrl = "http://10.255.255.1:9/",
        };
        var (windowA, sessionA) = BuildMainWindow(configA, Path.Combine(scratch, "profile-a"));
        windowA.Show();
        PumpFor(windowA, TimeSpan.FromSeconds(2.5));
        var overlayA = FindLoadingOverlay(windowA);
        Console.WriteLine($"[main] A phase={sessionA.Phase} overlayVisible={overlayA?.Visibility}");
        // 层级约定：窗口控制按钮 > 拖动条 > 加载浮层 —— 加载中三键可点、窗口可拖。
        var chromeA = windowA.FindName("ChromeBar") as FrameworkElement;
        var stripA = windowA.FindName("DragStrip") as FrameworkElement;
        var chromeHostA = FindAncestorGrid(chromeA);
        var stripHostA = FindAncestorGrid(stripA);
        var zOk = overlayA is not null && chromeHostA is not null
            && Panel.GetZIndex(chromeHostA) > Panel.GetZIndex(overlayA)
            && (stripHostA is null || Panel.GetZIndex(stripHostA) > Panel.GetZIndex(overlayA));
        Console.WriteLine($"[main] A zorder chromeHost={chromeHostA?.GetValue(Panel.ZIndexProperty)} "
            + $"stripHost={stripHostA?.GetValue(Panel.ZIndexProperty)} overlay={overlayA?.GetValue(Panel.ZIndexProperty)}");
        SaveRender(windowA, outFile.Replace(".png", ".loading.png", StringComparison.OrdinalIgnoreCase));
        var visibleOk = overlayA is { Visibility: Visibility.Visible };
        windowA.Close();

        // ---- Phase B: refused URL + direct navigate → overlay must hide after nav completes ----
        var configB = AppConfig.CreateDefault() with
        {
            DefaultUrl = "http://127.0.0.1:3080/",
            ServiceStrategy = ServiceStrategy.NeverStart,
        };
        var (windowB, sessionB) = BuildMainWindow(configB, Path.Combine(scratch, "profile-b"));
        windowB.Show();
        // WebView2 browser-process teardown of window A can make B's init slow; wait until the
        // core is actually up (cap 15s) instead of a fixed nap.
        var swB = Stopwatch.StartNew();
        var webviewB = windowB.FindName("WebView") as Microsoft.Web.WebView2.Wpf.WebView2;
        while (swB.Elapsed < TimeSpan.FromSeconds(15) && webviewB?.CoreWebView2 is null)
        {
            windowB.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
        PumpFor(windowB, TimeSpan.FromSeconds(2.5));
        var overlayB = FindLoadingOverlay(windowB);
        Console.WriteLine($"[main] B diag IsLoaded={windowB.IsLoaded} core={(webviewB?.CoreWebView2 is null ? "null" : "ready")} "
            + $"phase={sessionB.Phase} overlayVisible={overlayB?.Visibility}");
        Console.WriteLine($"[main] B phase={sessionB.Phase} overlayVisible={overlayB?.Visibility}");
        SaveRender(windowB, outFile.Replace(".png", ".settled.png", StringComparison.OrdinalIgnoreCase));
        var hiddenOk = overlayB is { Visibility: Visibility.Collapsed };
        windowB.Close();

        app.Shutdown();

        // 清理 scratch（用户要求仓库不留临时物；WebView2 可能刚释放，删除失败就重试一次）
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
                break;
            }
            catch when (attempt == 0)
            {
                Thread.Sleep(500);
            }
            catch { }
        }

        Console.WriteLine(visibleOk && hiddenOk && zOk
            ? "[main] PASS overlay shows while loading, hides after navigation settles, chrome/drag strip above overlay"
            : "[main] FAIL " + (visibleOk ? string.Empty : "A(visible) ") + (hiddenOk ? string.Empty : "B(hidden) ")
                + (zOk ? string.Empty : "zorder "));
        return visibleOk && hiddenOk && zOk ? 0 : 4;
    }

    /// <summary>
    /// 真实 WebView2 端到端验证：页面里用 <b>DSH 前端一模一样的调用形态</b>
    /// （<c>window.open(url, "_blank", "noopener,noreferrer")</c>）开链接，断言
    /// ① 命中正则的链接被交给外部浏览器、② 本机链接仍留在 WebView2、
    /// ③ <c>Handled=true</c> 真的压住了 WebView2 自带弹窗、④ 关闭开关后完全不接管。
    /// <para>
    /// 这一步不可省：单测只证明判定函数对，证明不了 <c>NewWindowRequested</c> 在真实 WebView2 上
    /// 确实会为这个调用形态触发、且接管真的生效。
    /// </para>
    /// </summary>
    private static int RenderExternalLinksEndToEnd(App app, string outFile)
    {
        app.DispatcherUnhandledException += (_, args) =>
        {
            Console.WriteLine($"[e2e] dispatch-exception {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.Handled = true;
        };

        var scratch = Path.Combine(AppContext.BaseDirectory, "ext-links-scratch");
        Directory.CreateDirectory(scratch);

        var browser = new RecordingBrowser();
        var config = AppConfig.CreateDefault() with
        {
            DefaultUrl = "about:blank",
            ServiceStrategy = ServiceStrategy.NeverStart,
        };

        var (window, _) = BuildMainWindow(config, Path.Combine(scratch, "profile"), browser);
        window.Show();

        // 默认宿主是**合成版**：它不是 XAML 里声明的 `WebView`（那是 hwnd 版的退路），
        // 而是运行时 new 出来塞进 WebHost 的 WebView2CompositionControl —— 必须按类型找。
        var core = WaitForCore(window, () => FindHostCore(window), TimeSpan.FromSeconds(20));
        if (core is null)
        {
            Console.Error.WriteLine("[e2e] FAIL CoreWebView2 未就绪，无法验证");
            window.Close();
            app.Shutdown();
            return 6;
        }

        PumpFor(window, TimeSpan.FromSeconds(1.5));
        core.NavigateToString("<html><body>e2e</body></html>");
        PumpFor(window, TimeSpan.FromSeconds(2));

        // ① 异源域名：必须被交给外部浏览器，且**不得**多出 WebView2 弹窗。
        var before = CountWebViewTopLevelWindows();
        var scriptResult = RunScript(window, core,
            "window.open('https://github.com/deepseek-ai/deepseek-harness','_blank','noopener,noreferrer'); 'ok'");
        PumpFor(window, TimeSpan.FromSeconds(1.5));
        var after = CountWebViewTopLevelWindows();
        var noPopup = after <= before;
        Console.WriteLine($"[e2e] chromeWidgetWindows before={before} after={after} noPopup={noPopup}");

        // ② 本机：必须**不**接管
        RunScript(window, core,
            "window.open('http://127.0.0.1:3080/','_blank','noopener,noreferrer'); 'ok'");
        PumpFor(window, TimeSpan.FromSeconds(1.5));

        var opened = browser.Opened.ToArray();
        Console.WriteLine($"[e2e] scriptResult={scriptResult} openedCount={opened.Length} "
            + $"opened=[{string.Join(", ", opened)}]");

        var extOk = opened.Length == 1
            && opened[0] == "https://github.com/deepseek-ai/deepseek-harness";
        var localOk = !opened.Any(u => u.Contains("127.0.0.1"));

        SaveRender(window, outFile);
        window.Close();

        // ③ 对照组：关闭开关后必须完全不接管，且**应当**出现 WebView2 自带弹窗 ——
        // 这一组同时证明上面的 noPopup 检查有牙齿（不是恒真）。
        var disabledBrowser = new RecordingBrowser();
        var disabledConfig = config with { OpenExternalLinksEnabled = false };
        var (windowOff, _) = BuildMainWindow(
            disabledConfig, Path.Combine(scratch, "profile-off"), disabledBrowser);
        windowOff.Show();
        var coreOff = WaitForCore(windowOff, () => FindHostCore(windowOff), TimeSpan.FromSeconds(20));
        var offOk = false;
        var controlPopup = false;
        if (coreOff is not null)
        {
            PumpFor(windowOff, TimeSpan.FromSeconds(1.5));
            coreOff.NavigateToString("<html><body>off</body></html>");
            PumpFor(windowOff, TimeSpan.FromSeconds(1.5));
            var offBefore = CountWebViewTopLevelWindows();
            RunScript(windowOff, coreOff,
                "window.open('https://github.com/deepseek-ai/deepseek-harness','_blank','noopener,noreferrer'); 'ok'");
            PumpFor(windowOff, TimeSpan.FromSeconds(2.5));
            var offAfter = CountWebViewTopLevelWindows();
            offOk = disabledBrowser.Opened.Count == 0;
            controlPopup = offAfter > offBefore;
            Console.WriteLine($"[e2e] disabled openedCount={disabledBrowser.Opened.Count} "
                + $"chromeWidgetWindows {offBefore}->{offAfter} controlPopupAppeared={controlPopup}");
        }
        else
        {
            Console.Error.WriteLine("[e2e] 关闭态窗口 CoreWebView2 未就绪");
        }

        windowOff.Close();
        app.Shutdown();
        Cleanup(scratch);

        var allOk = extOk && localOk && offOk && noPopup && controlPopup;
        Console.WriteLine(allOk
            ? "[e2e] PASS 命中正则的链接交给外部浏览器（且未弹 WebView2 窗口），本机链接仍留在 WebView2，关闭开关后完全不接管"
            : "[e2e] FAIL " + (extOk ? string.Empty : "external-link-not-forwarded ")
                + (localOk ? string.Empty : "loopback-was-forwarded ")
                + (offOk ? string.Empty : "disabled-still-forwarded ")
                + (noPopup ? string.Empty : "webview-popup-was-not-suppressed ")
                + (controlPopup ? string.Empty : "control-group-shows-no-popup(check-has-no-teeth)"));
        return allOk ? 0 : 7;
    }

    /// <summary>
    /// 数可见的 <c>Chrome_WidgetWin_1</c> 顶层窗口（<b>不限进程</b>）。
    /// <para>
    /// 不能按本进程 pid 过滤：WebView2 的弹窗是**浏览器进程**（msedgewebview2.exe）创建的，
    /// 宿主进程的窗口集合里根本看不到它 —— 按 pid 过滤时对照组也数不出弹窗，
    /// 那个断言就毫无牙齿（实测 controlPopupAppeared=False）。
    /// </para>
    /// </summary>
    private static int CountWebViewTopLevelWindows()
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            var cls = new System.Text.StringBuilder(64);
            if (GetClassName(hwnd, cls, cls.Capacity) > 0
                && cls.ToString() == "Chrome_WidgetWin_1")
                count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    /// <summary>执行脚本并**边等边泵 Dispatcher**。</summary>
    /// <remarks>
    /// 绝不能用 <c>ExecuteScriptAsync(...).GetAwaiter().GetResult()</c>：本方法跑在 STA 主线程上，
    /// 而 WebView2 的续延要回到同一个线程的消息泵 —— 阻塞等待就是死锁（实测挂满 5 分钟无输出）。
    /// </remarks>
    private static string? RunScript(
        Window window, Microsoft.Web.WebView2.Core.CoreWebView2 core, string script)
    {
        var task = core.ExecuteScriptAsync(script);
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(20);
        }

        if (!task.IsCompleted)
        {
            Console.WriteLine("[e2e] 脚本执行超时（WebView2 未在 15s 内返回）");
            return null;
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>取当前生效宿主的 CoreWebView2：优先合成版，其次 hwnd 版（与 MainWindow.Core 同序）。</summary>
    private static Microsoft.Web.WebView2.Core.CoreWebView2? FindHostCore(Window window)
    {
        if (window.FindName("WebHost") is not Panel host)
            return null;

        foreach (var child in host.Children)
        {
            switch (child)
            {
                case Microsoft.Web.WebView2.Wpf.WebView2CompositionControl composition:
                    return composition.CoreWebView2;
                case Microsoft.Web.WebView2.Wpf.WebView2 hwnd:
                    return hwnd.CoreWebView2;
            }
        }

        return null;
    }

    /// <summary>轮询等待 CoreWebView2 就绪（WebView2 初始化是异步的，固定 sleep 不可靠）。</summary>
    private static Microsoft.Web.WebView2.Core.CoreWebView2? WaitForCore(
        Window window, Func<Microsoft.Web.WebView2.Core.CoreWebView2?> get, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var core = get();
            if (core is not null)
                return core;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }

        return get();
    }

    private static void Cleanup(string dir)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                break;
            }
            catch when (attempt == 0)
            {
                Thread.Sleep(500);
            }
            catch { }
        }
    }

    /// <summary>记录被交给"外部浏览器"的 URL；不真的启动浏览器（自动化里绝不能弹真窗口）。</summary>
    private sealed class RecordingBrowser : IExternalBrowser
    {
        public List<string> Opened { get; } = new();

        public string? Open(string url)
        {
            Opened.Add(url);
            return null;
        }
    }

    private static (MainWindow Window, SessionViewModel Session) BuildMainWindow(AppConfig config, string profileDir)
    {
        var configStore = new ConfigStore(Path.Combine(profileDir, "config.json"));
        var probe = new WebViewProbe();
        var launcher = new CommandLauncher();
        var shell = new ShellViewModel();
        shell.RestoreFrom(config);
        var session = new SessionViewModel(probe, launcher);
        var settings = new SettingsViewModel(config);
        session.LogLine += Console.WriteLine;

        var window = new MainWindow(config, shell, session, settings, configStore, probe,
            profileDir)
        {
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
        };
        return (window, session);
    }

    /// <summary>带可注入浏览器启动器的版本（e2e 用假实现，避免自动化里真的弹出浏览器窗口）。</summary>
    private static (MainWindow Window, SessionViewModel Session) BuildMainWindow(
        AppConfig config, string profileDir, IExternalBrowser browser)
    {
        var configStore = new ConfigStore(Path.Combine(profileDir, "config.json"));
        var probe = new WebViewProbe();
        var launcher = new CommandLauncher();
        var shell = new ShellViewModel();
        shell.RestoreFrom(config);
        var session = new SessionViewModel(probe, launcher);
        var settings = new SettingsViewModel(config);
        session.LogLine += Console.WriteLine;

        var window = new MainWindow(config, shell, session, settings, configStore, probe,
            profileDir, runLog: null, launcher: launcher, livenessProbe: null, externalBrowser: browser)
        {
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
        };
        return (window, session);
    }

    private static void PumpFor(Window window, TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }

    private static System.Windows.Controls.Grid? FindLoadingOverlay(Window window) =>
        window.FindName("LoadingOverlay") as System.Windows.Controls.Grid;

    private static System.Windows.Controls.Grid? FindAncestorGrid(FrameworkElement? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Grid grid)
            {
                return grid;
            }
            element = element.Parent as FrameworkElement;
        }
        return null;
    }

    private static void SaveRender(Window window, string path)
    {
        var root = (Visual)window.Content;
        var w = (int)Math.Ceiling(((FrameworkElement)root).ActualWidth);
        var h = (int)Math.Ceiling(((FrameworkElement)root).ActualHeight);
        if (w <= 0 || h <= 0)
        {
            Console.Error.WriteLine($"[main] layout produced {w}x{h}");
            return;
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Console.WriteLine($"[main] saved {path} ({w}x{h})");
    }

    /// <summary>Answers 401 so <c>ProbeThenStart</c> resolves to <c>OpenConfigForToken</c> and the session lands in <c>NeedsToken</c> — the state that makes the temp-URL panel visible.</summary>
    private sealed class UnauthorizedProbe : IEndpointProbe
    {
        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) =>
            Task.FromResult(new ProbeResult(ProbeOutcome.Other, 401, "Unknown"));
    }

    /// <summary>Answers 200 so the service appears RUNNING (external instance): exercises the「复用外部实例」状态（关闭服务隐藏、启动服务隐藏）。</summary>
    private sealed class OkProbe : IEndpointProbe
    {
        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) =>
            Task.FromResult(new ProbeResult(ProbeOutcome.Ok, 200, null));
    }

    /// <summary>按队列逐次返回探测结果：模拟「启动前不可达 → 启动后可达」的完整启动管线，验证「服务已启动」（owned=true）状态。</summary>
    private sealed class SequenceProbe : IEndpointProbe
    {
        private readonly Queue<ProbeResult> _queue;

        public SequenceProbe(params ProbeResult[] results) => _queue = new Queue<ProbeResult>(results);

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            var result = _queue.Count > 0 ? _queue.Dequeue() : new ProbeResult(ProbeOutcome.Ok, 200, null);
            return Task.FromResult(result);
        }
    }

    /// <summary>Never invoked: the harness only renders, it never starts a session.</summary>
    private sealed class NopProbe : IEndpointProbe
    {
        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) =>
            Task.FromResult(new ProbeResult(ProbeOutcome.Unreachable, null, null));
    }

    /// <summary>Never invoked, same reason.</summary>
    private sealed class NopLauncher : IServerLauncher
    {
        public Task<LaunchResult> LaunchAsync(AppConfig cfg, IProgress<string> output, CancellationToken ct) =>
            Task.FromResult(new LaunchResult(null, false, string.Empty));
    }

    /// <summary>返回一条 URL，模拟「我们拉起的服务抓到 URL」→ owned=true。</summary>
    private sealed class UrlLauncher : IServerLauncher
    {
        public Task<LaunchResult> LaunchAsync(AppConfig cfg, IProgress<string> output, CancellationToken ct) =>
            Task.FromResult(new LaunchResult("http://127.0.0.1:3080/", false, "listening"));
    }
}
