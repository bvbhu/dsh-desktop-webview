using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using DshDesktop.Application;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;

namespace DshDesktop.App;

/// <summary>
/// 设置窗口。版式：每张卡片 = 若干「内联字段名 + 灰色说明」行 + 全宽输入框，下沿是校验条与按钮行。
/// <para>
/// 卡片顺序：① URL → ② 控制台 → ③ 本地服务启动策略 → ④ 窗口控制按钮 → ⑤ 拖动层。
/// 控制台紧跟 URL，因为临时 URL 输入框在 NeedsToken 时必须立刻被看到。
/// </para>
/// <para>
/// 绑定分治：整窗 <c>DataContext</c> = <see cref="SettingsViewModel"/>；控制台卡片另指到窗口自身，
/// 它读 <see cref="SessionViewModel"/> 且要转回 UI 线程刷新（见 <see cref="OnSessionPropertyChanged"/>）。
/// </para>
/// <para>
/// **必须实现 <see cref="INotifyPropertyChanged"/>**：控制台卡片的绑定只认接口，只声明同名事件会静默空转。
/// </para>
/// <para>保存成功即关窗；关窗回退是空操作（保存路径已先 <c>AcceptSnapshot</c> 推进基线）。</para>
/// </summary>
public partial class SettingsWindow : System.Windows.Window, INotifyPropertyChanged
{
    /// <summary>「用户就贴在滚动起始处」的容差（px）。太小频繁打断，太大看不出跟随。</summary>
    private const double FollowThreshold = 48;

    private readonly SettingsViewModel _vm;
    private readonly SessionViewModel _session;
    private SessionPhase _lastPhase;

    /// <summary>点「保存」且校验通过时抛出；由 MainWindow 落盘并即时套用外观。</summary>
    public event Action<AppConfig>? ConfigSaved;

    /// <summary>点「清除 Cookie」时抛出；WebView2 归 Shell 层所有，窗口只发号施令。</summary>
    /// <remarks>旧的「保存并重启会话」按钮已移除，会话类字段改为下次启动时自然生效。</remarks>
    public event Action? ClearCookiesRequested;

    /// <summary>点「清除 WebView 数据」时抛出；真正的清理发生在 Shell 层。</summary>
    public event Action? ClearWebViewDataRequested;

    /// <summary>用户提交临时 URL 时抛出；Shell 层据此等待导航完成，成功后清除错误并关窗 —— 导航是否成功只有 WebView2 知道。</summary>
    public event Action? TempUrlSubmitted;

    public SettingsWindow(SettingsViewModel vm, SessionViewModel session)
    {
        InitializeComponent();
        // 版本号在这里露出：主窗口标题栏保持纯标题，两种宿主模式下都不带版本后缀。
        Title = $"设置 - DSH Desktop Webview {AppVersion.Short}";
        _vm = vm;
        _session = session;
        _lastPhase = session.Phase;

        DataContext = _vm;
        ConsoleArea.DataContext = this;

        _session.PropertyChanged += OnSessionPropertyChanged;
        Closed += (_, _) =>
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            // 关窗即丢弃未保存的编辑；不回退的话下次打开会看到「改了但没保存」的幽灵字段。
            _vm.RevertToSnapshot();
        };
    }

    // ================= 错误条 / 控制台的数据源（其 DataContext = 本窗口） =================

    /// <summary>无错误时返回 null，标题行里的内联错误整段缩起。</summary>
    public string? ErrorText => string.IsNullOrEmpty(_session.ErrorMessage) ? null : _session.ErrorMessage;

    public string ConsoleText =>
        string.IsNullOrEmpty(_session.ConsoleOutput) ? "（暂无输出）" : _session.ConsoleOutput;

    public Visibility NeedsTokenVisibility =>
        _session.Phase == SessionPhase.NeedsToken ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseBinding(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>会话状态可能由后台线程推进（启动命令的输出在别的线程追加），统一转回 UI 线程再刷新绑定 —— TextBox 不接受跨线程通知。</summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RaiseBinding(nameof(ErrorText));
            RaiseBinding(nameof(ConsoleText));
            RaiseBinding(nameof(NeedsTokenVisibility));

            // 刚进入 NeedsToken：把控制台（临时 URL 输入框在里面）带到眼前。
            // 控制台整栏在滚动区最前面，所以是 ScrollToTop —— 它在最下面时要写 ScrollToEnd。
            if (_session.Phase == SessionPhase.NeedsToken && _lastPhase != SessionPhase.NeedsToken)
                ContentScroll.ScrollToTop();

            _lastPhase = _session.Phase;

            FollowConsole();
        });
    }

    /// <summary>控制台「跟随滚动」：只在用户本来就贴着顶部时保持贴顶，否则会把人正在改的设置拽走。</summary>
    private void FollowConsole()
    {
        ContentScroll.UpdateLayout(); // 先让新增长度生效，否则 ScrollableHeight 还是旧值
        if (ContentScroll.VerticalOffset <= FollowThreshold)
            ContentScroll.ScrollToTop();
    }

    // ================= 操作 =================

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = TryBuildValidated();
        if (cfg is null)
            return;

        // 先推进基线再抛事件：否则之后点「取消」会回退到启动时的旧值。
        _vm.AcceptSnapshot(cfg);
        ConfigSaved?.Invoke(cfg);

        MessageBox.Show(this,
            "已保存。\n" + "窗口控制按钮和拖动层立即生效，其余重启后生效。",
            "设置", MessageBoxButton.OK, MessageBoxImage.Information);

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.RevertToSnapshot();
        Close();
    }

    private void OnResetAppearance(object sender, RoutedEventArgs e)
        // 走属性 setter，INPC 会把改动刷回界面，无需手工 Raise。
        => _vm.ResetAppearance();

    private void OnClearCookies(object sender, RoutedEventArgs e) =>
        ClearCookiesRequested?.Invoke();

    private void OnClearWebViewData(object sender, RoutedEventArgs e) =>
        ClearWebViewDataRequested?.Invoke();

    /// <summary>控制台高度捏手：WPF 的 TextBox 自身没有缩放能力，用一个贴底的 Thumb 手改 Height；上限取窗口可用高度，太矮时先夹一次保证区间有效。</summary>
    private void OnConsoleGripDrag(object sender, DragDeltaEventArgs e)
    {
        var min = ConsoleBox.MinHeight;
        var max = Math.Max(min, ContentScroll.ActualHeight - 40);
        ConsoleBox.Height = Math.Clamp(ConsoleBox.Height + e.VerticalChange, min, max);
    }

    private void OnProvideTempUrl(object sender, RoutedEventArgs e)
    {
        var url = TempUrlInput.Text;
        if (string.IsNullOrWhiteSpace(url))
            return;

        _session.ProvideTempUrl(url.Trim());
        TempUrlInput.Text = string.Empty;
        TempUrlSubmitted?.Invoke();
    }

    /// <summary>校验通过则返回待保存配置，否则 null；校验条由 ValidationError 自己的 INPC 刷新。</summary>
    private AppConfig? TryBuildValidated() =>
        _vm.Validate() ? _vm.BuildConfig() : null;
}
