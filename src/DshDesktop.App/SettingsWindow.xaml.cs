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
/// 卡片顺序（自上而下）：① URL → ② 控制台 → ③ 本地服务启动策略 → ④ 窗口控制按钮 → ⑤ 拖动层。
/// URL 独立成卡置最前；控制台紧随其后，因为它是"现在发生了什么"的唯一出口
/// （临时 URL 输入框在 NeedsToken 时必须立刻被看到）。
/// </para>
/// <para>
/// 绑定分治：整窗 <c>DataContext</c> = <see cref="SettingsViewModel"/>，编辑区直接绑它的属性；
/// 控制台卡片另外把 <c>DataContext</c> 指到窗口自身 —— 它读的是 <see cref="SessionViewModel"/>
/// （错误内联在标题行、临时 URL 面板、控制台输出），且要转回 UI 线程才能刷新
/// （见 <see cref="OnSessionPropertyChanged"/>）。
/// </para>
/// <para>
/// **窗口必须实现 <see cref="INotifyPropertyChanged"/>**：控制台卡片绑的是窗口自己的 CLR 属性
/// （<see cref="ErrorText"/> 等），靠 <see cref="RaiseBinding"/> 触发通知刷新。只声明同名事件
/// 而不实现接口是无效的 —— WPF 绑定只认接口，通知会静默空转。
/// </para>
/// <para>保存成功即关窗；关窗回退是空操作（保存路径已先 <c>AcceptSnapshot</c> 推进基线）。</para>
/// </summary>
public partial class SettingsWindow : System.Windows.Window, INotifyPropertyChanged
{
    /// <summary>
    /// "用户就贴在滚动起始处"的容差（px）。太小会频繁打断，太大就看不出跟随。
    /// 控制台置顶后跟随的方向是"贴顶"，见 <see cref="FollowConsole"/>。
    /// </summary>
    private const double FollowThreshold = 48;

    private readonly SettingsViewModel _vm;
    private readonly SessionViewModel _session;
    private SessionPhase _lastPhase;

    /// <summary>点「保存」且校验通过时抛出；由 MainWindow 落盘并即时套用外观。</summary>
    public event Action<AppConfig>? ConfigSaved;

    /// <summary>点「清除 Cookie」时抛出。WebView2 归 Shell 层所有，窗口只发号施令、不自己去碰它。</summary>
    /// <remarks>
    /// 已移除「保存并重启会话」按钮及其 <c>SessionRestartRequested</c> 事件：
    /// 会话类字段（策略 / 命令 / 正则 / 工作目录）改为下次启动时自然生效。
    /// </remarks>
    public event Action? ClearCookiesRequested;

    /// <summary>点「清除 WebView 数据」时抛出。同上，真正的清理发生在 Shell 层。</summary>
    public event Action? ClearWebViewDataRequested;

    /// <summary>
    /// 用户提交了临时 URL 时抛出。Shell 层据此进入"等待导航完成"状态：
    /// 页面真正打开（NavigationCompleted 成功）后清除错误提示并关闭本窗口。
    /// 导航是否成功只有 WebView2 知道，窗口自己无从判断。
    /// </summary>
    public event Action? TempUrlSubmitted;

    public SettingsWindow(SettingsViewModel vm, SessionViewModel session)
    {
        InitializeComponent();
        _vm = vm;
        _session = session;
        _lastPhase = session.Phase;

        DataContext = _vm;
        ConsoleArea.DataContext = this;

        _session.PropertyChanged += OnSessionPropertyChanged;
        Closed += (_, _) =>
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            // 关窗即丢弃未保存的编辑。绑定直接改的是共享的 SettingsViewModel，
            // 不回退的话下次打开会看到"改了但没保存"的幽灵字段。
            // 已保存过的情况字段本就等于基线，这里是空操作。
            _vm.RevertToSnapshot();
        };
    }

    // ================= 错误条 / 控制台的数据源（它们的 DataContext = 本窗口） =================

    /// <summary>无错误时返回 null，标题行里的内联错误整段缩起（NotNullToVisibilityConverter）。</summary>
    public string? ErrorText => string.IsNullOrEmpty(_session.ErrorMessage) ? null : _session.ErrorMessage;

    public string ConsoleText =>
        string.IsNullOrEmpty(_session.ConsoleOutput) ? "（暂无输出）" : _session.ConsoleOutput;

    public Visibility NeedsTokenVisibility =>
        _session.Phase == SessionPhase.NeedsToken ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseBinding(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 会话状态可能由后台线程推进（启动命令的输出是在别的线程上追加的），
    /// 所以统一转回 UI 线程再刷新绑定 —— TextBox 的绑定不接受跨线程通知。
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RaiseBinding(nameof(ErrorText));
            RaiseBinding(nameof(ConsoleText));
            RaiseBinding(nameof(NeedsTokenVisibility));

            // 刚进入 NeedsToken：把控制台（临时 URL 输入框在里面）带到眼前，
            // 那一刻用户必须看到它，否则窗口开着却不知道要干什么。
            // 控制台整栏已移到滚动区最前面，所以是 ScrollToTop —— 此前它在最下面时这里写的是
            // ScrollToEnd，版式一改就得跟着改，否则会往反方向滚（表现为"输入框明明在却看不见"）。
            if (_session.Phase == SessionPhase.NeedsToken && _lastPhase != SessionPhase.NeedsToken)
                ContentScroll.ScrollToTop();

            _lastPhase = _session.Phase;

            FollowConsole();
        });
    }

    /// <summary>
    /// 控制台"跟随滚动"：控制台在滚动区最前面，控制台变长会把下方内容往下推。
    /// <para>
    /// 只在用户本来就贴着**顶部**时保持贴顶；否则会在别人正改下面设置时把页面拽回去。
    /// 这条以前是"贴底才跟"（控制台在最下面时），版式调整后方向随之反转。
    /// </para>
    /// </summary>
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
            "已保存，外观项立即生效。\n会话类字段（策略 / 命令 / 正则 / 工作目录）下次启动时生效。",
            "设置", MessageBoxButton.OK, MessageBoxImage.Information);

        // 提示框关掉之后关窗：保存完就不必再占着屏幕（用户要求）。
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

    /// <summary>
    /// 控制台高度捏手。WPF 的 TextBox 自己没有缩放能力（<c>ResizeMode</c> 是 Window 的属性），
    /// 所以用一个贴底的 <c>Thumb</c> 手动改 <see cref="ConsoleBox"/> 的 Height。
    /// <para>
    /// 下限取控件的 MinHeight（120），上限取窗口可用高度 —— 不设固定上限，
    /// 否则用户想拖大时会被顶回来。窗口太矮时上限可能小于下限，故先夹一次保证区间有效。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 校验通过则返回待保存配置，否则返回 null。
    /// 校验条绑的是 <c>SettingsViewModel.ValidationError</c>，由它自己的 INPC 刷新，这里不用管。
    /// </summary>
    private AppConfig? TryBuildValidated() =>
        _vm.Validate() ? _vm.BuildConfig() : null;
}
