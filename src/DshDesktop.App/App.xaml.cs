using System.IO;
using System.Windows;
using DshDesktop.Application;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;

namespace DshDesktop.App;

public partial class App : System.Windows.Application
{
    private RunLog? _runLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 离屏渲染预览（tools/settings-preview）复用真实 App 类但不 Run()。
        // 关键事实：Application 构造时就把启动操作 post 到了 Dispatcher —— 不调用 Run()
        // 它照样会执行，只是被推迟到"第一次泵"（Show 之后的 Invoke、模态框、异步续延……）。
        // 预览进程只想要 App.xaml 的样式与主题：跳过 run.log / 配置 / 主窗口，否则
        // ① 每次渲染都会真的拉起主窗口并初始化 WebView2；
        // ② ApplyFromSystem 按系统明暗覆盖主题，深色预览会被本机的浅色系统设置悄悄改掉
        //   （浅色机器上"恰好"看不出问题——这正是深色渲染核对长期以来失败的原因）。
        // 环境变量由 harness 在 new App() 之前设置。
        if (Environment.GetEnvironmentVariable("DSH_DESKTOP_PREVIEW") == "1")
        {
            ThemeSwitcher.ApplyFromSystem(); // 与正式路径一致：先按系统明暗装配一次主题
            return;
        }

        // 只发便携版：数据全部落在程序目录内（见 DataPaths 的注释）。
        var root = DataPaths.Root;
        var writable = DataPaths.IsWritable(root);

        // 建好程序目录内的 temp\，但**不再**设进程级的 TMP/TEMP。
        // 旧做法会被继承给本进程拉起的每一个子进程（整个 WebView2 浏览器进程树、
        // 用户在页面里跑的 dsh 及其派生的一切），后果见 DataPaths.TempEnvironmentVariableNames
        // 的注释：DSH 自己的沙箱要求临时根在工作区之外，撞上就整条命令拒绝执行。
        // 现在改为只对我们主动拉起的服务进程注入（见 CommandLauncher）。
        try
        {
            Directory.CreateDirectory(DataPaths.TempDir);
        }
        catch
        {
            // 建不出来就维持系统默认；下面那条"不可写"提示已经把该说的话说了
        }

        _runLog = new RunLog(DataPaths.RunLogFile);
        _runLog.Append("==== DSH-Desktop-Webview 启动 ====");
        _runLog.Append($"[paths] 模式=portable 数据目录={root} 可写={writable} temp={DataPaths.TempDir}");
        _runLog.Append($"[paths] 本进程 TMP/TEMP 未改写（子进程按需注入）: TMP={Environment.GetEnvironmentVariable("TMP")} TEMP={Environment.GetEnvironmentVariable("TEMP")}");

        if (!writable)
        {
            // 不做"悄悄改写到用户目录"的回落：那会破坏便携承诺（见 DataPaths 的注释）。
            // 但也不能装作没事 —— 用户必须知道"配置存不下来"，而不是过一会儿发现没生效。
            _runLog.Append("[paths] 数据目录不可写：配置与日志都会保存失败");
            MessageBox.Show(
                $"程序目录不可写，设置无法保存：\n{root}\n\n" +
                "请把整个文件夹移动到可写的位置（例如桌面或 D:\\）后重新启动。\n" +
                "本程序不会把数据写到程序目录之外。",
                "DSH Desktop Webview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // §7.2「跟随主题」：读系统外观设置，替换 App.xaml 合并的主题字典。
        // 必须早于 MainWindow 构造 —— ChromeBar 在构造期就用 SetResourceReference 解析图标色。
        var (theme, appsUseLightTheme) = ThemeSwitcher.ApplyFromSystem();
        var rawThemeValue = appsUseLightTheme?.ToString() ?? "missing";
        _runLog.Append($"[theme] 主题={theme}（AppsUseLightTheme={rawThemeValue}）");

        // 任何未处理异常都必须落进 run.log。
        // 此前 Loaded 处理器是 async void，初始化一抛异常就表现为"运行 exe 什么也不显示"，
        // 而日志里没有任何线索 —— 这类静默失败必须根除。
        DispatcherUnhandledException += (_, args) =>
        {
            _runLog?.Append($"[fatal] Dispatcher 未处理异常：{args.Exception}");
            MessageBox.Show(
                $"发生未处理异常：{args.Exception.Message}",
                "DSH Desktop Webview", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _runLog?.Append($"[fatal] 未处理异常：{args.ExceptionObject}");

        var configStore = new ConfigStore(DataPaths.ConfigFile);
        AppConfig config;
        try
        {
            config = configStore.Load();
            _runLog.Append($"[config] 已加载 {configStore.ConfigPath}");
        }
        catch (Exception ex)
        {
            _runLog.Append($"[config] 加载失败，回退默认配置：{ex.GetType().Name}: {ex.Message}");
            config = AppConfig.CreateDefault();
        }

        var probe = new WebViewProbe();
        var launcher = new CommandLauncher();
        var shell = new ShellViewModel();
        var session = new SessionViewModel(probe, launcher);
        var settings = new SettingsViewModel(config);

        session.LogLine += line => _runLog?.Append(line);

        shell.RestoreFrom(config);

        var mainWindow = new MainWindow(
            config, shell, session, settings, configStore, probe,
            DataPaths.ProfileDir, _runLog);
        MainWindow = mainWindow;
        mainWindow.Closed += (_, _) => Shutdown();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runLog?.Append($"==== 退出 exitCode={e.ApplicationExitCode} ====");
        base.OnExit(e);
    }
}
